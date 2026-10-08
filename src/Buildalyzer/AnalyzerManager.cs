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
    private readonly ConcurrentDictionary<string, IProjectAnalyzer> _projects = new(IOPath.Comparer);

    public IReadOnlyDictionary<string, IProjectAnalyzer> Projects => _projects;

    public ILoggerFactory? LoggerFactory { get; set; }

    internal ConcurrentDictionary<string, string> GlobalProperties { get; } = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal ConcurrentDictionary<string, string> EnvironmentVariables { get; } = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public SolutionInfo? Solution { get; }

    /// <summary>Resolves (and caches) <c>dotnet --info</c> for every project this manager builds.</summary>
    internal DotNetInfoResolver DotNetInfoResolver { get; } = new();

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

    // Rooted here, at the public boundary: the analyzers are keyed by path, and every other way a project
    // enters the manager (a solution's ProjectInfo, a resolved project reference) supplies a full path. A
    // caller passing "src/Foo/Foo.csproj" must reach the same analyzer as one passing its absolute path,
    // not silently get a second one.
    public IProjectAnalyzer? GetProject(string projectFilePath) => GetProject(IOPath.Parse(projectFilePath).Root(), null);

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
            // Unset the MSBuild discovery variables rather than let the child inherit them - the dotnet
            // muxer does not override them, so whatever the host had wins. A consumer that has registered
            // MSBuildLocator has them pointing at its own in-process MSBuild, which would aim this replay at
            // a different MSBuild than the muxer resolves: mismatched assemblies and SDK paths, the very
            // thing replaying out-of-process is meant to rule out. A null value means "unset".
            new Dictionary<string, string?>
            {
                [Environment.EnvironmentVariables.MSBUILD_EXE_PATH] = null,
                [Environment.EnvironmentVariables.MSBuildExtensionsPath] = null,
                [Environment.EnvironmentVariables.MSBuildSDKsPath] = null,
            },
            LoggerFactory);

        processRunner.Start();

        // The drain ends the read once the replay process has gone, including the case where it exits
        // without ever writing to the pipe (an invalid binary log, or a startup failure): the server keeps
        // its own copy of the client handle open there, so the read never sees the end of the file.
        PipeLoggerDrain.ReadUntilExit(pipeLogger, processRunner, eventCollector);

        // A replay that raised no event at all never read the log: one written by a newer MSBuild than the
        // one replaying it, a corrupt file, or a startup failure. Every binary log opens with BuildStarted,
        // so an empty pipe is unambiguous - and it is the only signal there is, as MSBuild exits with 0 from
        // a failed replay and merely prints the reason ("There was an exception while reading the log
        // file: ...") to its standard output. That is an error to surface, as reading the log in-process
        // did, not an empty result set to hand back.
        if (eventCollector.IsEmpty)
        {
            // The process has exited but its output handlers may still be delivering the lines that say why;
            // the drain's settle window does not cover them, as the pipe read ends the moment the logger shuts
            // down. The wait is bounded because the streams need not close at all (see ProcessRunner.WaitForExit).
            processRunner.WaitForOutput(OutputGracePeriod);
            ProcessData data = processRunner.Data;
            string[] output = [.. data.Output, .. data.Error];
            throw new InvalidOperationException(
                $"MSBuild could not replay the binary log {path} (exit code {processRunner.ExitCode})."
                + (output.Length > 0 ? $" MSBuild reported:{System.Environment.NewLine}{string.Join(System.Environment.NewLine, output)}" : string.Empty));
        }

        // The exit code counts towards the outcome as it does for a live build.
        return new AnalyzerResults
        {
            { eventProcessor.Results, processRunner.ExitCode == 0 && eventProcessor.OverallSuccess }
        };
    }

    /// <summary>How long to wait for a replay process' output to finish arriving once it has exited.</summary>
    private const int OutputGracePeriod = 2_000;

    private IProjectAnalyzer? GetProject(IOPath path, ProjectInfo? project)
        => (Guard.NotDefault(path).File(), project) switch
        {
            // The analyzer is created by the factory overload rather than passed in: constructing one
            // reads (and, for a file-based app, asks the SDK to generate) the project, which is work a
            // repeated lookup should not redo.
            ({ Exists: true }, _) => _projects.GetOrAdd(path.ToString(), _ => new ProjectAnalyzer(this, path, project)),
            (_, not null) => null,
            _ => throw new ArgumentException($"The path {path} could not be found."),
        };
}
