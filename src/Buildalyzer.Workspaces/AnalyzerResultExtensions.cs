using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Buildalyzer.Workspaces;

public static class AnalyzerResultExtensions
{
    /// <summary>
    /// Gets a Roslyn workspace for the analyzed results.
    /// </summary>
    /// <param name="analyzerResult">The results from building a Buildalyzer project analyzer.</param>
    /// <param name="addProjectReferences">
    /// <c>true</c> to add projects to the workspace for project references that exist in the same <see cref="AnalyzerManager"/>.
    /// If <c>true</c> this will trigger (re)building all referenced projects. Directly add <see cref="AnalyzerResult"/> instances instead if you already have them available.
    /// </param>
    /// <returns>A Roslyn workspace.</returns>
    public static AdhocWorkspace GetWorkspace(this IAnalyzerResult analyzerResult, bool addProjectReferences = false)
    {
        if (analyzerResult == null)
        {
            throw new ArgumentNullException(nameof(analyzerResult));
        }
        AdhocWorkspace workspace = analyzerResult.Manager.CreateWorkspace();
        analyzerResult.AddToWorkspace(workspace, addProjectReferences);
        return workspace;
    }

    /// <summary>
    /// Adds a result to an existing Roslyn workspace.
    /// </summary>
    /// <param name="analyzerResult">The results from building a Buildalyzer project analyzer.</param>
    /// <param name="workspace">A Roslyn workspace.</param>
    /// <param name="addProjectReferences">
    /// <c>true</c> to add projects to the workspace for project references that exist in the same <see cref="AnalyzerManager"/>.
    /// If <c>true</c> this will trigger (re)building all referenced projects. Directly add <see cref="AnalyzerResult"/> instances instead if you already have them available.
    /// </param>
    /// <returns>The newly added Roslyn project, or <c>null</c> if the project couldn't be added to the workspace.</returns>
    public static Project AddToWorkspace(this IAnalyzerResult analyzerResult, Workspace workspace, bool addProjectReferences = false)
    {
        if (analyzerResult == null)
        {
            throw new ArgumentNullException(nameof(analyzerResult));
        }
        if (workspace == null)
        {
            throw new ArgumentNullException(nameof(workspace));
        }

        // Get or create an ID for this project
        ProjectId projectId = ProjectId.CreateFromSerialized(analyzerResult.ProjectGuid);

        // Cache the project references
        analyzerResult.Manager.WorkspaceProjectReferences[projectId.Id] = [.. analyzerResult.ProjectReferences];

        // Create and add the project, but only if it's a support Roslyn project type
        ProjectInfo projectInfo = GetProjectInfo(analyzerResult, workspace, projectId);
        if (projectInfo is null)
        {
            // Something went wrong (maybe not a support project type), so don't add this project
            return null;
        }
        Solution solution = workspace.CurrentSolution.AddProject(projectInfo);

        // Check if this project is referenced by any other projects in the workspace
        foreach (Project existingProject in solution.Projects.ToArray())
        {
            if (!existingProject.Id.Equals(projectId)
                && analyzerResult.Manager.WorkspaceProjectReferences.TryGetValue(existingProject.Id.Id, out string[] existingReferences)
                && existingReferences.Contains(analyzerResult.ProjectFilePath))
            {
                // Add the reference to the existing project
                ProjectReference projectReference = new ProjectReference(projectId);
                solution = solution.AddProjectReference(existingProject.Id, projectReference);
            }
        }

        // Apply solution changes
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Could not apply workspace solution changes");
        }

        // Add any project references not already added
        if (addProjectReferences)
        {
            foreach (ProjectAnalyzer referencedAnalyzer in GetReferencedAnalyzerProjects(analyzerResult))
            {
                // Check if the workspace contains the project inside the loop since adding one might also add this one due to transitive references
                if (!workspace.CurrentSolution.Projects.Any(x => x.FilePath == referencedAnalyzer.ProjectFile.Path))
                {
                    referencedAnalyzer.AddToWorkspace(workspace, addProjectReferences);
                }
            }
        }

        // By now all the references of this project have been recursively added, so resolve any remaining transitive project references
        Project project = workspace.CurrentSolution.GetProject(projectId);
        HashSet<ProjectReference> referencedProjects = [.. project.ProjectReferences];
        HashSet<ProjectId> visitedProjectIds = [];
        Stack<ProjectReference> projectReferenceStack = new Stack<ProjectReference>(project.ProjectReferences);
        while (projectReferenceStack.Count > 0)
        {
            ProjectReference projectReference = projectReferenceStack.Pop();
            Project nestedProject = workspace.CurrentSolution.GetProject(projectReference.ProjectId);
            if (nestedProject is not null && visitedProjectIds.Add(nestedProject.Id))
            {
                foreach (ProjectReference nestedProjectReference in nestedProject.ProjectReferences)
                {
                    projectReferenceStack.Push(nestedProjectReference);
                    referencedProjects.Add(nestedProjectReference);
                }
            }
        }
        foreach (ProjectReference referencedProject in referencedProjects)
        {
            if (!project.ProjectReferences.Contains(referencedProject))
            {
                ProjectReference projectReference = new ProjectReference(referencedProject.ProjectId);
                solution = workspace.CurrentSolution.AddProjectReference(project.Id, projectReference);
                if (!workspace.TryApplyChanges(solution))
                {
                    throw new InvalidOperationException("Could not apply workspace solution changes");
                }
            }
        }

        // Find and return this project
        return workspace.CurrentSolution.GetProject(projectId);
    }

    private static ProjectInfo GetProjectInfo(IAnalyzerResult analyzerResult, Workspace workspace, ProjectId projectId)
    {
        string projectName = Path.GetFileNameWithoutExtension(analyzerResult.ProjectFilePath);
        if (!TryGetSupportedLanguageName(analyzerResult.ProjectFilePath, out string languageName))
        {
            return null;
        }
        return ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            projectName,
            projectName,
            languageName,
            filePath: analyzerResult.ProjectFilePath,
            outputFilePath: analyzerResult.GetProperty("TargetPath"),
            documents: GetDocuments(analyzerResult, projectId),
            projectReferences: GetExistingProjectReferences(analyzerResult, workspace),
            metadataReferences: GetMetadataReferences(analyzerResult),
            analyzerReferences: GetAnalyzerReferences(analyzerResult, workspace),
            additionalDocuments: GetAdditionalDocuments(analyzerResult, projectId),
            parseOptions: CreateParseOptions(analyzerResult, languageName),
            compilationOptions: CreateCompilationOptions(analyzerResult, languageName));
    }

    private static ParseOptions CreateParseOptions(IAnalyzerResult analyzerResult, string languageName)
    {
        // language-specific code is in local functions, to prevent assembly loading failures when assembly for the other language is not available
        if (languageName == LanguageNames.CSharp)
        {
            ParseOptions CreateCSharpParseOptions()
            {
                CSharpParseOptions parseOptions = new CSharpParseOptions();

                // Add any constants
                parseOptions = parseOptions.WithPreprocessorSymbols(analyzerResult.PreprocessorSymbols);

                // Get language version
                string langVersion = analyzerResult.GetProperty("LangVersion");
                if (!string.IsNullOrWhiteSpace(langVersion)
                    && Microsoft.CodeAnalysis.CSharp.LanguageVersionFacts.TryParse(langVersion, out Microsoft.CodeAnalysis.CSharp.LanguageVersion languageVersion))
                {
                    parseOptions = parseOptions.WithLanguageVersion(languageVersion);
                }

                return parseOptions;
            }

            return CreateCSharpParseOptions();
        }

        if (languageName == LanguageNames.VisualBasic)
        {
            ParseOptions CreateVBParseOptions()
            {
                VisualBasicParseOptions parseOptions = new VisualBasicParseOptions();

                // Get language version
                string langVersion = analyzerResult.GetProperty("LangVersion");
                Microsoft.CodeAnalysis.VisualBasic.LanguageVersion languageVersion = Microsoft.CodeAnalysis.VisualBasic.LanguageVersion.Default;
                if (!string.IsNullOrWhiteSpace(langVersion)
                    && Microsoft.CodeAnalysis.VisualBasic.LanguageVersionFacts.TryParse(langVersion, ref languageVersion))
                {
                    parseOptions = parseOptions.WithLanguageVersion(languageVersion);
                }

                return parseOptions;
            }

            return CreateVBParseOptions();
        }

        return null;
    }

    private static CompilationOptions CreateCompilationOptions(IAnalyzerResult analyzerResult, string languageName)
    {
        string outputType = analyzerResult.GetProperty("OutputType");
        OutputKind? kind = null;
        switch (outputType)
        {
            case "Library":
                kind = OutputKind.DynamicallyLinkedLibrary;
                break;
            case "Exe":
                kind = OutputKind.ConsoleApplication;
                break;
            case "Module":
                kind = OutputKind.NetModule;
                break;
            case "Winexe":
                kind = OutputKind.WindowsApplication;
                break;
        }

        if (kind.HasValue)
        {
            // language-specific code is in local functions, to prevent assembly loading failures when assembly for the other language is not available
            if (languageName == LanguageNames.CSharp)
            {
                Enum.TryParse(analyzerResult.GetProperty("Nullable"), ignoreCase: true, out NullableContextOptions nullable);

                CompilationOptions CreateCSharpCompilationOptions() => new CSharpCompilationOptions(kind.Value, nullableContextOptions: nullable);

                return CreateCSharpCompilationOptions();
            }

            if (languageName == LanguageNames.VisualBasic)
            {
                CompilationOptions CreateVBCompilationOptions() => new VisualBasicCompilationOptions(kind.Value);

                return CreateVBCompilationOptions();
            }
        }

        return null;
    }

    private static IEnumerable<ProjectReference> GetExistingProjectReferences(IAnalyzerResult analyzerResult, Workspace workspace) =>
        analyzerResult.ProjectReferences
            .Select(x => workspace.CurrentSolution.Projects.FirstOrDefault(y => y.FilePath.Equals(x, StringComparison.OrdinalIgnoreCase)))
            .Where(x => x != null)
            .Select(x => new ProjectReference(x.Id))
            ?? [];

    private static IEnumerable<IProjectAnalyzer> GetReferencedAnalyzerProjects(IAnalyzerResult analyzerResult) =>
        analyzerResult.ProjectReferences
            .Select(x => analyzerResult.Manager.Projects.TryGetValue(x, out IProjectAnalyzer a) ? a : analyzerResult.Manager.GetProject(x))
            .Where(x => x != null)
            ?? [];

    private static IEnumerable<DocumentInfo> GetDocuments(IAnalyzerResult analyzerResult, ProjectId projectId)
    {
        string[] sourceFiles = analyzerResult.SourceFiles ?? [];
        return GetDocuments(sourceFiles, projectId);
    }

    private static IEnumerable<DocumentInfo> GetDocuments(IEnumerable<string> files, ProjectId projectId) =>
       files.Where(File.Exists)
           .Select(x => DocumentInfo.Create(
               DocumentId.CreateNewId(projectId),
               Path.GetFileName(x),
               loader: TextLoader.From(
                   TextAndVersion.Create(
                       SourceText.From(File.ReadAllText(x), Encoding.Unicode), VersionStamp.Create())),
               filePath: x));

    private static IEnumerable<DocumentInfo> GetAdditionalDocuments(IAnalyzerResult analyzerResult, ProjectId projectId)
    {
        string projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        string[] additionalFiles = analyzerResult.AdditionalFiles ?? [];
        return GetDocuments(additionalFiles.Select(x => Path.Combine(projectDirectory!, x)), projectId);
    }

    private static IEnumerable<MetadataReference> GetMetadataReferences(IAnalyzerResult analyzerResult) =>
        analyzerResult
            .References?.Where(File.Exists)
            .Select(x => MetadataReference.CreateFromFile(x, new MetadataReferenceProperties(aliases: analyzerResult.ReferenceAliases.GetValueOrDefault(x))))
            ?? [];

    private static IEnumerable<AnalyzerReference> GetAnalyzerReferences(IAnalyzerResult analyzerResult, Workspace workspace)
    {
        IAnalyzerAssemblyLoader loader = workspace.Services.GetRequiredService<IAnalyzerService>().GetLoader();

        string projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        return analyzerResult.AnalyzerReferences?.Where(x => File.Exists(Path.GetFullPath(x, projectDirectory!)))
            .Select(x => new AnalyzerFileReference(Path.GetFullPath(x, projectDirectory!), loader))
            ?? [];
    }

    private static bool TryGetSupportedLanguageName(string projectPath, out string languageName)
    {
        switch (Path.GetExtension(projectPath))
        {
            case ".csproj":
                languageName = LanguageNames.CSharp;
                return true;
            case ".vbproj":
                languageName = LanguageNames.VisualBasic;
                return true;
            default:
                languageName = null;
                return false;
        }
    }
}