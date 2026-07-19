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

        // Add the referenced projects first (post-order) so this result can wire to their outputs.
        // Seed the visited set with this project so a reference cycle can't re-add it.
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { NormalizePath(analyzerResult.ProjectFilePath) };
        if (addProjectReferences)
        {
            // Build the referenced-project closure up front in parallel (this project's own result is
            // already in hand), then populate the workspace sequentially from that cache below.
            IReadOnlyList<IProjectAnalyzer> referencedRoots = ResolveReferencedRoots(analyzerResult.Manager, analyzerResult.ProjectReferences);
            IReadOnlyDictionary<string, IAnalyzerResult[]> prebuilt = PrebuildReferenceClosure(analyzerResult.Manager, referencedRoots);
            AddReferencedAnalyzers(analyzerResult.Manager, analyzerResult.ProjectReferences, workspace, visited, prebuilt);
        }

        // A single result adds as a bare-named project (no target-framework discriminator).
        ProjectId? projectId = AddResult(analyzerResult, workspace, addDiscriminator: false);
        return projectId is null ? null : workspace.CurrentSolution.GetProject(projectId);
    }

    /// <summary>
    /// Adds every succeeded target-framework result of an analyzer - and, post-order, the analyzers it
    /// references - to the workspace, modelling each (project, framework) as its own Roslyn project just
    /// like MSBuildWorkspace. Project references are wired by resolved output-assembly path, so a consumer
    /// binds the exact framework flavour of a multi-targeted dependency that MSBuild resolved. Returns the
    /// ProjectIds of this analyzer's own per-framework projects, in framework order.
    /// </summary>
    internal static IReadOnlyList<ProjectId> AddAnalyzer(IProjectAnalyzer analyzer, Workspace workspace, bool addProjectReferences, HashSet<string> visited, IReadOnlyDictionary<string, IAnalyzerResult[]> prebuilt = null)
    {
        string projectPath = NormalizePath(analyzer.ProjectFile.Path);
        if (!visited.Add(projectPath))
        {
            return [];
        }

        // One Roslyn project per succeeded target framework. The empty-framework outer aggregate a
        // multi-targeted build produces is never succeeded, so it is filtered out here. Results are
        // reused from the pre-built cache when the caller built projects in parallel up front.
        IAnalyzerResult[] results = prebuilt is not null && prebuilt.TryGetValue(projectPath, out IAnalyzerResult[] cached)
            ? cached
            : [.. analyzer.Build().Where(r => r.Succeeded)];

        // Post-order: add the projects this one references before it, so their outputs are present to
        // wire against and every project reference resolves in a single forward pass.
        if (addProjectReferences)
        {
            AddReferencedAnalyzers(analyzer.Manager, results.SelectMany(r => r.ProjectReferences), workspace, visited, prebuilt);
        }

        // Match MSBuildWorkspace: only append a "(tfm)" discriminator when the project produced more
        // than one framework; a single-framework project keeps its bare name.
        bool addDiscriminator = results
            .Select(r => r.TargetFramework)
            .Where(tfm => !string.IsNullOrEmpty(tfm))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1;

        List<ProjectId> ids = [];
        foreach (IAnalyzerResult result in results)
        {
            if (AddResult(result, workspace, addDiscriminator) is { } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static void AddReferencedAnalyzers(IAnalyzerManager manager, IEnumerable<string> projectReferences, Workspace workspace, HashSet<string> visited, IReadOnlyDictionary<string, IAnalyzerResult[]> prebuilt)
    {
        foreach (string referencePath in projectReferences.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IProjectAnalyzer referenced = ResolveReferenced(manager, referencePath);
            if (referenced is not null)
            {
                AddAnalyzer(referenced, workspace, addProjectReferences: true, visited, prebuilt);
            }
        }
    }

    /// <summary>
    /// Builds every project reachable through project references from <paramref name="roots"/> and returns
    /// their succeeded results keyed by normalized project path, for reuse when populating the workspace.
    /// </summary>
    /// <remarks>
    /// The transitive closure is discovered by parsing project files only (see
    /// <see cref="Buildalyzer.Construction.IProjectFile.ProjectReferences"/>), so no build is needed to find
    /// it. Every project in the closure is then built in a single parallel wave - this keeps even a linear
    /// A→B→C reference chain concurrent, which a level-by-level build would serialize. <c>Build()</c> is safe
    /// to run concurrently across projects; the workspace itself is populated sequentially afterwards. A
    /// reference the static parse misses simply isn't cached here and is built on demand by
    /// <see cref="AddAnalyzer"/>, so correctness never depends on the discovery being complete.
    /// </remarks>
    internal static IReadOnlyDictionary<string, IAnalyzerResult[]> PrebuildReferenceClosure(
        IAnalyzerManager manager, IEnumerable<IProjectAnalyzer> roots)
    {
        // Discover the closure (cheap XML parse per project); dedupe by canonical project path.
        Dictionary<string, IProjectAnalyzer> closure = new(StringComparer.OrdinalIgnoreCase);
        Queue<IProjectAnalyzer> pending = new();
        foreach (IProjectAnalyzer root in roots)
        {
            if (closure.TryAdd(NormalizePath(root.ProjectFile.Path), root))
            {
                pending.Enqueue(root);
            }
        }

        while (pending.Count > 0)
        {
            IProjectAnalyzer analyzer = pending.Dequeue();
            foreach (string reference in analyzer.ProjectFile.ProjectReferences)
            {
                if (ResolveReferenced(manager, reference) is { } referenced
                    && closure.TryAdd(NormalizePath(referenced.ProjectFile.Path), referenced))
                {
                    pending.Enqueue(referenced);
                }
            }
        }

        // Build the whole closure concurrently, then index the results by project path. Each build restores
        // itself (via the -restore switch, folded into the same invocation); a separate up-front graph
        // restore was measured to be slower, since the per-project restores already overlap in this parallel
        // wave while an up-front restore only adds a serial process.
        return closure.Values
            .AsParallel()
            .Select(a => (Path: NormalizePath(a.ProjectFile.Path), Results: a.Build().Where(r => r.Succeeded).ToArray()))
            .ToList()
            .ToDictionary(x => x.Path, x => x.Results, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<IProjectAnalyzer> ResolveReferencedRoots(IAnalyzerManager manager, IEnumerable<string> projectReferences)
    {
        List<IProjectAnalyzer> roots = [];
        foreach (string reference in projectReferences.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ResolveReferenced(manager, reference) is { } referenced)
            {
                roots.Add(referenced);
            }
        }

        return roots;
    }

    private static IProjectAnalyzer ResolveReferenced(IAnalyzerManager manager, string referencePath) =>
        manager.Projects.TryGetValue(referencePath, out IProjectAnalyzer existing)
            ? existing
            : manager.GetProject(referencePath);

    // Adds a single target-framework result as its own Roslyn project and wires its project references by
    // resolved output-assembly path. Returns null when the language is unsupported, or the id of the
    // existing project when the same output has already been added (idempotent).
    private static ProjectId? AddResult(IAnalyzerResult analyzerResult, Workspace workspace, bool addDiscriminator)
    {
        if (!TryGetSupportedLanguageName(analyzerResult.ProjectFilePath, out _))
        {
            return null;
        }

        // Idempotent: two results with the same output assembly are the same (project, framework).
        if (FindProjectByOutput(workspace.CurrentSolution, analyzerResult.GetProperty("TargetPath")) is { } existingId)
        {
            return existingId;
        }

        ProjectId projectId = ProjectId.CreateNewId();
        Microsoft.CodeAnalysis.ProjectInfo projectInfo = GetProjectInfo(analyzerResult, workspace, projectId, ProjectName(analyzerResult, addDiscriminator));
        if (projectInfo is null)
        {
            return null;
        }

        Solution solution = workspace.CurrentSolution.AddProject(projectInfo);
        solution = WireProjectReferences(solution, projectId, analyzerResult);

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Could not apply workspace solution changes");
        }

        return projectId;
    }

    // Correlates this project's resolved references against the output-assembly paths (TargetPath and the
    // reference assembly TargetRefPath) of the projects already in the workspace: any match becomes a
    // project reference. The reference is not also a metadata reference because a design-time build never
    // produces the output on disk (GetMetadataReferences filters by File.Exists). This is how
    // MSBuildWorkspace resolves the exact framework flavour of a multi-targeted dependency.
    private static Solution WireProjectReferences(Solution solution, ProjectId projectId, IAnalyzerResult analyzerResult)
    {
        Dictionary<string, ProjectId> outputToProject = BuildOutputIndex(solution, projectId);
        if (outputToProject.Count == 0)
        {
            return solution;
        }

        HashSet<ProjectId> referenced = [];
        foreach (string reference in GetReferencePaths(analyzerResult))
        {
            if (outputToProject.TryGetValue(NormalizePath(reference), out ProjectId targetId) && referenced.Add(targetId))
            {
                solution = solution.AddProjectReference(projectId, new ProjectReference(targetId));
            }
        }

        return solution;
    }

    private static Dictionary<string, ProjectId> BuildOutputIndex(Solution solution, ProjectId exclude)
    {
        Dictionary<string, ProjectId> index = new(StringComparer.OrdinalIgnoreCase);
        foreach (Project project in solution.Projects)
        {
            if (project.Id.Equals(exclude))
            {
                continue;
            }

            foreach (string? output in new[] { project.OutputFilePath, project.OutputRefFilePath })
            {
                if (!string.IsNullOrEmpty(output))
                {
                    index[NormalizePath(output)] = project.Id;
                }
            }
        }

        return index;
    }

    private static ProjectId? FindProjectByOutput(Solution solution, string? outputPath)
    {
        if (string.IsNullOrEmpty(outputPath))
        {
            return null;
        }

        string normalized = NormalizePath(outputPath);
        foreach (Project project in solution.Projects)
        {
            if (project.OutputFilePath is { } path && NormalizePath(path).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return project.Id;
            }
        }

        return null;
    }

    // The resolved reference paths used for output-path correlation. Unlike GetMetadataReferences these
    // are NOT filtered by File.Exists: a project reference resolves to a dependency's output that a
    // design-time build never writes to disk, and that (nonexistent) path is exactly what we match on.
    private static IEnumerable<string> GetReferencePaths(IAnalyzerResult analyzerResult)
    {
        string[] references = analyzerResult.References ?? [];
        if (references.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            references = GetItemPaths(analyzerResult, "ReferencePath");
        }

        return references;
    }

    private static string ProjectName(IAnalyzerResult analyzerResult, bool addDiscriminator)
    {
        string name = Path.GetFileNameWithoutExtension(analyzerResult.ProjectFilePath);
        return addDiscriminator && !string.IsNullOrWhiteSpace(analyzerResult.TargetFramework)
            ? $"{name}({analyzerResult.TargetFramework})"
            : name;
    }

    internal static string NormalizePath(string path) => Path.GetFullPath(path);

    private static Microsoft.CodeAnalysis.ProjectInfo? GetProjectInfo(IAnalyzerResult analyzerResult, Workspace workspace, ProjectId projectId, string projectName)
    {
        if (!TryGetSupportedLanguageName(analyzerResult.ProjectFilePath, out string languageName))
        {
            return null;
        }

        string assemblyName = analyzerResult.GetProperty("AssemblyName") is { Length: > 0 } name ? name : projectName;
        (CompilationOptions? compilationOptions, ParseOptions? parseOptions) = CreateOptions(analyzerResult, languageName);

        // Project references are wired after the project is added, by output-assembly path, so that a
        // multi-targeted dependency resolves to the exact framework flavour MSBuild chose (see WireProjectReferences).
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
            projectReferences: [],
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
        return GetDocuments(analyzerConfigFiles, projectId, Path.GetDirectoryName(analyzerResult.ProjectFilePath), GetChecksumAlgorithm(analyzerResult));
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

    private static IEnumerable<DocumentInfo> GetDocuments(IAnalyzerResult analyzerResult, ProjectId projectId)
    {
        string[] sourceFiles = analyzerResult.SourceFiles ?? [];

        // When MSBuild fails before the compiler runs, CompilerCommand (and so SourceFiles) is empty.
        // Fall back to the evaluation-time Compile items so the workspace still has documents (issue #341).
        if (sourceFiles.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            sourceFiles = GetItemPaths(analyzerResult, "Compile");
        }

        return GetDocuments(sourceFiles, projectId, Path.GetDirectoryName(analyzerResult.ProjectFilePath), GetChecksumAlgorithm(analyzerResult));
    }

    private static IEnumerable<DocumentInfo> GetDocuments(IEnumerable<string> files, ProjectId projectId, string? projectDirectory, SourceHashAlgorithm checksumAlgorithm) =>
       files.Where(File.Exists)
           .Select(x => DocumentInfo.Create(
               DocumentId.CreateNewId(projectId),
               Path.GetFileName(x),
               folders: GetDocumentFolders(x, projectDirectory),
               loader: TextLoader.From(
                   TextAndVersion.Create(ReadSourceText(x, checksumAlgorithm), VersionStamp.Create())),
               filePath: x));

    /// <summary>
    /// When MSBuild aborts before the compiler task runs, <c>CompilerCommand</c> is never captured, so the
    /// compiler-backed accessors (<c>SourceFiles</c>, <c>References</c>, ...) are empty even though the project
    /// evaluated its <c>Compile</c> items. In that case the workspace is reconstructed from evaluation-time
    /// items and the resolved <c>ReferencePath</c> captured from ResolveAssemblyReference. See issue #341.
    /// </summary>
    private static bool ShouldFallBackToItems(IAnalyzerResult analyzerResult) =>
        (analyzerResult.SourceFiles is null || analyzerResult.SourceFiles.Length == 0)
        && analyzerResult.Items.TryGetValue("Compile", out IProjectItem[] compileItems)
        && compileItems.Length > 0;

    // Preprocessor symbols come from the compiler command line; when it never ran, recover them from the
    // evaluated DefineConstants property (issue #341).
    private static IEnumerable<string> GetPreprocessorSymbols(IAnalyzerResult analyzerResult)
    {
        if (analyzerResult.PreprocessorSymbols is { Length: > 0 } symbols)
        {
            return symbols;
        }

        if (ShouldFallBackToItems(analyzerResult)
            && analyzerResult.GetProperty("DefineConstants") is { } defineConstants
            && !string.IsNullOrWhiteSpace(defineConstants))
        {
            return defineConstants.Split([';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return analyzerResult.PreprocessorSymbols ?? [];
    }

    /// <summary>Resolves the <c>ItemSpec</c> of each item of the given type to a full path.</summary>
    private static string[] GetItemPaths(IAnalyzerResult analyzerResult, string itemType)
    {
        if (!analyzerResult.Items.TryGetValue(itemType, out IProjectItem[] items) || items.Length == 0)
        {
            return [];
        }

        string projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        return [.. items
            .Select(x => Path.GetFullPath(x.ItemSpec, projectDirectory!))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    // Preserve the file's own encoding (detecting a BOM, defaulting to UTF-8) rather than forcing a
    // fixed encoding, so Document text encoding matches MSBuildWorkspace and the compiler.
    private static SourceText ReadSourceText(string path, SourceHashAlgorithm checksumAlgorithm)
    {
        using FileStream stream = File.OpenRead(path);
        return SourceText.From(stream, Encoding.UTF8, checksumAlgorithm);
    }

    // The SDK hashes source with SHA256 by default (older projects used SHA1); mirror MSBuildWorkspace,
    // which takes the algorithm from the ChecksumAlgorithm property, so document checksums match.
    private static SourceHashAlgorithm GetChecksumAlgorithm(IAnalyzerResult analyzerResult) =>
        analyzerResult.GetProperty("ChecksumAlgorithm")?.ToUpperInvariant() switch
        {
            "SHA1" => SourceHashAlgorithm.Sha1,
            _ => SourceHashAlgorithm.Sha256,
        };

    // Mirrors MSBuildWorkspace: a document's logical folders are the directory of its path relative
    // to the project directory. Files outside the project cone are auto-linked by the SDK and keep no
    // folders, so anything whose relative path escapes the project directory yields none.
    private static IEnumerable<string> GetDocumentFolders(string filePath, string? projectDirectory)
    {
        if (string.IsNullOrEmpty(projectDirectory))
        {
            return [];
        }

        string relativePath = Path.GetRelativePath(projectDirectory!, filePath);

        // GetRelativePath returns a rooted path when the file sits on a different drive/UNC root, and a
        // ..-prefixed path when it escapes the project directory on the same root. Either way the file is
        // outside the project cone, so it keeps no folders.
        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith("..", StringComparison.Ordinal))
        {
            return [];
        }

        string relativeDirectory = Path.GetDirectoryName(relativePath) ?? string.Empty;
        return relativeDirectory.Length == 0
            ? []
            : relativeDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }

    private static IEnumerable<DocumentInfo> GetAdditionalDocuments(IAnalyzerResult analyzerResult, ProjectId projectId)
    {
        string projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        string[] additionalFiles = analyzerResult.AdditionalFiles ?? [];

        // Fall back to the evaluation-time AdditionalFiles items when the compiler never ran (issue #341).
        if (additionalFiles.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            return GetDocuments(GetItemPaths(analyzerResult, "AdditionalFiles"), projectId, projectDirectory, GetChecksumAlgorithm(analyzerResult));
        }

        return GetDocuments(additionalFiles.Select(x => Path.Combine(projectDirectory!, x)), projectId, projectDirectory, GetChecksumAlgorithm(analyzerResult));
    }

    private static IEnumerable<MetadataReference> GetMetadataReferences(IAnalyzerResult analyzerResult)
    {
        string[] references = analyzerResult.References ?? [];

        // Fall back to the resolved ReferencePath items (captured from ResolveAssemblyReference) when the
        // compiler never ran, so the recovered workspace can still bind types (issue #341).
        if (references.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            references = GetItemPaths(analyzerResult, "ReferencePath");
        }

        return references
            .Where(File.Exists)
            .Select(x => MetadataReference.CreateFromFile(x, new MetadataReferenceProperties(
                aliases: analyzerResult.ReferenceAliases.GetValueOrDefault(x),
                embedInteropTypes: analyzerResult.ReferencesEmbeddingInteropTypes.Contains(x))));
    }

    private static IEnumerable<AnalyzerReference> GetAnalyzerReferences(IAnalyzerResult analyzerResult, Workspace workspace)
    {
        IAnalyzerAssemblyLoader loader = workspace.Services.GetRequiredService<IAnalyzerService>().GetLoader();

        string projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        string[] analyzerReferences = analyzerResult.AnalyzerReferences ?? [];

        // Fall back to the evaluation-time Analyzer items when the compiler never ran (issue #341).
        if (analyzerReferences.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            analyzerReferences = GetItemPaths(analyzerResult, "Analyzer");
        }

        return analyzerReferences.Where(x => File.Exists(Path.GetFullPath(x, projectDirectory!)))
            .Select(x => new AnalyzerFileReference(Path.GetFullPath(x, projectDirectory!), loader));
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
