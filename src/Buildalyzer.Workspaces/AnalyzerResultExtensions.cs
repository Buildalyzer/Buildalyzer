using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Buildalyzer.Construction;
using Buildalyzer.IO;
using Microsoft.Extensions.Logging;

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
    /// <returns>
    /// The newly added Roslyn project, or <c>null</c> if the project couldn't be added to the workspace
    /// (most commonly because its language is not one Roslyn workspaces support, such as F#).
    /// </returns>
    public static Project? AddToWorkspace(this IAnalyzerResult analyzerResult, Workspace workspace, bool addProjectReferences = false)
    {
        Guard.NotNull(analyzerResult);
        Guard.NotNull(workspace);

        // Add the referenced projects first (post-order) so this result can wire to their outputs.
        // Seed the visited set with this project so a reference cycle can't re-add it.
        HashSet<string> visited = new(IOPath.Comparer) { NormalizePath(analyzerResult.ProjectFilePath) };
        if (addProjectReferences)
        {
            // Build the referenced-project closure up front in parallel (this project's own result is
            // already in hand), then populate the workspace sequentially from that cache below.
            IReadOnlyList<IProjectAnalyzer> referencedRoots = ResolveReferencedRoots(analyzerResult.Manager, analyzerResult.ProjectReferences);
            IReadOnlyDictionary<string, IAnalyzerResult[]> prebuilt = PrebuildReferenceClosure(analyzerResult.Manager, referencedRoots);
            AddReferencedAnalyzers(analyzerResult.Manager, analyzerResult.ProjectReferences, workspace, visited, prebuilt);
        }

        // Match MSBuildWorkspace's naming: a framework flavour of a multi-targeted project is
        // "<Project>(<tfm>)" even when it is the only flavour being added; a single-targeted project
        // keeps its bare name. Only this result is in hand, so whether the project is multi-targeted is
        // read from its TargetFrameworks (see IsMultiTargeted).
        ProjectId? projectId = AddResult(analyzerResult, workspace, addDiscriminator: IsMultiTargeted(analyzerResult));
        return projectId is null ? null : workspace.CurrentSolution.GetProject(projectId);
    }

    /// <summary>
    /// Adds every succeeded target-framework result of an analyzer - and, post-order, the analyzers it
    /// references - to the workspace, modelling each (project, framework) as its own Roslyn project just
    /// like MSBuildWorkspace. Project references are wired by resolved output-assembly path, so a consumer
    /// binds the exact framework flavour of a multi-targeted dependency that MSBuild resolved. Returns the
    /// ProjectIds of this analyzer's own per-framework projects, in framework order.
    /// </summary>
    internal static IReadOnlyList<ProjectId> AddAnalyzer(IProjectAnalyzer analyzer, Workspace workspace, bool addProjectReferences, HashSet<string> visited, IReadOnlyDictionary<string, IAnalyzerResult[]>? prebuilt = null)
    {
        string projectPath = NormalizePath(analyzer.ProjectFile.Path);
        if (!visited.Add(projectPath))
        {
            return [];
        }

        // One Roslyn project per target framework. Results are reused from the pre-built cache when the
        // caller built projects in parallel up front.
        IAnalyzerResult[] results = prebuilt is not null && prebuilt.TryGetValue(projectPath, out IAnalyzerResult[] cached)
            ? cached
            : WorkspaceResults(analyzer.Build());

        // Post-order: add the projects this one references before it, so their outputs are present to
        // wire against and every project reference resolves in a single forward pass.
        if (addProjectReferences)
        {
            AddReferencedAnalyzers(analyzer.Manager, results.SelectMany(r => r.ProjectReferences), workspace, visited, prebuilt);
        }

        // Match MSBuildWorkspace exactly: it appends the "(tfm)" discriminator only when the project yielded
        // more than one per-framework project, whatever TargetFrameworks says. A project that lists a single
        // framework in TargetFrameworks, or that overrides an inherited TargetFrameworks with its own
        // TargetFramework, builds once and keeps its bare name.
        bool addDiscriminator = results.Length > 1;

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

    private static void AddReferencedAnalyzers(IAnalyzerManager manager, IEnumerable<string> projectReferences, Workspace workspace, HashSet<string> visited, IReadOnlyDictionary<string, IAnalyzerResult[]>? prebuilt)
    {
        foreach (string referencePath in projectReferences.Distinct(IOPath.Comparer))
        {
            if (ResolveReferenced(manager, referencePath) is { } referenced)
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
    /// <see cref="Buildalyzer.Construction.ProjectFile.ProjectReferences"/>), so no build is needed to find
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
        Dictionary<string, IProjectAnalyzer> closure = new(IOPath.Comparer);
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
            foreach (string reference in ((ProjectFile)analyzer.ProjectFile).ProjectReferences)
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
        return BuildAll(closure.Values);
    }

    /// <summary>
    /// Builds every analyzer concurrently and indexes the results to model as Roslyn projects by project path.
    /// </summary>
    /// <remarks>
    /// A single failing build surfaces its own exception, as it does when a project is built on the sequential
    /// path (and as the core does for a multi-targeted project's per-framework builds), rather than PLINQ's
    /// <see cref="AggregateException"/> around it.
    /// </remarks>
    internal static IReadOnlyDictionary<string, IAnalyzerResult[]> BuildAll(IEnumerable<IProjectAnalyzer> analyzers)
    {
        try
        {
            return analyzers
                .AsParallel()
                .Select(a => (Path: NormalizePath(a.ProjectFile.Path), Results: WorkspaceResults(a.Build())))
                .ToList()
                .ToDictionary(x => x.Path, x => x.Results, IOPath.Comparer);
        }
        catch (AggregateException exception) when (exception.InnerExceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerExceptions[0]).Throw();
            throw;
        }
    }

    /// <summary>
    /// The per-framework results to model as Roslyn projects.
    /// </summary>
    /// <remarks>
    /// A <em>failed</em> result is deliberately kept: when MSBuild aborts before the compiler task runs the
    /// project still evaluated its <c>Compile</c> items and resolved its references, and the workspace is
    /// reconstructed from those (see <see cref="ShouldFallBackToItems"/> and issue #341). Dropping failed
    /// results here would leave that recovery unreachable from <c>IProjectAnalyzer.GetWorkspace()</c> and
    /// <c>IAnalyzerManager.GetWorkspace()</c>.
    /// <para>
    /// Only the empty-framework outer evaluation of a multi-targeted build is dropped, and only when
    /// per-framework results exist alongside it: it compiles nothing itself and would otherwise duplicate
    /// the project under a bare name. When it is the only result (an evaluation-only or early-failure
    /// build) it is kept, since it is all the caller has.
    /// </para>
    /// </remarks>
    internal static IAnalyzerResult[] WorkspaceResults(IAnalyzerResults results)
    {
        IAnalyzerResult[] all = [.. results];
        return all.Any(r => !string.IsNullOrEmpty(r.TargetFramework))
            ? [.. all.Where(r => !string.IsNullOrEmpty(r.TargetFramework))]
            : all;
    }

    private static IReadOnlyList<IProjectAnalyzer> ResolveReferencedRoots(IAnalyzerManager manager, IEnumerable<string> projectReferences)
    {
        List<IProjectAnalyzer> roots = [];
        foreach (string reference in projectReferences.Distinct(IOPath.Comparer))
        {
            if (ResolveReferenced(manager, reference) is { } referenced)
            {
                roots.Add(referenced);
            }
        }

        return roots;
    }

    // Fail-soft: a reference whose project file is not on disk is skipped rather than handed to
    // GetProject, which throws for a missing file. The static XML scan that feeds the closure discovery
    // keeps conditioned references and unexpanded "$(Property)" includes, neither of which is a real
    // project, and a build's evaluated references can name a file that has since been removed.
    private static IProjectAnalyzer? ResolveReferenced(IAnalyzerManager manager, string referencePath) =>
        manager.Projects.TryGetValue(referencePath, out IProjectAnalyzer existing)
            ? existing
            : File.Exists(referencePath)
                ? manager.GetProject(referencePath)
                : null;

    // Adds a single target-framework result as its own Roslyn project and wires its project references by
    // resolved output-assembly path. Returns null when the language is unsupported, or the id of the
    // existing project when the same (project file, project name) has already been added (idempotent).
    private static ProjectId? AddResult(IAnalyzerResult analyzerResult, Workspace workspace, bool addDiscriminator)
    {
        if (!TryGetSupportedLanguageName(analyzerResult.ProjectFilePath, out string languageName))
        {
            return null;
        }

        // Idempotent: the same (project file, project name) is the same (project, framework).
        string projectName = ProjectName(analyzerResult, addDiscriminator);
        if (FindProject(workspace.CurrentSolution, analyzerResult.ProjectFilePath, projectName) is { } existingId)
        {
            return existingId;
        }

        // Parse the captured compiler command line once and share it across options, documents and
        // references. It is the authoritative record of what the compiler actually saw and is present in
        // virtually every binary log (the csc/vbc command line is a normal-verbosity message), so it also
        // backfills the compiler-derived inputs when the structured task-input events weren't captured.
        string? projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        CommandLineArguments? commandLine = ParseCommandLine(analyzerResult, languageName, projectDirectory);
        if (commandLine is null)
        {
            // Warn that the project is reconstructed from evaluation data (see ShouldFallBackToItems): it has
            // no build-generated sources and its options come from evaluated properties. Without the warning
            // such a project is indistinguishable from a fully built one.
            analyzerResult.Manager.LoggerFactory?.CreateLogger(typeof(AnalyzerResultExtensions).FullName!).LogWarning(
                "No compiler invocation was captured for {ProjectFile} ({TargetFramework}); the build did not reach CoreCompile, "
                + "so the workspace project is reconstructed from the evaluated items and properties plus what the build resolved "
                + "before failing (build-generated sources and anything resolved after the failure are missing). Succeeded: {Succeeded}.",
                analyzerResult.ProjectFilePath,
                string.IsNullOrEmpty(analyzerResult.TargetFramework) ? "no target framework" : analyzerResult.TargetFramework,
                analyzerResult.Succeeded);
        }

        ProjectId projectId = ProjectId.CreateNewId();
        Microsoft.CodeAnalysis.ProjectInfo projectInfo = GetProjectInfo(
            analyzerResult, workspace, projectId, projectName, languageName, projectDirectory, commandLine);
        if (projectInfo is null)
        {
            return null;
        }

        Solution solution = workspace.CurrentSolution.AddProject(projectInfo);

        // Wire both directions so the result is independent of the order projects are added in: this
        // project references the dependencies already present, and the consumers already present that
        // resolved to this project's output reference it. The consumers' reference paths are remembered
        // per workspace for that reverse pass.
        ReferencePath[] referencePaths = [.. GetReferencePaths(analyzerResult, commandLine)];
        solution = WireProjectReferences(solution, projectId, analyzerResult.TargetFramework, referencePaths);
        ReferenceCache cache = ReferenceCaches.GetOrCreateValue(workspace);
        cache.Set(projectId, analyzerResult.TargetFramework, referencePaths);
        solution = WireReferencingProjects(solution, projectId, cache);

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
    private static Solution WireProjectReferences(Solution solution, ProjectId projectId, string? consumerTargetFramework, IReadOnlyList<ReferencePath> references)
    {
        Dictionary<string, List<(ProjectId Id, string? TargetFramework)>> outputToProjects = BuildOutputIndex(solution, projectId);
        if (outputToProjects.Count == 0)
        {
            return solution;
        }

        HashSet<ProjectId> referenced = [];
        foreach (ReferencePath reference in references)
        {
            if (outputToProjects.TryGetValue(NormalizePath(reference.Reference), out List<(ProjectId Id, string? TargetFramework)> candidates)
                && ChooseReferencedProject(candidates, consumerTargetFramework) is { } targetId
                && referenced.Add(targetId))
            {
                // Carry the aliases and embed-interop flag over to the project reference. Turning a
                // resolved assembly reference into a project reference must not change what the compiler
                // sees: an `extern alias`ed dependency is not in the global namespace, and an embedded
                // interop reference contributes types rather than a reference. Dropping them here would
                // make the workspace compile differently from the build it came from.
                solution = solution.AddProjectReference(
                    projectId,
                    new ProjectReference(targetId, reference.Aliases, reference.EmbedInteropTypes));
            }
        }

        return solution;
    }

    // The reverse of WireProjectReferences: every project already in the workspace whose resolved
    // references name the newly added project's output gets a project reference to it. Without this pass
    // a consumer added before its dependency - the documented pattern of adding results directly when they
    // are already in hand - would silently miss the reference. The candidate choice is the forward pass's,
    // so when the new project is the framework flavour a consumer would have picked over the sibling it
    // was wired to earlier (only that sibling existed at the time), the consumer is rewired to it; the
    // graph ends up as it would had the consumer been added last.
    private static Solution WireReferencingProjects(Solution solution, ProjectId projectId, ReferenceCache cache)
    {
        if (solution.GetProject(projectId) is not { } added)
        {
            return solution;
        }

        HashSet<string> outputs = new(IOPath.Comparer);
        foreach (string? output in new[] { added.OutputFilePath, added.OutputRefFilePath })
        {
            if (!string.IsNullOrEmpty(output))
            {
                outputs.Add(NormalizePath(output));
            }
        }

        if (outputs.Count == 0)
        {
            return solution;
        }

        foreach ((ProjectId consumerId, string? consumerTargetFramework, ReferencePath[] references) in cache.Snapshot())
        {
            if (consumerId.Equals(projectId) || solution.GetProject(consumerId) is not { } consumer)
            {
                continue;
            }

            foreach (ReferencePath reference in references)
            {
                string normalized = NormalizePath(reference.Reference);
                if (!outputs.Contains(normalized))
                {
                    continue;
                }

                if (!BuildOutputIndex(solution, consumerId).TryGetValue(normalized, out List<(ProjectId Id, string? TargetFramework)> candidates)
                    || !projectId.Equals(ChooseReferencedProject(candidates, consumerTargetFramework)))
                {
                    continue;
                }

                // Drop a reference to a sibling flavour of the same output that only won because this
                // flavour was not there yet.
                foreach (ProjectReference existing in consumer.ProjectReferences)
                {
                    if (!existing.ProjectId.Equals(projectId) && candidates.Any(c => c.Id.Equals(existing.ProjectId)))
                    {
                        solution = solution.RemoveProjectReference(consumerId, existing);
                    }
                }

                if (!consumer.ProjectReferences.Any(r => r.ProjectId.Equals(projectId)))
                {
                    solution = solution.AddProjectReference(
                        consumerId,
                        new ProjectReference(projectId, reference.Aliases, reference.EmbedInteropTypes));
                }

                consumer = solution.GetProject(consumerId)!;
            }
        }

        return solution;
    }

    /// <summary>A resolved reference path with the alias and embed-interop metadata the compiler was given for it.</summary>
    private readonly record struct ReferencePath(string Reference, ImmutableArray<string> Aliases, bool EmbedInteropTypes);

    // The reference paths of every project added to a workspace, kept for as long as the workspace lives
    // (the table holds it weakly) so that WireReferencingProjects can wire consumers added earlier.
    private static readonly ConditionalWeakTable<Workspace, ReferenceCache> ReferenceCaches = new();

    private sealed class ReferenceCache
    {
        private readonly Dictionary<ProjectId, (string? TargetFramework, ReferencePath[] References)> _entries = [];

        public void Set(ProjectId projectId, string? targetFramework, ReferencePath[] references)
        {
            lock (_entries)
            {
                _entries[projectId] = (targetFramework, references);
            }
        }

        public List<(ProjectId Id, string? TargetFramework, ReferencePath[] References)> Snapshot()
        {
            lock (_entries)
            {
                return [.. _entries.Select(e => (e.Key, e.Value.TargetFramework, e.Value.References))];
            }
        }
    }

    // Every project claiming an output path is kept as a candidate: a multi-targeted project that sets
    // AppendTargetFrameworkToOutputPath=false has one output path for ALL of its frameworks, so a single
    // last-write-wins slot would silently rewire consumers to an arbitrary flavour.
    private static Dictionary<string, List<(ProjectId Id, string? TargetFramework)>> BuildOutputIndex(Solution solution, ProjectId exclude)
    {
        Dictionary<string, List<(ProjectId Id, string? TargetFramework)>> index = new(IOPath.Comparer);
        foreach (Project project in solution.Projects)
        {
            if (project.Id.Equals(exclude))
            {
                continue;
            }

            string? targetFramework = ExtractTargetFramework(project.Name);
            foreach (string? output in new[] { project.OutputFilePath, project.OutputRefFilePath })
            {
                if (!string.IsNullOrEmpty(output))
                {
                    string normalized = NormalizePath(output);
                    if (!index.TryGetValue(normalized, out List<(ProjectId Id, string? TargetFramework)> candidates))
                    {
                        index[normalized] = candidates = [];
                    }

                    if (!candidates.Any(c => c.Id.Equals(project.Id)))
                    {
                        candidates.Add((project.Id, targetFramework));
                    }
                }
            }
        }

        return index;
    }

    // The output path alone cannot say which framework flavour a consumer resolved when several projects
    // claim the same path (AppendTargetFrameworkToOutputPath=false). Prefer the candidate whose framework
    // equals the consuming result's - when the consumer's own framework is among the dependency's, that is
    // also what MSBuild's nearest-framework negotiation picks. Otherwise fall back to the last candidate
    // added, preserving the previous behaviour for references that stay genuinely ambiguous.
    private static ProjectId? ChooseReferencedProject(List<(ProjectId Id, string? TargetFramework)> candidates, string? consumerTargetFramework)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count > 1 && !string.IsNullOrEmpty(consumerTargetFramework))
        {
            foreach ((ProjectId id, string? targetFramework) in candidates)
            {
                if (string.Equals(targetFramework, consumerTargetFramework, StringComparison.OrdinalIgnoreCase))
                {
                    return id;
                }
            }
        }

        return candidates[^1].Id;
    }

    // The "(tfm)" discriminator ProjectName appends to a multi-targeted project's name is the only
    // per-framework identity a Roslyn Project carries; a single-framework project keeps its bare name
    // (and its output path can't collide with a sibling flavour's).
    private static string? ExtractTargetFramework(string projectName)
    {
        int open = projectName.LastIndexOf('(');
        return open >= 0 && projectName.EndsWith(")", StringComparison.Ordinal)
            ? projectName[(open + 1)..^1]
            : null;
    }

    // Locates an already-added Roslyn project by its identity: the project file it came from plus its name,
    // which carries the "(tfm)" discriminator for a multi-targeted project. Output paths are NOT an identity
    // - unrelated projects can share a TargetPath, and a multi-targeted project that sets
    // AppendTargetFrameworkToOutputPath=false has one output path for all of its frameworks - so they are
    // used only for wiring project references (see WireProjectReferences).
    private static ProjectId? FindProject(Solution solution, string? projectFilePath, string projectName)
    {
        if (string.IsNullOrEmpty(projectFilePath))
        {
            return null;
        }

        string normalized = NormalizePath(projectFilePath);
        foreach (Project project in solution.Projects)
        {
            if (project.FilePath is { } path
                && NormalizePath(path).Equals(normalized, IOPath.Comparison)
                && string.Equals(project.Name, projectName, StringComparison.Ordinal))
            {
                return project.Id;
            }
        }

        return null;
    }

    // The resolved reference paths used for output-path correlation, each with the aliases and embed-interop
    // flag the compiler was given for it. Unlike GetMetadataReferences these are NOT filtered by File.Exists:
    // a project reference resolves to a dependency's output that a design-time build never writes to disk,
    // and that (nonexistent) path is exactly what we match on.
    private static IEnumerable<ReferencePath> GetReferencePaths(
        IAnalyzerResult analyzerResult,
        CommandLineArguments? commandLine)
    {
        string[] references = analyzerResult.References ?? [];

        // The command line's /reference: switches list the same resolved assembly paths (project outputs
        // included), so they correlate to project references too when task inputs weren't captured. The
        // switch carries its own alias and embed-interop metadata.
        if (references.Length == 0 && commandLine is not null)
        {
            return commandLine.MetadataReferences
                .Select(r => new ReferencePath(r.Reference, r.Properties.Aliases, r.Properties.EmbedInteropTypes));
        }

        if (references.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            return GetReferencePathItems(analyzerResult);
        }

        return references.Select(reference => new ReferencePath(
            reference,
            analyzerResult.ReferenceAliases.GetValueOrDefault(reference),
            analyzerResult.ReferencesEmbeddingInteropTypes.Contains(reference)));
    }

    // Whether the project is multi-targeted, judged from a single framework's result. MSBuildWorkspace loads
    // a project once per ';'-separated entry of TargetFrameworks (unless TargetFramework is set) and names
    // each "<Project>(<tfm>)" only when that yields more than one, so a TargetFrameworks listing a single
    // framework does not earn the discriminator. The evaluated TargetFrameworks is present in each
    // framework's inner build too, so it can be read from any result. One case is not decidable from the
    // result alone: a project that overrides an inherited multi-valued TargetFrameworks with its own
    // TargetFramework builds once, but its result looks like one flavour of a multi-targeted build; the
    // analyzer-level entry points count the results instead (see AddAnalyzer) and get that case right.
    private static bool IsMultiTargeted(IAnalyzerResult analyzerResult) =>
        (analyzerResult.GetProperty("TargetFrameworks") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1;

    private static string ProjectName(IAnalyzerResult analyzerResult, bool addDiscriminator)
    {
        string name = Path.GetFileNameWithoutExtension(analyzerResult.ProjectFilePath);
        return addDiscriminator && !string.IsNullOrWhiteSpace(analyzerResult.TargetFramework)
            ? $"{name}({analyzerResult.TargetFramework})"
            : name;
    }

    internal static string NormalizePath(string path) => Path.GetFullPath(path);

    private static Microsoft.CodeAnalysis.ProjectInfo? GetProjectInfo(
        IAnalyzerResult analyzerResult, Workspace workspace, ProjectId projectId, string projectName,
        string languageName, string? projectDirectory, CommandLineArguments? commandLine)
    {
        string assemblyName = analyzerResult.GetProperty("AssemblyName") is { Length: > 0 } name ? name : projectName;
        (CompilationOptions? compilationOptions, ParseOptions? parseOptions) = CreateOptions(analyzerResult, languageName, projectDirectory, commandLine);

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
            documents: GetDocuments(analyzerResult, projectId, commandLine),
            projectReferences: [],
            metadataReferences: GetMetadataReferences(analyzerResult, commandLine),
            analyzerReferences: GetAnalyzerReferences(analyzerResult, workspace, commandLine),
            additionalDocuments: GetAdditionalDocuments(analyzerResult, projectId, commandLine))
            .WithDefaultNamespace(analyzerResult.GetProperty("RootNamespace"))
            .WithAnalyzerConfigDocuments(GetAnalyzerConfigDocuments(analyzerResult, projectId, commandLine));
    }

    /// <summary>
    /// Parses the captured compiler command line with Roslyn's own parser, or returns <c>null</c> when no
    /// command line was captured (e.g. the build failed before the compiler task ran). The result is the
    /// authoritative record of what the compiler saw - defines, language version, unsafe/checked/nullable,
    /// optimization, platform, warning level, documentation mode and full diagnostic configuration, plus the
    /// resolved source/reference/analyzer/additional/analyzer-config sets - and is used both for the
    /// compilation/parse options and to backfill those inputs when the structured task-input events are absent.
    /// </summary>
    /// <remarks>
    /// Language-specific parsing stays in local functions so the parser assembly for the other language is
    /// never loaded for a project that does not use it.
    /// </remarks>
    private static CommandLineArguments? ParseCommandLine(IAnalyzerResult analyzerResult, string languageName, string? projectDirectory)
    {
        if (analyzerResult.CompilerArguments is not { Length: > 0 } arguments)
        {
            return null;
        }

        if (languageName == LanguageNames.CSharp)
        {
            CommandLineArguments ParseCSharp() => CSharpCommandLineParser.Default.Parse(arguments, projectDirectory, sdkDirectory: null);
            return ParseCSharp();
        }

        if (languageName == LanguageNames.VisualBasic)
        {
            CommandLineArguments ParseVisualBasic() => VisualBasicCommandLineParser.Default.Parse(arguments, projectDirectory, sdkDirectory: null);
            return ParseVisualBasic();
        }

        return null;
    }

    /// <summary>
    /// Produces the compilation and parse options. The primary source is the parsed compiler command line,
    /// which yields exactly the options the compiler used (and that MSBuildWorkspace reports). When no command
    /// line was captured (the build stopped before the compiler ran) the command line the compiler task would
    /// have produced is reconstructed from the evaluated properties and parsed the same way
    /// (<see cref="EvaluatedCommandLine"/>), so NoWarn/WarningsAsErrors, defines, language version, nullable,
    /// signing, the module name and the rest follow the compiler's own semantics on that path too.
    /// </summary>
    private static (CompilationOptions? CompilationOptions, ParseOptions? ParseOptions) CreateOptions(
        IAnalyzerResult analyzerResult, string languageName, string? projectDirectory, CommandLineArguments? commandLine)
    {
        (CompilationOptions? compilationOptions, ParseOptions? parseOptions) = CreateRawOptions(analyzerResult, languageName, commandLine);

        if (compilationOptions is not null)
        {
            compilationOptions = WithWorkspaceServices(compilationOptions, projectDirectory);
        }

        // The compiler only parses doc comments when asked to emit them (/doc), so a project without
        // GenerateDocumentationFile parses with DocumentationMode.None. MSBuildWorkspace always raises that
        // to Parse ("ensure that doc-comments are parsed") so IDE features see the comments; do the same.
        if (parseOptions is { DocumentationMode: DocumentationMode.None })
        {
            parseOptions = parseOptions.WithDocumentationMode(DocumentationMode.Parse);
        }

        return (compilationOptions, parseOptions);
    }

    private static (CompilationOptions? CompilationOptions, ParseOptions? ParseOptions) CreateRawOptions(
        IAnalyzerResult analyzerResult, string languageName, CommandLineArguments? commandLine)
    {
        commandLine ??= EvaluatedCommandLine.Parse(analyzerResult, languageName, Path.GetDirectoryName(analyzerResult.ProjectFilePath));
        return commandLine is null ? (null, null) : (commandLine.CompilationOptions, commandLine.ParseOptions);
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

    private static IEnumerable<DocumentInfo> GetAnalyzerConfigDocuments(IAnalyzerResult analyzerResult, ProjectId projectId, CommandLineArguments? commandLine)
    {
        // The compiler receives these as absolute paths via /analyzerconfig:, including the
        // SDK-generated <Project>.GeneratedMSBuildEditorConfig.editorconfig that surfaces
        // build_property.* values many source generators depend on.
        string[] analyzerConfigFiles = analyzerResult.AnalyzerConfigFiles ?? [];

        // Backfill from the command line's /analyzerconfig: switches when task inputs weren't captured.
        if (analyzerConfigFiles.Length == 0 && commandLine is not null)
        {
            analyzerConfigFiles = [.. commandLine.AnalyzerConfigPaths];
        }

        // Last resort, when the compiler never ran (issue #341): the SDK computes EditorConfigFiles at
        // evaluation - every .editorconfig and .globalconfig in a directory above a Compile item, plus the
        // project's own GlobalAnalyzerConfigFiles - and hands exactly that item group to the compiler task as
        // AnalyzerConfigFiles, so a failed build still has the set that governs analyzer severities and
        // source-generator options. Only the build-generated GeneratedMSBuildEditorConfig.editorconfig is
        // missing, as the build-generated sources are; MSBuildWorkspace reads the same items in this case.
        if (analyzerConfigFiles.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            analyzerConfigFiles = GetItemPaths(analyzerResult, "EditorConfigFiles");
        }

        return GetDocuments(
            analyzerConfigFiles,
            projectId,
            Path.GetDirectoryName(analyzerResult.ProjectFilePath),
            GetChecksumAlgorithm(analyzerResult),
            GetDocumentLinks(analyzerResult, "EditorConfigFiles"));
    }

    private static IEnumerable<DocumentInfo> GetDocuments(IAnalyzerResult analyzerResult, ProjectId projectId, CommandLineArguments? commandLine)
    {
        string[] sourceFiles = analyzerResult.SourceFiles ?? [];

        // When the compiler task's inputs weren't captured (e.g. replaying a binary log that lacks task
        // parameters) SourceFiles is empty, but the csc/vbc command line - a normal-verbosity message present
        // in virtually every binary log - still lists the exact, final source set (generated files included).
        if (sourceFiles.Length == 0 && commandLine is not null)
        {
            sourceFiles = [.. commandLine.SourceFiles.Select(f => f.Path)];
        }

        // Last resort: when neither the task inputs nor a command line were captured (the build failed before
        // the compiler ran), the evaluation-time Compile items - declaration order, no build-generated
        // sources - at least give the workspace documents (issue #341).
        if (sourceFiles.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            sourceFiles = GetItemPaths(analyzerResult, "Compile");
        }

        return GetDocuments(
            sourceFiles,
            projectId,
            Path.GetDirectoryName(analyzerResult.ProjectFilePath),
            GetChecksumAlgorithm(analyzerResult),
            GetDocumentLinks(analyzerResult, "Compile"));
    }

    // Every input path becomes a document, whether or not it exists on disk, matching MSBuildWorkspace.
    // The text is loaded lazily on first read; a missing file then produces an empty document and a
    // standard Roslyn document-load failure instead of being silently dropped here (issue #345).
    private static IEnumerable<DocumentInfo> GetDocuments(
        IEnumerable<string> files,
        ProjectId projectId,
        string? projectDirectory,
        SourceHashAlgorithm checksumAlgorithm,
        IReadOnlyDictionary<string, string> links) =>
       files.Select(x =>
       {
           (string name, IEnumerable<string> folders) = GetLogicalPath(x, projectDirectory, links);
           return DocumentInfo.Create(
               DocumentId.CreateNewId(projectId),
               name,
               folders: folders,
               loader: new LazyFileTextLoader(x, checksumAlgorithm),
               filePath: x);
       });

    /// <summary>
    /// Reads a document's text from disk on first access, like MSBuildWorkspace's file loader, instead of
    /// eagerly at workspace construction. Roslyn turns an <see cref="IOException"/> thrown here into an
    /// empty document plus a document-load diagnostic, which is exactly how MSBuildWorkspace surfaces a
    /// source path that does not exist on disk.
    /// </summary>
    private sealed class LazyFileTextLoader(string path, SourceHashAlgorithm checksumAlgorithm) : TextLoader
    {
        public override Task<TextAndVersion> LoadTextAndVersionAsync(LoadTextOptions options, CancellationToken cancellationToken)
            => Task.FromResult(TextAndVersion.Create(
                ReadSourceText(path, checksumAlgorithm),
                VersionStamp.Create(File.GetLastWriteTimeUtc(path)),
                path));
    }

    /// <summary>
    /// When MSBuild aborts before the compiler task runs, <c>CompilerCommand</c> is never captured, so the
    /// compiler-backed accessors (<c>SourceFiles</c>, <c>References</c>, ...) are empty even though the project
    /// evaluated its <c>Compile</c> items. In that case the workspace is reconstructed from evaluation-time
    /// items plus the item groups the build's resolution tasks output before failing - the
    /// <c>ReferencePath</c> from ResolveAssemblyReference and the analyzers from the SDK's targeting-pack and
    /// package resolution. See issue #341.
    /// <para>
    /// Only safe for languages whose compilation is order-insensitive. Evaluated <c>Compile</c> items are
    /// in declaration order, which for F# is not compile order (<c>FSharpSourceCodeCompileOrder</c> re-sorts
    /// them by <c>CompileOrder</c> metadata before <c>CoreCompile</c>), so this fallback would silently
    /// produce a differently-meaning compilation. F# never reaches here today because
    /// <see cref="TryGetSupportedLanguageName"/> rejects <c>.fsproj</c>; guard this if that ever changes.
    /// </para>
    /// </summary>
    private static bool ShouldFallBackToItems(IAnalyzerResult analyzerResult) =>
        (analyzerResult.SourceFiles is null || analyzerResult.SourceFiles.Length == 0)
        && analyzerResult.Items.TryGetValue("Compile", out IProjectItem[] compileItems)
        && compileItems.Length > 0;

    // The resolved ReferencePath items (captured from ResolveAssemblyReference, issue #341), each with the
    // aliases and embed-interop flag its metadata carries: the compiler task reads both off these same items,
    // forwarded from the Reference/ProjectReference items, so a reconstructed project that dropped them would
    // compile differently from the build. One entry per path, as the compiler-side maps are keyed. A lone
    // 'global' alias is the ordinary unaliased reference, which no aliases already means (and which
    // CreateMetadataReferences would otherwise split into two identical references).
    private static IEnumerable<ReferencePath> GetReferencePathItems(IAnalyzerResult analyzerResult)
    {
        if (!analyzerResult.Items.TryGetValue("ReferencePath", out IProjectItem[] items) || items.Length == 0)
        {
            return [];
        }

        string projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        return items
            .GroupBy(x => Path.GetFullPath(x.ItemSpec, projectDirectory!), IOPath.Comparer)
            .Select(group =>
            {
                ImmutableArray<string> aliases = [.. group.SelectMany(AliasNames).Distinct(StringComparer.Ordinal)];
                return new ReferencePath(
                    group.Key,
                    aliases.All(IsGlobalAlias) ? [] : aliases,
                    group.Any(EmbedsInteropTypes));
            });

        static IEnumerable<string> AliasNames(IProjectItem item)
            => Metadata(item, "Aliases").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        static bool EmbedsInteropTypes(IProjectItem item)
            => string.Equals(Metadata(item, "EmbedInteropTypes").Trim(), "true", StringComparison.OrdinalIgnoreCase);

        static string Metadata(IProjectItem item, string name)
            => item.Metadata.FirstOrDefault(m => string.Equals(m.Key, name, StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;

        static bool IsGlobalAlias(string alias) => string.Equals(alias, "global", StringComparison.OrdinalIgnoreCase);
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
            .Distinct(IOPath.Comparer)];
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

    /// <summary>
    /// Maps the physical path of every evaluated item of the given type that carries <c>Link</c> metadata to
    /// that link - the logical path the item is filed under, as in
    /// <c>&lt;Compile Include="../Shared.cs" Link="Virtual/Shared.cs" /&gt;</c>. The compiler is only ever
    /// handed the physical path, so the link is read back from the evaluated items, which is where
    /// MSBuildWorkspace takes it from too.
    /// </summary>
    private static IReadOnlyDictionary<string, string> GetDocumentLinks(IAnalyzerResult analyzerResult, string itemType)
    {
        if (!analyzerResult.Items.TryGetValue(itemType, out IProjectItem[] items) || items.Length == 0)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        string projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        Dictionary<string, string> links = new(IOPath.Comparer);
        foreach (IProjectItem item in items)
        {
            if (item.Metadata.TryGetValue("Link", out string link) && !string.IsNullOrWhiteSpace(link))
            {
                links[Path.GetFullPath(item.ItemSpec, projectDirectory ?? ".")] = link;
            }
        }

        return links;
    }

    // Mirrors MSBuildWorkspace: a document is filed under its logical path - the item's Link metadata when
    // it has one, and the path relative to the project directory otherwise. The last segment of that path is
    // the document's name and the ones before it are its folders, which is how a file that lives outside the
    // project directory still shows up in the folder the project files it under.
    private static (string Name, IEnumerable<string> Folders) GetLogicalPath(
        string filePath, string? projectDirectory, IReadOnlyDictionary<string, string> links)
        => links.TryGetValue(filePath, out string link) && SplitLogicalPath(link) is [.. var folders, var name]
            ? (name, folders)
            : (Path.GetFileName(filePath), GetDocumentFolders(filePath, projectDirectory));

    // Split on the platform's separators, exactly as MSBuildWorkspace splits a logical path. That makes a
    // backslash-separated link ("Virtual\Shared.cs", the form Visual Studio writes) a single name rather
    // than a folder and a name when the analysis runs off Windows - which is what MSBuildWorkspace reports
    // there too, and matching it is the point.
    private static string[] SplitLogicalPath(string logicalPath)
        => logicalPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    // Mirrors MSBuildWorkspace: the logical folders of a document without a link are the directory of its
    // path relative to the project directory. A file outside the project cone that carries no link of its
    // own keeps no folders, so anything whose relative path escapes the project directory yields none.
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
        if (Path.IsPathRooted(relativePath) || EscapesDirectory(relativePath))
        {
            return [];
        }

        string relativeDirectory = Path.GetDirectoryName(relativePath) ?? string.Empty;
        return relativeDirectory.Length == 0
            ? []
            : relativeDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }

    // Only a leading ".." segment escapes the directory: a folder that merely starts with two dots
    // ("..generated/Foo.cs") is inside it.
    private static bool EscapesDirectory(string relativePath)
        => relativePath == ".."
        || (relativePath.StartsWith("..", StringComparison.Ordinal)
            && (relativePath[2] == Path.DirectorySeparatorChar || relativePath[2] == Path.AltDirectorySeparatorChar));

    private static IEnumerable<DocumentInfo> GetAdditionalDocuments(IAnalyzerResult analyzerResult, ProjectId projectId, CommandLineArguments? commandLine)
    {
        string projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        string[] additionalFiles = analyzerResult.AdditionalFiles ?? [];

        IReadOnlyDictionary<string, string> links = GetDocumentLinks(analyzerResult, "AdditionalFiles");

        // Backfill from the command line's /additionalfile: switches when task inputs weren't captured.
        if (additionalFiles.Length == 0 && commandLine is not null)
        {
            return GetDocuments(commandLine.AdditionalFiles.Select(f => f.Path), projectId, projectDirectory, GetChecksumAlgorithm(analyzerResult), links);
        }

        // Fall back to the evaluation-time AdditionalFiles items when the compiler never ran (issue #341).
        if (additionalFiles.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            return GetDocuments(GetItemPaths(analyzerResult, "AdditionalFiles"), projectId, projectDirectory, GetChecksumAlgorithm(analyzerResult), links);
        }

        return GetDocuments(additionalFiles.Select(x => Path.GetFullPath(x, projectDirectory!)), projectId, projectDirectory, GetChecksumAlgorithm(analyzerResult), links);
    }

    private static IEnumerable<MetadataReference> GetMetadataReferences(IAnalyzerResult analyzerResult, CommandLineArguments? commandLine)
    {
        string[] references = analyzerResult.References ?? [];

        // Backfill from the command line's /reference: switches - which also carry each reference's alias
        // and embed-interop metadata - when task inputs weren't captured.
        if (references.Length == 0 && commandLine is not null)
        {
            return commandLine.MetadataReferences
                .Where(r => File.Exists(r.Reference))
                .Select(r => MetadataReference.CreateFromFile(r.Reference, r.Properties));
        }

        // Fall back to the resolved ReferencePath items (captured from ResolveAssemblyReference) when neither
        // the compiler task inputs nor a command line were captured, so the recovered workspace can still
        // bind types (issue #341).
        if (references.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            return GetReferencePathItems(analyzerResult)
                .Where(r => File.Exists(r.Reference))
                .SelectMany(r => CreateMetadataReferences(r.Reference, r.Aliases, r.EmbedInteropTypes));
        }

        return references
            .Where(File.Exists)
            .SelectMany(x => CreateMetadataReferences(
                x,
                analyzerResult.ReferenceAliases.GetValueOrDefault(x),
                analyzerResult.ReferencesEmbeddingInteropTypes.Contains(x)));
    }

    // The Csc task passes a reference aliased "global,Foo" as two switches - a plain /reference and a
    // /reference:Foo= - and the compiler keeps both, which is the shape MSBuildWorkspace reports (and the one
    // the command-line fallback above produces). A single reference carrying both aliases binds the same.
    private static IEnumerable<MetadataReference> CreateMetadataReferences(string path, ImmutableArray<string> aliases, bool embedInteropTypes)
    {
        if (aliases.IsDefaultOrEmpty || !aliases.Any(IsGlobalAlias))
        {
            yield return MetadataReference.CreateFromFile(path, new MetadataReferenceProperties(aliases: aliases, embedInteropTypes: embedInteropTypes));
            yield break;
        }

        yield return MetadataReference.CreateFromFile(path, new MetadataReferenceProperties(embedInteropTypes: embedInteropTypes));
        yield return MetadataReference.CreateFromFile(path, new MetadataReferenceProperties(
            aliases: [.. aliases.Where(a => !IsGlobalAlias(a))],
            embedInteropTypes: embedInteropTypes));

        static bool IsGlobalAlias(string alias) => string.Equals(alias, "global", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<AnalyzerReference> GetAnalyzerReferences(IAnalyzerResult analyzerResult, Workspace workspace, CommandLineArguments? commandLine)
    {
        IAnalyzerAssemblyLoader loader = workspace.Services.GetRequiredService<IAnalyzerService>().GetLoader();

        string projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        string[] analyzerReferences = analyzerResult.AnalyzerReferences ?? [];

        // Backfill from the command line's /analyzer: switches when task inputs weren't captured.
        if (analyzerReferences.Length == 0 && commandLine is not null)
        {
            analyzerReferences = [.. commandLine.AnalyzerReferences.Select(a => a.FilePath)];
        }

        // Fall back to the Analyzer items when the compiler never ran (issue #341): the evaluated ones plus
        // those the SDK resolved before the failure, which the build forwards as task outputs.
        // ResolveTargetingPackAssets adds the targeting packs' analyzers to Analyzer directly, while
        // ResolvePackageAssets outputs the packages' as ResolvedAnalyzers and a later target copies them into
        // Analyzer with an item-group operation no task event records - so both groups are read.
        if (analyzerReferences.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            analyzerReferences = [.. GetItemPaths(analyzerResult, "Analyzer")
                .Concat(GetItemPaths(analyzerResult, "ResolvedAnalyzers"))
                .Distinct(IOPath.Comparer)];
        }

        // A path that is missing on disk (an unbuilt project-private analyzer, say) becomes an
        // UnresolvedAnalyzerReference rather than being silently dropped, exactly as MSBuildWorkspace
        // surfaces it via CommandLineArguments.ResolveAnalyzerReferences (issue #345).
        return analyzerReferences
            .Select(x => Path.GetFullPath(x, projectDirectory!))
            .Select(x => File.Exists(x)
                ? new AnalyzerFileReference(x, loader)
                : (AnalyzerReference)new UnresolvedAnalyzerReference(x));
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
