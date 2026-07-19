using System.Collections.Concurrent;
using System.Threading;
using Buildalyzer.Environment;
using Buildalyzer.IO;
using Buildalyzer.Logging;
using Microsoft.Extensions.Logging;
using XenoAtom.MsBuildPipeLogger;

namespace Buildalyzer;

public class AnalyzerManager : IAnalyzerManager
{
    // Match project paths the way the current file system does (ordinal on case-sensitive
    // file systems, case-insensitive elsewhere) so the same project isn't tracked twice when
    // its path arrives with different casing.
    private readonly ConcurrentDictionary<string, IProjectAnalyzer> _projects
        = new(IOPath.IsCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IProjectAnalyzer> Projects => _projects;

    public ILoggerFactory? LoggerFactory { get; set; }

    internal ConcurrentDictionary<string, string> GlobalProperties { get; } = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal ConcurrentDictionary<string, string> EnvironmentVariables { get; } = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public SolutionInfo? Solution { get; }

    public AnalyzerManager(AnalyzerManagerOptions? options = null)
        : this(IOPath.Empty, options)
    {
    }

    public AnalyzerManager(string solutionFilePath, AnalyzerManagerOptions? options = null)
        : this(IOPath.Parse(solutionFilePath), options)
    {
    }

    // Paths are normalized to IOPath here, at the public boundary; IOPath is an internal
    // detail and is deliberately kept out of the consumer-facing input surface.
    private AnalyzerManager(IOPath solutionFilePath, AnalyzerManagerOptions? options)
    {
        options ??= new AnalyzerManagerOptions();
        LoggerFactory = options.LoggerFactory;

        if (solutionFilePath.HasValue)
        {
            Solution = SolutionInfo.Load(solutionFilePath, p => options.ProjectFilter?.Invoke(p) ?? true);

            // init projects.
            foreach (var proj in Solution)
            {
                var analyzer = new ProjectAnalyzer(this, proj.Location, proj);
                _projects.TryAdd(proj.Path, analyzer);
            }
        }
    }

    public void SetGlobalProperty(string key, string value)
    {
        GlobalProperties[key] = value;
    }

    public void RemoveGlobalProperty(string key)
    {
        // Nulls are removed before passing to MSBuild and can be used to ignore values in lower-precedence collections
        GlobalProperties[key] = null;
    }

    public void SetEnvironmentVariable(string key, string value)
    {
        EnvironmentVariables[key] = value;
    }

    public IProjectAnalyzer? GetProject(string projectFilePath) => GetProject(IOPath.Parse(projectFilePath), null);

    /// <inheritdoc/>
    public IAnalyzerResults Analyze(string binLogPath)
    {
        var path = IOPath.Parse(binLogPath).Root();
        if (path.File() is not { Exists: true } file)
        {
            throw new ArgumentException($"The path {path} could not be found.");
        }

        // Replay the binary log by handing it to MSBuild on the command line with the pipe logger
        // attached. MSBuild reads and replays the log out-of-process, so its version always matches
        // whatever wrote the log (no in-process reader, no MSBuildLocator, no version mismatch). The
        // replayed events stream over the pipe as XenoAtom PipeBuildEventArgs, through the same
        // EventProcessor the live build uses.
        using var cancellation = new CancellationTokenSource();
        using var pipeLogger = new AnonymousPipeLoggerServer(cancellation.Token);
        using var eventCollector = new BuildEventArgsCollector(pipeLogger);
        using var eventProcessor = new EventProcessor(this, null, true);
        eventProcessor.SubscribePipe(pipeLogger);

        var loggerArgument = BuildArgument.Logger(
            isDotNet: true,
            new LoggerConfiguration
            {
                ClientHandle = pipeLogger.GetClientHandle(),
                LogEverything = true,
            });

        var arguments = string.Join(
            ' ',
            "msbuild",
            BuildArgument.Path(path),
            BuildArgument.NoConsoleLogger,
            loggerArgument);

        using var processRunner = new ProcessRunner(
            "dotnet",
            arguments,
            file.DirectoryName ?? System.Environment.CurrentDirectory,
            new Dictionary<string, string?>(),
            LoggerFactory);

        // If MSBuild exits without ever writing to the pipe (e.g. an invalid binary log or a startup
        // failure), the server keeps its client handle open so the read never sees EOF. Dispose the
        // logger on such an exit to unblock ReadAll rather than hang indefinitely.
        void OnProcessRunnerExited()
        {
            if (eventCollector.IsEmpty && processRunner.ExitCode != 0)
            {
                pipeLogger.Dispose();
            }
        }

        processRunner.Exited += OnProcessRunnerExited;
        processRunner.Start();
        try
        {
            pipeLogger.ReadAll();
        }
        catch (ObjectDisposedException)
        {
            // Ignore
        }

        processRunner.WaitForExit();

        return new AnalyzerResults
        {
            { eventProcessor.Results, eventProcessor.OverallSuccess }
        };
    }

    private IProjectAnalyzer? GetProject(IOPath path, ProjectInfo? project)
        => (Guard.NotDefault(path).File(), project) switch
        {
            ({ Exists: true }, _) => _projects.GetOrAdd(path.ToString(), new ProjectAnalyzer(this, path, project)),
            (_, not null) => null,
            _ => throw new ArgumentException($"The path {path} could not be found."),
        };
}
