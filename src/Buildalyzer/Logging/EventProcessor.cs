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
    private readonly Stack<AnalyzerResult> _currentResult = new();
    private readonly Stack<string> _targetStack = new();
    private readonly Dictionary<int, PropertiesAndItems> _evaluationResults = [];
    private readonly AnalyzerManager _manager;
    private readonly ProjectAnalyzer _analyzer;
    private readonly ILogger<EventProcessor> _logger;
    private readonly bool _analyze;

    private PipeEventDispatcher? _pipeSource;
    private IOPath _projectFilePath;

    // The project-context ids of the primary project's builds (one per inner build when multi-targeting).
    // Used to attribute compiler task-input events to the primary result and reject those raised by
    // referenced projects that are compiled in the same MSBuild invocation.
    private readonly HashSet<int> _primaryProjectContextIds = [];

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
        source.BuildFinished += OnPipeBuildFinished;
    }

    // ----- Core handlers (source-independent) -------------------------------------------------------

    private void OnEvaluationFinished(int evaluationId, PropertiesAndItems propertiesAndItems)
        => _evaluationResults[evaluationId] = propertiesAndItems;

    private void OnProjectStarted(string? projectFile, PropertiesAndItems? propertiesAndItems, int? projectContextId)
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
            // so register it as primary; otherwise OnPipeTaskParameter rejects its CoreCompile inputs and
            // the primary result is left with no source files.
            if (projectContextId is { } tempContextId && IsPrimaryMarkupCompilation(projectPath))
            {
                _primaryProjectContextIds.Add(tempContextId);
            }

            return;
        }

        // Remember this project build's context so its compiler task-input events can be told apart from
        // those raised by referenced projects that build in the same invocation (see OnPipeTaskParameter).
        if (projectContextId is { } contextId)
        {
            _primaryProjectContextIds.Add(contextId);
        }

        string tfm = propertiesAndItems?.Properties.TryGet("TargetFrameworkMoniker")?.StringValue ?? string.Empty;

        if (propertiesAndItems is { Properties: { }, Items: { } })
        {
            if (!_results.TryGetValue(tfm, out AnalyzerResult result))
            {
                result = new AnalyzerResult(_projectFilePath.ToString(), _manager, _analyzer);
                _results[tfm] = result;
            }

            result.ProcessProject(propertiesAndItems);
            _currentResult.Push(result);
            return;
        }

        // Push a null result so the stack stays balanced on project finish.
        _currentResult.Push(null);
    }

    // WPF markup compilation compiles the primary project under a generated "<name>_<hash>_wpftmp" project
    // (GenerateTemporaryTargetAssembly). "_wpftmp" is a PresentationBuildTasks-reserved suffix that a real
    // referenced project never carries, so it cleanly tells the primary's own markup compile apart from a
    // referenced project that we must keep out of the result. Legacy WPF drops the temp project beside the
    // original and the SDK drops it under obj/, so match on the suffix rather than the location.
    private static bool IsPrimaryMarkupCompilation(IOPath projectPath)
        => projectPath.File() is { } file
        && Path.GetFileNameWithoutExtension(file.Name)
            .EndsWith("_wpftmp", IOPath.IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private void OnProjectFinished(string? projectFile, bool succeeded)
    {
        if (IOPath.Parse(projectFile).Root().Equals(_projectFilePath))
        {
            AnalyzerResult result = _currentResult.Pop();
            result?.Succeeded = succeeded;
        }
    }

    private void OnTargetStarted(string? targetName) => _targetStack.Push(targetName ?? string.Empty);

    private void OnTargetFinished(string? targetName)
    {
        if (_targetStack.Pop() != (targetName ?? string.Empty))
        {
            throw new InvalidOperationException("Mismatched target events");
        }
    }

    private void OnMessage(string? senderName, string? message, string? projectFile, string? commandLineTaskName, string? commandLine)
    {
        if (!_currentResult.TryPeek(out var result) || !IsRelevant())
        {
            return;
        }

        // F# writes its command line as an Fsc message rather than a task-command-line event.
        if (senderName.IsMatch("Fsc")
            && !string.IsNullOrWhiteSpace(message)
            && _targetStack.Any(x => x == "CoreCompile")
            && !result.HasCommandLine)
        {
            result.ProcessFscCommandLine(message);
        }

        if (commandLineTaskName.IsMatch("Csc"))
        {
            result.ProcessCscCommandLine(commandLine, _targetStack.Any(x => x == "CoreCompile"));
        }
        else if (commandLineTaskName.IsMatch("Vbc"))
        {
            result.ProcessVbcCommandLine(commandLine);
        }

        bool IsRelevant()
            => !result.HasCommandLine
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

        OnProjectStarted(e.ProjectFile, propertiesAndItems, e.BuildEventContext?.ProjectContextId);
    }

    private void OnPipeProjectFinished(PipeProjectFinishedEventArgs e) => OnProjectFinished(e.ProjectFile, e.Succeeded);

    // Collect the compiler task's resolved input parameters (structured items with metadata). For live builds
    // the logger has already filtered to CoreCompile's compiler-input item groups; for a replayed binary log
    // every task parameter is forwarded, so we gate on TaskInput kind, item type, and the CoreCompile target.
    // The event must also originate from the primary project's build context: referenced projects compiled in
    // the same invocation raise their own compiler-input events (the logger's CoreCompile filter is project-
    // agnostic), and without this check their Sources/References would be merged into the primary result.
    private void OnPipeTaskParameter(PipeTaskParameterEventArgs e)
    {
        if (e.BuildEventContext is not { } context
            || !_primaryProjectContextIds.Contains(context.ProjectContextId)
            || !_currentResult.TryPeek(out var result)
            || result is null)
        {
            return;
        }

        // The compiler task's resolved inputs (Sources/References/...), captured inside CoreCompile.
        if (e.Kind == PipeTaskParameterKind.TaskInput
            && e.ItemType is { Length: > 0 } itemType
            && IsCompilerInput(itemType)
            && _targetStack.Any(x => x == "CoreCompile"))
        {
            result.AddTaskParameterInput(itemType, e.Items.Select(ToInputItem));
        }

        // ResolveAssemblyReference's resolved references, produced before CoreCompile. Kept so the workspace
        // can still be reconstructed when the build fails before the compiler runs (issue #341). On a
        // successful build the compiler task inputs above supersede this.
        else if (e.Kind == PipeTaskParameterKind.TaskOutput
            && string.Equals(e.ItemType, "ReferencePath", StringComparison.OrdinalIgnoreCase))
        {
            result.AddItems("ReferencePath", e.Items.Select(item => (IProjectItem)new PipeProjectItem(item)));
        }
    }

    private static CompilerInputItem ToInputItem(PipeItem item)
        => new(item.EvaluatedInclude, item.Metadata.Select(m => (m.Name, m.Value)).ToArray());

    private static bool IsCompilerInput(string itemType) => itemType is
        "Sources" or "References" or "Analyzers" or "AdditionalFiles" or "AnalyzerConfigFiles" or "EmbeddedFiles";

    private void OnPipeTargetStarted(PipeTargetStartedEventArgs e) => OnTargetStarted(e.TargetName);

    private void OnPipeTargetFinished(PipeTargetFinishedEventArgs e) => OnTargetFinished(e.TargetName);

    private void OnPipeMessage(PipeBuildMessageEventArgs e)
    {
        var commandLine = e as PipeTaskCommandLineEventArgs;
        OnMessage(e.SenderName, e.Message, e.ProjectFile, commandLine?.TaskName, commandLine?.CommandLine);
    }

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
            pipe.BuildFinished -= OnPipeBuildFinished;
        }
    }
}
