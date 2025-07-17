extern alias StructuredLogger;
using Microsoft.Build.Framework;
using Microsoft.Extensions.Logging;

namespace Buildalyzer.Logging;

internal class EventProcessor : IDisposable
{
    private readonly Dictionary<string, AnalyzerResult> _results = [];
    private readonly Stack<AnalyzerResult> _currentResult = new();
    private readonly Stack<TargetStartedEventArgs> _targetStack = new();
    private readonly Dictionary<int, PropertiesAndItems> _evalulationResults = [];
    private readonly AnalyzerManager _manager;
    private readonly ProjectAnalyzer _analyzer;
    private readonly ILogger<EventProcessor> _logger;
    private readonly IEnumerable<Microsoft.Build.Framework.ILogger> _buildLoggers;
    private readonly IEventSource _eventSource;
    private readonly bool _analyze;

    private string _projectFilePath;

    public bool OverallSuccess { get; private set; }

    public IEnumerable<AnalyzerResult> Results => _results.Values;

    public EventProcessor(AnalyzerManager manager, ProjectAnalyzer analyzer, IEnumerable<Microsoft.Build.Framework.ILogger> buildLoggers, IEventSource eventSource, bool analyze)
    {
        _manager = manager;
        _analyzer = analyzer;
        _logger = manager.LoggerFactory?.CreateLogger<EventProcessor>();
        _buildLoggers = buildLoggers ?? Array.Empty<Microsoft.Build.Framework.ILogger>();
        _eventSource = eventSource;
        _analyze = analyze;

        _projectFilePath = _analyzer?.ProjectFile.Path;

        // Initialize the loggers
        foreach (Microsoft.Build.Framework.ILogger buildLogger in _buildLoggers)
        {
            buildLogger.Initialize(eventSource);
        }

        // Send events to the tree constructor
        if (analyze)
        {
            eventSource.StatusEventRaised += StatusEventRaised;
            eventSource.ProjectStarted += ProjectStarted;
            eventSource.ProjectFinished += ProjectFinished;
            eventSource.TargetStarted += TargetStarted;
            eventSource.TargetFinished += TargetFinished;
            eventSource.MessageRaised += MessageRaised;
            eventSource.BuildFinished += BuildFinished;
            if (_logger != null)
            {
                eventSource.ErrorRaised += ErrorRaised;
            }
        }
    }

    // In binlog 14 we need to gather properties and items during evaluation and "glue" them with the project event args
    // But can never remove ProjectStarted: "even v14 will log them on ProjectStarted if any legacy loggers are present (for compat)"
    // See https://twitter.com/KirillOsenkov/status/1427686459713019904
    private void StatusEventRaised(object sender, BuildStatusEventArgs e)
    {
        if (e is ProjectEvaluationFinishedEventArgs slEv)
        {
            _evalulationResults[slEv.BuildEventContext.EvaluationId] = new PropertiesAndItems
            {
                Properties = CompilerProperties.FromDictionaryEntries(slEv.Properties),
                Items = CompilerItemsCollection.FromDictionaryEntries(slEv.Items),
            };
        }
    }

    private void ProjectStarted(object sender, ProjectStartedEventArgs e)
    {
        // If we're not using an analyzer (I.e., from a binary log) and this is the first project file path we've seen, then it's the primary
        if (_projectFilePath == null)
        {
            _projectFilePath = AnalyzerManager.NormalizePath(e.ProjectFile);
        }

        // Make sure this is the same project, nested MSBuild tasks may have spawned additional builds of other projects
        if (AnalyzerManager.NormalizePath(e.ProjectFile) == _projectFilePath)
        {
            // Get the items and properties from the evaluation if needed
            PropertiesAndItems propertiesAndItems = e.Properties is null
                ? (_evalulationResults.TryGetValue(e.BuildEventContext.EvaluationId, out PropertiesAndItems evaluationResult)
                    ? evaluationResult
                    : null)
                : new PropertiesAndItems
                {
                    Properties = CompilerProperties.FromDictionaryEntries(e.Properties),
                    Items = CompilerItemsCollection.FromDictionaryEntries(e.Items),
                };

            // Get the TFM for this project
            // use an empty string if no target framework was found, for example in case of C++ projects with VS >= 2022
            string tfm = propertiesAndItems?.Properties.TryGet("TargetFrameworkMoniker")?.StringValue
                ?? string.Empty;

            if (propertiesAndItems != null && propertiesAndItems.Properties != null && propertiesAndItems.Items != null)
            {
                if (!_results.TryGetValue(tfm, out AnalyzerResult result))
                {
                    result = new AnalyzerResult(_projectFilePath, _manager, _analyzer);
                    _results[tfm] = result;
                }
                result.ProcessProject(propertiesAndItems);
                _currentResult.Push(result);
                return;
            }

            // Push a null result so the stack is balanced on project finish
            _currentResult.Push(null);
        }
    }

    private void ProjectFinished(object sender, ProjectFinishedEventArgs e)
    {
        // Make sure this is the same project, nested MSBuild tasks may have spawned additional builds of other projects
        if (AnalyzerManager.NormalizePath(e.ProjectFile) == _projectFilePath)
        {
            AnalyzerResult result = _currentResult.Pop();
            if (result != null)
            {
                result.Succeeded = e.Succeeded;
            }
        }
    }

    private void TargetStarted(object sender, TargetStartedEventArgs e)
    {
        _targetStack.Push(e);
    }

    private void TargetFinished(object sender, TargetFinishedEventArgs e)
    {
        if (_targetStack.Pop().TargetName != e.TargetName)
        {
            // Sanity check
            throw new InvalidOperationException("Mismatched target events");
        }
    }

    private void MessageRaised(object sender, BuildMessageEventArgs e)
    {
        var result = _currentResult.Count == 0 ? null : _currentResult.Peek();
        if (result is not object || !IsRelevant())
        {
            return;
        }

        // Process the command line arguments for the Fsc task
        if (e.SenderName.IsMatch("Fsc")
            && !string.IsNullOrWhiteSpace(e.Message)
            && _targetStack.Any(x => x.TargetName == "CoreCompile")
            && result.CompilerCommand is null)
        {
            result.ProcessFscCommandLine(e.Message);
        }

        // Process the command line arguments for the Csc task
        if (e is TaskCommandLineEventArgs cmd && cmd.TaskName.IsMatch("Csc"))
        {
            result.ProcessCscCommandLine(cmd.CommandLine, _targetStack.Any(x => x.TargetName == "CoreCompile"));
        }

        if (e is TaskCommandLineEventArgs cmdVbc && cmdVbc.TaskName.IsMatch("Vbc"))
        {
            result.ProcessVbcCommandLine(cmdVbc.CommandLine);
        }

        bool IsRelevant() => string.IsNullOrEmpty(result.Command) || AnalyzerManager.NormalizePath(e.ProjectFile) == _projectFilePath;
    }

    private void BuildFinished(object sender, BuildFinishedEventArgs e)
    {
        OverallSuccess = e.Succeeded;
    }

    private void ErrorRaised(object sender, BuildErrorEventArgs e) => _logger.LogError(e.Message);

    public void Dispose()
    {
        if (_analyze)
        {
            _eventSource.ProjectStarted -= ProjectStarted;
            _eventSource.ProjectFinished -= ProjectFinished;
            _eventSource.TargetStarted -= TargetStarted;
            _eventSource.TargetFinished -= TargetFinished;
            _eventSource.MessageRaised -= MessageRaised;
            _eventSource.BuildFinished -= BuildFinished;
            if (_logger != null)
            {
                _eventSource.ErrorRaised -= ErrorRaised;
            }
        }

        // Need to release the loggers in case they get used again (I.e., Restore followed by Clean;Build)
        foreach (Microsoft.Build.Framework.ILogger buildLogger in _buildLoggers)
        {
            buildLogger.Shutdown();
        }
    }
}