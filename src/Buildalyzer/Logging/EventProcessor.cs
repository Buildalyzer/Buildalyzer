using System.IO;
using Buildalyzer.IO;
using Microsoft.Extensions.Logging;
using XenoAtom.MsBuildPipeLogger;

namespace Buildalyzer.Logging;

/// <summary>
/// Turns a stream of build events into <see cref="AnalyzerResult"/>s. Events arrive over the pipe as
/// XenoAtom's MSBuild-free <see cref="PipeBuildEventArgs"/> - both live builds and replayed binary logs run
/// out-of-process and stream their events through the same pipe, so the result-building logic lives in one
/// place regardless of where the events came from.
/// </summary>
internal sealed class EventProcessor : IDisposable
{
    private readonly Dictionary<string, AnalyzerResult> _results = [];
    private readonly Dictionary<int, PropertiesAndItems> _evaluationResults = [];
    private readonly AnalyzerManager _manager;
    private readonly ProjectAnalyzer _analyzer;
    private readonly ILogger<EventProcessor> _logger;
    private readonly bool _analyze;

    private PipeEventDispatcher? _pipeSource;
    private IOPath _projectFilePath;

    // The result each of the primary project's build contexts is building into (one context per inner build
    // when multi-targeting), and by the same token the set of contexts that belong to the primary project at
    // all. A single "current result" stack would be wrong for the same reason a global target stack is:
    // MSBuild interleaves events from different build contexts whenever more than one project builds at a
    // time (/m, and any binary log recorded from such a build), and a multi-targeted project's inner builds
    // are exactly that. A project that starts last can finish first, so stack position says nothing about
    // which result an event belongs to - it would hand one framework's success flag, command line and
    // compiler inputs to another. Every event is resolved through the context it actually came from instead,
    // which also rejects the events raised by referenced projects compiled in the same MSBuild invocation.
    private readonly Dictionary<int, AnalyzerResult?> _resultsByContext = [];

    // The project-context ids that are currently executing CoreCompile. A single global target stack would be
    // wrong here: MSBuild interleaves target events from different build contexts whenever more than one
    // project builds at a time (/m, and any binary log recorded from such a build), so one project's
    // TargetFinished can arrive while another's target is still open. That both mis-attributes compiler task
    // inputs to the wrong CoreCompile and pops a target that never matches the one being finished. Targets do
    // not nest within a single project context, so tracking the contexts that are inside CoreCompile is
    // enough, and every event is then judged against the context it actually came from.
    private readonly HashSet<int> _coreCompileContexts = [];

    public EventProcessor(AnalyzerManager manager, ProjectAnalyzer analyzer, bool analyze)
    {
        _manager = manager;
        _analyzer = analyzer;
        _logger = manager.LoggerFactory?.CreateLogger<EventProcessor>();
        _analyze = analyze;
        _projectFilePath = IOPath.Parse(_analyzer?.ProjectFile.Path).Root();
    }

    public bool OverallSuccess { get; private set; }

    public IEnumerable<AnalyzerResult> Results => _results.Values;

    /// <summary>Subscribes to the build events delivered over the pipe (no MSBuild dependency).</summary>
    public void SubscribePipe(PipeEventDispatcher source)
    {
        _pipeSource = source;
        if (!_analyze)
        {
            return;
        }

        source.ProjectEvaluationFinished += OnPipeEvaluationFinished;
        source.ProjectStarted += OnPipeProjectStarted;
        source.ProjectFinished += OnPipeProjectFinished;
        source.TargetStarted += OnPipeTargetStarted;
        source.TargetFinished += OnPipeTargetFinished;
        source.TaskParameterRaised += OnPipeTaskParameter;
        source.MessageRaised += OnPipeMessage;
        source.ErrorRaised += OnPipeError;
        source.BuildFinished += OnPipeBuildFinished;
    }

    // ----- Core handlers (source-independent) -------------------------------------------------------

    private void OnEvaluationFinished(int evaluationId, PropertiesAndItems propertiesAndItems)
        => _evaluationResults[evaluationId] = propertiesAndItems;

    private void OnProjectStarted(string? projectFile, PropertiesAndItems? propertiesAndItems, int contextId, int? parentContextId)
    {
        var projectPath = IOPath.Parse(projectFile).Root();

        // If we're replaying a binary log and this is the first project we've seen, treat it as the primary.
        if (!_projectFilePath.HasValue)
        {
            _projectFilePath = projectPath;
        }

        // Nested MSBuild tasks may spawn builds of other projects; only track the primary one.
        if (!projectPath.Equals(_projectFilePath))
        {
            // WPF's full Build compiles the primary project's real source set - including the
            // markup-generated GeneratedInternalTypeHelper.g.cs - inside a sibling "*_wpftmp" project
            // spun up by GenerateTemporaryTargetAssembly. That temp build has its own project-context id,
            // so point it at the result of the build that spawned it (the MSBuild task runs it from inside
            // the primary project's build); otherwise its CoreCompile inputs are rejected as another
            // project's and the primary result is left with no source files.
            if (IsPrimaryMarkupCompilation(projectPath)
                && parentContextId is { } parentId
                && _resultsByContext.TryGetValue(parentId, out AnalyzerResult? spawningResult))
            {
                _resultsByContext[contextId] = spawningResult;
            }

            return;
        }

        // NuGet builds its restore graph by evaluating the project again under ExcludeRestorePackageImports,
        // once per framework in TargetFrameworks - and it does that even for a project that pins a single
        // TargetFramework over an imported TargetFrameworks list, restoring every listed framework. Those
        // evaluations never build anything; each would otherwise surface as a phantom target-framework
        // result with no compiler invocation behind it. The restore's own (outer) evaluation does not carry
        // the property and is kept, as target-framework discovery reads TargetFrameworks from it.
        if (IsRestoreGraphEvaluation(propertiesAndItems))
        {
            _resultsByContext[contextId] = null;
            return;
        }

        if (propertiesAndItems is { Properties: { }, Items: { } })
        {
            string key = ResultKey(propertiesAndItems);
            if (!_results.TryGetValue(key, out AnalyzerResult result))
            {
                result = new AnalyzerResult(_projectFilePath.ToString(), _manager, _analyzer);
                _results[key] = result;
            }

            result.ProcessProject(propertiesAndItems);

            // Remember which result this project build's context feeds, so its events can be told apart from
            // those raised by any other project - or inner build - running at the same time.
            _resultsByContext[contextId] = result;
            return;
        }

        // The primary project, but without the evaluation data needed to build a result from it. Record the
        // context anyway so its later events are recognised as the primary project's and skipped, rather
        // than falling through to whichever result happens to be around.
        _resultsByContext[contextId] = null;
    }

    // Keyed by the short TargetFramework rather than the moniker: net8.0 and net8.0-windows share the moniker
    // .NETCoreApp,Version=v8.0, so moniker keys would fold their inner builds into one result. A legacy project
    // has no TargetFramework, only a moniker.
    private static string ResultKey(PropertiesAndItems propertiesAndItems)
        => propertiesAndItems.Properties.TryGet("TargetFramework")?.StringValue is { Length: > 0 } targetFramework
        ? targetFramework
        : propertiesAndItems.Properties.TryGet("TargetFrameworkMoniker")?.StringValue ?? string.Empty;

    private static bool IsRestoreGraphEvaluation(PropertiesAndItems? propertiesAndItems) =>
        propertiesAndItems?.Properties.TryGet("ExcludeRestorePackageImports")?.StringValue is { } value
        && bool.TryParse(value, out bool excluded)
        && excluded;

    // WPF markup compilation compiles the primary project under a generated "<name>_<hash>_wpftmp" project
    // (GenerateTemporaryTargetAssembly), where <name> is the originating project's file name. "_wpftmp" is a
    // PresentationBuildTasks-reserved suffix that a real referenced project never carries, but a referenced
    // WPF project's full Build spins up its own "<referenced>_<hash>_wpftmp" too - so the name must also
    // start with the primary project's name to keep referenced projects' markup compiles out of the result.
    // Legacy WPF drops the temp project beside the original and the SDK drops it under obj/, so match on the
    // name rather than the location.
    private bool IsPrimaryMarkupCompilation(IOPath projectPath)
    {
        StringComparison comparison = IOPath.IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        return projectPath.File() is { } file
            && _projectFilePath.File() is { } primary
            && Path.GetFileNameWithoutExtension(file.Name) is { } name
            && name.EndsWith("_wpftmp", comparison)
            && name.StartsWith(Path.GetFileNameWithoutExtension(primary.Name) + "_", comparison);
    }

    private void OnProjectFinished(string? projectFile, bool succeeded, int contextId)
    {
        // Resolved through this build's own context rather than by finish order: inner builds of a
        // multi-targeted project do not finish in the order they started, so the last result to start is not
        // the one this finish belongs to.
        if (IOPath.Parse(projectFile).Root().Equals(_projectFilePath)
            && _resultsByContext.TryGetValue(contextId, out AnalyzerResult? result))
        {
            result?.Succeeded = succeeded;
        }
    }

    private void OnTargetStarted(string? targetName, int contextId)
    {
        if (targetName == "CoreCompile")
        {
            _coreCompileContexts.Add(contextId);
        }
    }

    private void OnTargetFinished(string? targetName, int contextId)
    {
        if (targetName == "CoreCompile")
        {
            _coreCompileContexts.Remove(contextId);
        }
    }

    private void OnMessage(string? senderName, string? message, string? projectFile, string? commandLineTaskName, string? commandLine, int contextId)
    {
        // Only the primary project's own build contexts have a result; a referenced project compiled in the
        // same invocation raises its own Csc/Fsc messages and must not write to it.
        if (!_resultsByContext.TryGetValue(contextId, out AnalyzerResult? result) || result is null || !IsRelevant())
        {
            return;
        }

        bool coreCompile = _coreCompileContexts.Contains(contextId);

        // F# writes its command line as an Fsc message rather than a task-command-line event.
        if (senderName.IsMatch("Fsc")
            && !string.IsNullOrWhiteSpace(message)
            && coreCompile
            && !result.HasCommandLine)
        {
            result.ProcessFscCommandLine(message);
        }

        if (commandLineTaskName.IsMatch("Csc"))
        {
            result.ProcessCscCommandLine(commandLine, coreCompile);
        }
        else if (commandLineTaskName.IsMatch("Vbc"))
        {
            result.ProcessVbcCommandLine(commandLine);
        }

        bool IsRelevant()
            => result is not { HasCommandLine: true }
            || IOPath.Parse(projectFile).Root().Equals(_projectFilePath);
    }

    private void OnBuildFinished(bool succeeded) => OverallSuccess = succeeded;

    // ----- Pipe (local event) adapters --------------------------------------------------------------

    private void OnPipeEvaluationFinished(PipeProjectEvaluationFinishedEventArgs e)
    {
        if (e.BuildEventContext is { } context)
        {
            OnEvaluationFinished(context.EvaluationId, new PropertiesAndItems
            {
                Properties = CompilerProperties.FromPipeProperties(e.Properties),
                Items = CompilerItemsCollection.FromPipeItems(e.Items),
            });
        }
    }

    private void OnPipeProjectStarted(PipeProjectStartedEventArgs e)
    {
        PropertiesAndItems? propertiesAndItems = e.Properties.Count > 0 || e.Items.Count > 0
            ? new PropertiesAndItems
            {
                Properties = CompilerProperties.FromPipeProperties(e.Properties),
                Items = CompilerItemsCollection.FromPipeItems(e.Items),
            }
            : e.BuildEventContext is { } context && _evaluationResults.TryGetValue(context.EvaluationId, out var existing)
                ? existing
                : null;

        OnProjectStarted(e.ProjectFile, propertiesAndItems, ProjectContextId(e), e.ParentProjectBuildEventContext?.ProjectContextId);
    }

    private void OnPipeProjectFinished(PipeProjectFinishedEventArgs e) => OnProjectFinished(e.ProjectFile, e.Succeeded, ProjectContextId(e));

    // Collect the compiler task's resolved input parameters (structured items with metadata). For live builds
    // the logger has already filtered to CoreCompile's compiler-input item groups; for a replayed binary log
    // every task parameter is forwarded, so we gate on TaskInput kind, item type, and the CoreCompile target.
    // The event's own build context both admits it and picks the result it feeds: referenced projects compiled
    // in the same invocation raise their own compiler-input events (the logger's CoreCompile filter is project-
    // agnostic), and without this check their Sources/References would be merged into the primary result.
    private void OnPipeTaskParameter(PipeTaskParameterEventArgs e)
    {
        if (e.BuildEventContext is not { } context
            || !_resultsByContext.TryGetValue(context.ProjectContextId, out AnalyzerResult? result)
            || result is null)
        {
            return;
        }

        // The compiler task's resolved inputs (Sources/References/...), captured inside CoreCompile. The
        // CoreCompile lookup is scoped to this event's own project context so a concurrently building
        // project's CoreCompile can neither vouch for nor disqualify these inputs.
        if (e.Kind == PipeTaskParameterKind.TaskInput
            && e.ItemType is { Length: > 0 } itemType
            && IsCompilerInput(itemType)
            && _coreCompileContexts.Contains(context.ProjectContextId))
        {
            result.AddTaskParameterInput(itemType, e.Items.Select(ToInputItem));
        }

        // The item groups the SDK resolves before CoreCompile: ResolveAssemblyReference's ReferencePath, and
        // the analyzers ResolveTargetingPackAssets adds to Analyzer and ResolvePackageAssets outputs as
        // ResolvedAnalyzers. Kept so the workspace can still be reconstructed when the build fails before the
        // compiler runs (issue #341). On a successful build the compiler task inputs above supersede them.
        else if (e.Kind == PipeTaskParameterKind.TaskOutput
            && e.ItemType is { Length: > 0 } outputType
            && IsResolvedBeforeCompile(outputType))
        {
            result.AddItems(outputType, e.Items.Select(item => (IProjectItem)new PipeProjectItem(item)));
        }
    }

    private static CompilerInputItem ToInputItem(PipeItem item)
        => new(item.EvaluatedInclude, item.Metadata.Select(m => (m.Name, m.Value)).ToArray());

    private static bool IsCompilerInput(string itemType) => itemType is
        "Sources" or "References" or "Analyzers" or "AdditionalFiles" or "AnalyzerConfigFiles" or "EmbeddedFiles";

    // MSBuild item names are case-insensitive, unlike the compiler task's parameter names above.
    private static bool IsResolvedBeforeCompile(string itemType) =>
        string.Equals(itemType, "ReferencePath", StringComparison.OrdinalIgnoreCase)
        || string.Equals(itemType, "Analyzer", StringComparison.OrdinalIgnoreCase)
        || string.Equals(itemType, "ResolvedAnalyzers", StringComparison.OrdinalIgnoreCase);

    private void OnPipeTargetStarted(PipeTargetStartedEventArgs e) => OnTargetStarted(e.TargetName, ProjectContextId(e));

    private void OnPipeTargetFinished(PipeTargetFinishedEventArgs e) => OnTargetFinished(e.TargetName, ProjectContextId(e));

    private void OnPipeMessage(PipeBuildMessageEventArgs e)
    {
        var commandLine = e as PipeTaskCommandLineEventArgs;
        OnMessage(e.SenderName, e.Message, e.ProjectFile, commandLine?.TaskName, commandLine?.CommandLine, ProjectContextId(e));
    }

    // Events without a build context are grouped under a single sentinel id rather than dropped, so a logger
    // that omits the context still pairs its target and task events with each other.
    //
    // The id alone is a safe key even for /m builds and their binlogs: a project-context id is unique across
    // the whole build, not just its own node. MSBuild's LoggingService seeds each node's counter with the
    // node id and advances it by MaxCPUCount + 2, so nodes allocate from disjoint ranges and never collide.
    private static int ProjectContextId(PipeBuildEventArgs e)
        => e.BuildEventContext is { } context ? context.ProjectContextId : int.MinValue;

    // Surface build errors in the log. The pipe logger forwards ErrorRaised precisely so a failed build can
    // say why; without a subscriber the reason is dropped and a failure shows only as a nonzero exit code.
    private void OnPipeError(PipeBuildErrorEventArgs e)
        => _logger?.LogError(
            "{File}({Line},{Column}): error {Code}: {Message}",
            string.IsNullOrEmpty(e.File) ? e.ProjectFile : e.File,
            e.LineNumber,
            e.ColumnNumber,
            e.Code,
            e.Message);

    private void OnPipeBuildFinished(PipeBuildFinishedEventArgs e) => OnBuildFinished(e.Succeeded);

    public void Dispose()
    {
        if (_analyze && _pipeSource is { } pipe)
        {
            pipe.ProjectEvaluationFinished -= OnPipeEvaluationFinished;
            pipe.ProjectStarted -= OnPipeProjectStarted;
            pipe.ProjectFinished -= OnPipeProjectFinished;
            pipe.TargetStarted -= OnPipeTargetStarted;
            pipe.TargetFinished -= OnPipeTargetFinished;
            pipe.TaskParameterRaised -= OnPipeTaskParameter;
            pipe.MessageRaised -= OnPipeMessage;
            pipe.ErrorRaised -= OnPipeError;
            pipe.BuildFinished -= OnPipeBuildFinished;
        }
    }
}
