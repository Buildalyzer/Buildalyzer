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
        Guard.NotNull(analyzerResult);
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
        Guard.NotNull(analyzerResult);
        Guard.NotNull(workspace);

        // Get or create an ID for this project
        ProjectId projectId = ProjectId.CreateFromSerialized(analyzerResult.ProjectGuid);

        // Cache the project references
        analyzerResult.Manager.WorkspaceProjectReferences[projectId.Id] = [.. analyzerResult.ProjectReferences];

        // Create and add the project, but only if it's a support Roslyn project type
        Microsoft.CodeAnalysis.ProjectInfo projectInfo = GetProjectInfo(analyzerResult, workspace, projectId);
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
            foreach (var referencedAnalyzer in GetReferencedAnalyzerProjects(analyzerResult))
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

    private static Microsoft.CodeAnalysis.ProjectInfo? GetProjectInfo(IAnalyzerResult analyzerResult, Workspace workspace, ProjectId projectId)
    {
        string projectName = Path.GetFileNameWithoutExtension(analyzerResult.ProjectFilePath);
        return TryGetSupportedLanguageName(analyzerResult.ProjectFilePath, out string languageName)
            ? Microsoft.CodeAnalysis.ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                projectName,
                projectName,
                languageName,
                filePath: analyzerResult.ProjectFilePath,
                outputFilePath: analyzerResult.GetProperty("TargetPath"),
                compilationOptions: CreateCompilationOptions(analyzerResult, languageName),
                parseOptions: CreateParseOptions(analyzerResult, languageName),
                documents: GetDocuments(analyzerResult, projectId),
                projectReferences: GetExistingProjectReferences(analyzerResult, workspace),
                metadataReferences: GetMetadataReferences(analyzerResult),
                analyzerReferences: GetAnalyzerReferences(analyzerResult, workspace),
                additionalDocuments: GetAdditionalDocuments(analyzerResult, projectId))
            : null;
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
                parseOptions = parseOptions.WithPreprocessorSymbols(GetPreprocessorSymbols(analyzerResult));

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

                CompilationOptions CreateCSharpCompilationOptions()
                {
                    CSharpCompilationOptions opts = new CSharpCompilationOptions(kind.Value, nullableContextOptions: nullable);

                    if (bool.TryParse(analyzerResult.GetProperty("AllowUnsafeBlocks"), out bool allowUnsafe))
                    {
                        opts = opts.WithAllowUnsafe(allowUnsafe);
                    }

                    if (bool.TryParse(analyzerResult.GetProperty("CheckForOverflowUnderflow"), out bool checkOverflow))
                    {
                        opts = opts.WithOverflowChecks(checkOverflow);
                    }

                    if (bool.TryParse(analyzerResult.GetProperty("Deterministic"), out bool deterministic))
                    {
                        opts = opts.WithDeterministic(deterministic);
                    }

                    // PlatformTarget is the per-project compiler target; Platform from MSBuild is the
                    // solution platform (typically "AnyCPU"). Prefer PlatformTarget, fall back to Platform.
                    string platform = analyzerResult.GetProperty("PlatformTarget")
                        ?? analyzerResult.GetProperty("Platform");
                    if (!string.IsNullOrWhiteSpace(platform)
                        && Enum.TryParse(platform, ignoreCase: true, out Platform platformValue))
                    {
                        opts = opts.WithPlatform(platformValue);
                    }

                    if (int.TryParse(analyzerResult.GetProperty("WarningLevel"), out int warningLevel))
                    {
                        opts = opts.WithWarningLevel(warningLevel);
                    }

                    return opts;
                }

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

        // If MSBuild failed before the compiler ran, CompilerCommand is null and there are
        // no source files. Fall back to the evaluation-time Compile items so the workspace
        // still contains documents (see https://github.com/Buildalyzer/Buildalyzer/issues/341).
        if (sourceFiles.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            sourceFiles = GetItemPaths(analyzerResult, "Compile");
        }

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

        // Fall back to evaluation-time AdditionalFiles items when the compiler never ran (issue #341).
        if (additionalFiles.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            return GetDocuments(GetItemPaths(analyzerResult, "AdditionalFiles"), projectId);
        }

        return GetDocuments(additionalFiles.Select(x => Path.Combine(projectDirectory!, x)), projectId);
    }

    private static IEnumerable<MetadataReference> GetMetadataReferences(IAnalyzerResult analyzerResult)
    {
        string[] references = analyzerResult.References ?? [];

        // When the compiler never ran (issue #341) the resolved reference set is unavailable.
        // Fall back to the evaluation-time reference items: ReferencePath (resolved by
        // ResolveAssemblyReferences) is preferred; the raw Reference items are a last resort.
        if (references.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            string[] fallback = GetItemPaths(analyzerResult, "ReferencePath");
            references = fallback.Length > 0 ? fallback : GetItemPaths(analyzerResult, "Reference");
        }

        return references
            .Where(File.Exists)
            .Select(x => MetadataReference.CreateFromFile(x, new MetadataReferenceProperties(aliases: analyzerResult.ReferenceAliases.GetValueOrDefault(x))));
    }

    private static IEnumerable<AnalyzerReference> GetAnalyzerReferences(IAnalyzerResult analyzerResult, Workspace workspace)
    {
        IAnalyzerAssemblyLoader loader = workspace.Services.GetRequiredService<IAnalyzerService>().GetLoader();

        string projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        string[] analyzerReferences = analyzerResult.AnalyzerReferences ?? [];

        // Fall back to evaluation-time Analyzer items when the compiler never ran (issue #341).
        if (analyzerReferences.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            analyzerReferences = GetItemPaths(analyzerResult, "Analyzer");
        }

        return analyzerReferences.Where(x => File.Exists(Path.GetFullPath(x, projectDirectory!)))
            .Select(x => new AnalyzerFileReference(Path.GetFullPath(x, projectDirectory!), loader));
    }

    /// <summary>
    /// Determines whether workspace state should be reconstructed from evaluation-time
    /// items and properties. This happens when MSBuild aborted before the compiler task
    /// (<c>Csc</c>/<c>Vbc</c>/<c>Fsc</c>) ran, so <c>CompilerCommand</c> was never captured
    /// and the compiler-backed accessors return empty, yet the project did evaluate its
    /// <c>Compile</c> items. See https://github.com/Buildalyzer/Buildalyzer/issues/341.
    /// </summary>
    private static bool ShouldFallBackToItems(IAnalyzerResult analyzerResult) =>
        (analyzerResult.SourceFiles is null || analyzerResult.SourceFiles.Length == 0)
        && analyzerResult.Items.TryGetValue("Compile", out IProjectItem[] compileItems)
        && compileItems.Length > 0;

    /// <summary>
    /// Resolves the <c>ItemSpec</c> of every item of the given type to a full path relative
    /// to the project directory. Returns an empty array when the item type isn't present.
    /// </summary>
    private static string[] GetItemPaths(IAnalyzerResult analyzerResult, string itemType)
    {
        if (!analyzerResult.Items.TryGetValue(itemType, out IProjectItem[] items) || items.Length == 0)
        {
            return [];
        }

        string projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        return items
            .Select(x => Path.GetFullPath(x.ItemSpec, projectDirectory!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> GetPreprocessorSymbols(IAnalyzerResult analyzerResult)
    {
        if (analyzerResult.PreprocessorSymbols is { Length: > 0 } symbols)
        {
            return symbols;
        }

        // Fall back to the evaluated DefineConstants property when the compiler never ran (issue #341).
        if (ShouldFallBackToItems(analyzerResult))
        {
            string defineConstants = analyzerResult.GetProperty("DefineConstants");
            if (!string.IsNullOrWhiteSpace(defineConstants))
            {
                return defineConstants.Split([';'], StringSplitOptions.RemoveEmptyEntries);
            }
        }

        return analyzerResult.PreprocessorSymbols ?? [];
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
