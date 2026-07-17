using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Buildalyzer.Environment;
using Buildalyzer.IO;
using Buildalyzer.Logging;
using Microsoft.Extensions.Logging;
using XenoAtom.MsBuildPipeLogger;

namespace Buildalyzer;

public class AnalyzerManager : IAnalyzerManager
{
    private readonly ConcurrentDictionary<string, IProjectAnalyzer> _projects = new();

    public IReadOnlyDictionary<string, IProjectAnalyzer> Projects => _projects;

    public ILoggerFactory? LoggerFactory { get; set; }

    internal ConcurrentDictionary<string, string> GlobalProperties { get; } = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal ConcurrentDictionary<string, string> EnvironmentVariables { get; } = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// This maps Roslyn project IDs to full normalized project file paths of references (since the Roslyn Project doesn't provide access to this data)
    /// which allows us to match references with Roslyn projects that already exist in the Workspace/Solution (instead of rebuilding them).
    /// This cache exists in <see cref="AnalyzerManager"/> so that it's lifetime can be controlled and it can be collected when <see cref="AnalyzerManager"/> goes out of scope.
    /// </summary>
#pragma warning disable SA1401 // Fields should be private
    internal ConcurrentDictionary<Guid, string[]> WorkspaceProjectReferences = new();
#pragma warning restore SA1401 // Fields should be private

    public string? SolutionFilePath => Solution?.Path.ToString();

    public SolutionInfo? Solution { get; }

    public AnalyzerManager(AnalyzerManagerOptions? options = null)
        : this(IOPath.Empty, options)
    {
    }

    [Obsolete("Use AnalyzerManager(IOPath, AnalyzerManagerOptions) instead.")]
    public AnalyzerManager(string solutionFilePath, AnalyzerManagerOptions? options = null)
        : this(IOPath.Parse(solutionFilePath), options) { }

    public AnalyzerManager(IOPath solutionFilePath, AnalyzerManagerOptions? options = null)
    {
        options ??= new AnalyzerManagerOptions();
        LoggerFactory = options.LoggerFactory;

        if (solutionFilePath.HasValue)
        {
            Solution = SolutionInfo.Load(solutionFilePath, p => options.ProjectFilter?.Invoke(p) ?? true);

            // init projects.
            foreach (var proj in Solution)
            {
                var analyzer = new ProjectAnalyzer(this, proj.Path, proj);
                _projects.TryAdd(proj.Path.ToString(), analyzer);
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

    [Obsolete("Use GetProject(IOPath) instead.")]
    public IProjectAnalyzer? GetProject(string projectFilePath) => GetProject(IOPath.Parse(projectFilePath));

    public IProjectAnalyzer? GetProject(IOPath projectFilePath) => GetProject(projectFilePath, null);

    /// <inheritdoc/>
    public IAnalyzerResults Analyze(string binLogPath)
    {
        binLogPath = NormalizePath(binLogPath);
        if (!File.Exists(binLogPath))
        {
            throw new ArgumentException($"The path {binLogPath} could not be found.");
        }

        // Replay the binary log by handing it to MSBuild on the command line with the pipe logger
        // attached. MSBuild reads and replays the log out-of-process, so its version always matches
        // whatever wrote the log (no in-process reader, no MSBuildLocator, no version mismatch). The
        // replayed events stream over the pipe as XenoAtom PipeBuildEventArgs, through the same
        // EventProcessor the live build uses.
        using var cancellation = new CancellationTokenSource();
        using var pipeLogger = new AnonymousPipeLoggerServer(cancellation.Token);
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
            BuildArgument.Path(IOPath.Parse(binLogPath)),
            BuildArgument.NoConsoleLogger,
            loggerArgument);

        using var processRunner = new ProcessRunner(
            "dotnet",
            arguments,
            Path.GetDirectoryName(binLogPath) ?? System.Environment.CurrentDirectory,
            new Dictionary<string, string?>(),
            LoggerFactory);

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

    [Obsolete("Use IOPath instead.")]
    internal static string? NormalizePath(string? path) =>
        path == null ? null : Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
}
