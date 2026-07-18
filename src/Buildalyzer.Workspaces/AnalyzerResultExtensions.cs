using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
        if (!TryGetSupportedLanguageName(analyzerResult.ProjectFilePath, out string languageName))
        {
            return null;
        }

        string projectName = Path.GetFileNameWithoutExtension(analyzerResult.ProjectFilePath);
        string assemblyName = analyzerResult.GetProperty("AssemblyName") is { Length: > 0 } name ? name : projectName;
        (CompilationOptions? compilationOptions, ParseOptions? parseOptions) = CreateOptions(analyzerResult, languageName);

        return Microsoft.CodeAnalysis.ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            projectName,
            assemblyName,
            languageName,
            filePath: analyzerResult.ProjectFilePath,
            outputFilePath: analyzerResult.GetProperty("TargetPath"),
            outputRefFilePath: analyzerResult.GetProperty("TargetRefPath"),
            compilationOptions: compilationOptions,
            parseOptions: parseOptions,
            documents: GetDocuments(analyzerResult, projectId),
            projectReferences: GetExistingProjectReferences(analyzerResult, workspace),
            metadataReferences: GetMetadataReferences(analyzerResult),
            analyzerReferences: GetAnalyzerReferences(analyzerResult, workspace),
            additionalDocuments: GetAdditionalDocuments(analyzerResult, projectId))
            .WithDefaultNamespace(analyzerResult.GetProperty("RootNamespace"))
            .WithAnalyzerConfigDocuments(GetAnalyzerConfigDocuments(analyzerResult, projectId));
    }

    /// <summary>
    /// Produces the compilation and parse options. The primary source is the compiler command line
    /// that the design-time build produced: parsing it with Roslyn's own command-line parser yields
    /// exactly the options the compiler used (and that MSBuildWorkspace reports), covering defines,
    /// language version, unsafe/checked/nullable, optimization, platform, warning level, documentation
    /// mode and the full diagnostic configuration in one shot. When no command line was captured
    /// (e.g. the build failed before the compiler task ran) it falls back to reconstructing the options
    /// from evaluated MSBuild properties - a best effort that is necessarily less complete.
    /// </summary>
    private static (CompilationOptions? CompilationOptions, ParseOptions? ParseOptions) CreateOptions(IAnalyzerResult analyzerResult, string languageName)
    {
        string? projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        (CompilationOptions? compilationOptions, ParseOptions? parseOptions) = CreateRawOptions(analyzerResult, languageName, projectDirectory);

        if (compilationOptions is not null)
        {
            compilationOptions = WithWorkspaceServices(compilationOptions, projectDirectory);
        }

        return (compilationOptions, parseOptions);
    }

    private static (CompilationOptions? CompilationOptions, ParseOptions? ParseOptions) CreateRawOptions(IAnalyzerResult analyzerResult, string languageName, string? projectDirectory)
    {
        if (analyzerResult.CompilerArguments is { Length: > 0 } arguments)
        {
            // Language-specific parsing stays in local functions so the parser assembly for the other
            // language is never loaded for a project that does not use it.
            if (languageName == LanguageNames.CSharp)
            {
                (CompilationOptions, ParseOptions) FromCSharpCommandLine()
                {
                    CSharpCommandLineArguments parsed = CSharpCommandLineParser.Default.Parse(arguments, projectDirectory, sdkDirectory: null);
                    return (parsed.CompilationOptions, parsed.ParseOptions);
                }

                return FromCSharpCommandLine();
            }

            if (languageName == LanguageNames.VisualBasic)
            {
                (CompilationOptions, ParseOptions) FromVisualBasicCommandLine()
                {
                    VisualBasicCommandLineArguments parsed = VisualBasicCommandLineParser.Default.Parse(arguments, projectDirectory, sdkDirectory: null);
                    return (parsed.CompilationOptions, parsed.ParseOptions);
                }

                return FromVisualBasicCommandLine();
            }
        }

        return (CreateCompilationOptions(analyzerResult, languageName), CreateParseOptions(analyzerResult, languageName));
    }

    /// <summary>
    /// Attaches the same compilation "services" MSBuildWorkspace puts on every project, so features
    /// relying on them behave the same: assembly identity/version unification (<see cref="DesktopAssemblyIdentityComparer"/>),
    /// XML documentation <c>&lt;include&gt;</c> resolution, <c>#load</c>/<c>#line</c> source resolution, and
    /// strong-name signing during <c>Emit</c>. The command-line parser leaves these null. The metadata
    /// reference resolver MSBuildWorkspace uses is internal to Roslyn and only affects <c>#r</c>, so it is
    /// left at its default.
    /// </summary>
    private static CompilationOptions WithWorkspaceServices(CompilationOptions options, string? projectDirectory)
    {
        ImmutableArray<string> keyFileSearchPaths = projectDirectory is null ? [] : [projectDirectory];
        return options
            .WithXmlReferenceResolver(new XmlFileResolver(projectDirectory))
            .WithSourceReferenceResolver(new SourceFileResolver([], projectDirectory))
            .WithStrongNameProvider(new DesktopStrongNameProvider(keyFileSearchPaths, Path.GetTempPath()))
            .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default);
    }

    // A documentation file (DocumentationFile / GenerateDocumentationFile) causes MSBuild to pass
    // /doc to the compiler, which switches the parser into diagnosing doc comments.
    private static bool GeneratesDocumentationFile(IAnalyzerResult analyzerResult) =>
        !string.IsNullOrWhiteSpace(analyzerResult.GetProperty("DocumentationFile"))
        || (bool.TryParse(analyzerResult.GetProperty("GenerateDocumentationFile"), out bool generate) && generate);

    private static IEnumerable<DocumentInfo> GetAnalyzerConfigDocuments(IAnalyzerResult analyzerResult, ProjectId projectId)
    {
        // The compiler receives these as absolute paths via /analyzerconfig:, including the
        // SDK-generated <Project>.GeneratedMSBuildEditorConfig.editorconfig that surfaces
        // build_property.* values many source generators depend on.
        string[] analyzerConfigFiles = analyzerResult.AnalyzerConfigFiles ?? [];
        return GetDocuments(analyzerConfigFiles, projectId, Path.GetDirectoryName(analyzerResult.ProjectFilePath));
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

                // A documentation file (/doc) makes the compiler diagnose doc comments.
                if (GeneratesDocumentationFile(analyzerResult))
                {
                    parseOptions = parseOptions.WithDocumentationMode(DocumentationMode.Diagnose);
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

                if (GeneratesDocumentationFile(analyzerResult))
                {
                    parseOptions = parseOptions.WithDocumentationMode(DocumentationMode.Diagnose);
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

                    if (bool.TryParse(analyzerResult.GetProperty("Optimize"), out bool optimize))
                    {
                        opts = opts.WithOptimizationLevel(optimize ? OptimizationLevel.Release : OptimizationLevel.Debug);
                    }

                    if (bool.TryParse(analyzerResult.GetProperty("TreatWarningsAsErrors"), out bool warningsAsErrors))
                    {
                        opts = opts.WithGeneralDiagnosticOption(warningsAsErrors ? ReportDiagnostic.Error : ReportDiagnostic.Default);
                    }

                    return opts;
                }

                return CreateCSharpCompilationOptions();
            }

            if (languageName == LanguageNames.VisualBasic)
            {
                CompilationOptions CreateVBCompilationOptions()
                {
                    VisualBasicCompilationOptions opts = new VisualBasicCompilationOptions(kind.Value);

                    if (bool.TryParse(analyzerResult.GetProperty("Optimize"), out bool optimize))
                    {
                        opts = opts.WithOptimizationLevel(optimize ? OptimizationLevel.Release : OptimizationLevel.Debug);
                    }

                    if (bool.TryParse(analyzerResult.GetProperty("TreatWarningsAsErrors"), out bool warningsAsErrors))
                    {
                        opts = opts.WithGeneralDiagnosticOption(warningsAsErrors ? ReportDiagnostic.Error : ReportDiagnostic.Default);
                    }

                    return opts;
                }

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
        return GetDocuments(sourceFiles, projectId, Path.GetDirectoryName(analyzerResult.ProjectFilePath));
    }

    private static IEnumerable<DocumentInfo> GetDocuments(IEnumerable<string> files, ProjectId projectId, string? projectDirectory) =>
       files.Where(File.Exists)
           .Select(x => DocumentInfo.Create(
               DocumentId.CreateNewId(projectId),
               Path.GetFileName(x),
               folders: GetDocumentFolders(x, projectDirectory),
               loader: TextLoader.From(
                   TextAndVersion.Create(
                       SourceText.From(File.ReadAllText(x), Encoding.Unicode), VersionStamp.Create())),
               filePath: x));

    // Mirrors MSBuildWorkspace: a document's logical folders are the directory of its path relative
    // to the project directory. Files outside the project cone are auto-linked by the SDK and keep no
    // folders, so anything whose relative path escapes the project directory yields none.
    private static IEnumerable<string> GetDocumentFolders(string filePath, string? projectDirectory)
    {
        if (string.IsNullOrEmpty(projectDirectory))
        {
            return [];
        }

        string relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(projectDirectory!, filePath)) ?? string.Empty;
        return relativeDirectory.Length == 0 || relativeDirectory.StartsWith("..", StringComparison.Ordinal)
            ? []
            : relativeDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }

    private static IEnumerable<DocumentInfo> GetAdditionalDocuments(IAnalyzerResult analyzerResult, ProjectId projectId)
    {
        string projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        string[] additionalFiles = analyzerResult.AdditionalFiles ?? [];
        return GetDocuments(additionalFiles.Select(x => Path.Combine(projectDirectory!, x)), projectId, projectDirectory);
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
