using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Buildalyzer.Construction;
using Buildalyzer.Environment;
using Buildalyzer.IO;
using Buildalyzer.Logging;
using Microsoft.Extensions.Logging;
using XenoAtom.MsBuildPipeLogger;

namespace Buildalyzer;

public class ProjectAnalyzer : IProjectAnalyzer
{
    // Binary logs requested via AddBinaryLogger. MSBuild writes these natively via /bl (full fidelity,
    // SDK version) rather than an in-process BinaryLogger, and they're read back by replaying them
    // through MSBuild on the command line (see AnalyzerManager.Analyze).
    private readonly List<(string Path, string ImportsMode)> _binaryLogPaths = [];

    // When a build is one of several in succession (restore, or per-TFM builds), each binlog path is
    // suffixed (e.g. ".restore" or ".net8.0") so the invocations don't overwrite one another.
    private string? _binaryLogSuffix;

    // Project-specific global properties and environment variables
    private readonly ConcurrentDictionary<string, string> _globalProperties = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, string> _environmentVariables = new(StringComparer.OrdinalIgnoreCase);

    public AnalyzerManager Manager { get; }

    public IProjectFile ProjectFile { get; }

    public EnvironmentFactory EnvironmentFactory { get; }

    public string SolutionDirectory { get; }

    public ProjectInfo? Project { get; }

    /// <inheritdoc/>
    public Guid ProjectGuid { get; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> GlobalProperties => GetEffectiveGlobalProperties(null);

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> EnvironmentVariables => GetEffectiveEnvironmentVariables(null);

    public ILogger<ProjectAnalyzer> Logger { get; set; }

    /// <inheritdoc/>
    public bool IgnoreFaultyImports { get; set; } = true;

    // The project file path should already be normalized
    internal ProjectAnalyzer(AnalyzerManager manager, IOPath path, ProjectInfo? project)
    {
        Manager = manager;
        Logger = Manager.LoggerFactory?.CreateLogger<ProjectAnalyzer>();
        ProjectFile = new ProjectFile(path.ToString());
        EnvironmentFactory = new EnvironmentFactory(Manager, ProjectFile);
        Project = project;
        string? solutionFilePath = manager.Solution?.Path;
        SolutionDirectory = (string.IsNullOrEmpty(solutionFilePath)
            ? path.File()!.Directory.FullName : Path.GetDirectoryName(solutionFilePath)) + Path.DirectorySeparatorChar;

        // Get(or create) a project GUID
        ProjectGuid = project?.Guid ?? Buildalyzer.ProjectGuid.Create(ProjectFile.Name);

        // Set the solution directory global property
        SetGlobalProperty(MsBuildProperties.SolutionDir, SolutionDirectory);
    }

    /// <inheritdoc/>
    public IAnalyzerResults Build(string[] targetFrameworks) =>
        Build(targetFrameworks, new EnvironmentOptions());

    /// <inheritdoc/>
    public IAnalyzerResults Build(string[] targetFrameworks, EnvironmentOptions environmentOptions)
    {
        Guard.NotNull(environmentOptions);

        // If the set of target frameworks is empty, just build the default
        if (targetFrameworks == null || targetFrameworks.Length == 0)
        {
            targetFrameworks = [null];
        }

        AnalyzerResults results = [];

        // Builds that pin a target framework can't restore themselves (see Restore), so run
        // a single up-front restore with the project's own (outer) build environment. It
        // produces an assets file covering every framework, so one restore is enough.
        bool restore = environmentOptions.Restore && targetFrameworks.Any(t => t is not null);
        if (restore && !Restore(EnvironmentFactory.GetBuildEnvironment(null, environmentOptions), results))
        {
            return results;
        }

        // Create a new build environment for each target; multiple targets build in parallel.
        BuildTargetsPerFramework(
            targetFrameworks,
            targetFramework =>
            {
                BuildEnvironment buildEnvironment = EnvironmentFactory.GetBuildEnvironment(targetFramework, environmentOptions);
                return restore ? buildEnvironment.WithRestore(false) : buildEnvironment;
            },
            results);

        return results;
    }

    /// <inheritdoc/>
    public IAnalyzerResults Build(string[] targetFrameworks, BuildEnvironment buildEnvironment)
    {
        Guard.NotNull(buildEnvironment);

        // If the set of target frameworks is empty, just build the default
        if (targetFrameworks == null || targetFrameworks.Length == 0)
        {
            targetFrameworks = [null];
        }

        AnalyzerResults results = [];

        // Builds that pin a target framework can't restore themselves (see Restore), so run
        // a single up-front restore covering every framework.
        if (buildEnvironment.Restore && targetFrameworks.Any(t => t is not null))
        {
            if (!Restore(buildEnvironment, results))
            {
                return results;
            }
            buildEnvironment = buildEnvironment.WithRestore(false);
        }

        BuildTargetsPerFramework(targetFrameworks, _ => buildEnvironment, results);

        return results;
    }

    // Restore is a per-project operation that belongs to the outer build: restoring with the
    // TargetFramework global property pinned executes the inner build's restore instead,
    // writing an assets file that covers only that framework and breaking any other
    // framework's build with NETSDK1005 (#346). So builds that pin a target framework never
    // use the -restore switch; the Restore target runs in this separate up-front invocation
    // that doesn't pin TargetFramework. Builds without a pinned target framework keep using
    // -restore in the build invocation itself, which already restores the outer build.
    // Returns whether the restore succeeded; callers short-circuit on failure rather than
    // running builds that would only fail with a misleading (e.g. NETSDK1005) error.
    private bool Restore(BuildEnvironment buildEnvironment, AnalyzerResults results) =>
        Restore(buildEnvironment, results, out _);

    private bool Restore(BuildEnvironment buildEnvironment, AnalyzerResults results, out string[] targetFrameworks)
    {
        AnalyzerResults restoreResults = [];
        using (WithSuffixedBinaryLogPaths("restore", true))
        {
            BuildTargets(buildEnvironment.WithRestore(false), null, ["Restore"], restoreResults);
        }

        // Only carry over the success flag: the restore invocation's evaluation would
        // otherwise surface as an extra (empty) target framework result.
        results.Add([], restoreResults.OverallSuccess);

        // On failure the caller stops before any build overwrites BuildEventArguments, so
        // carry the restore's events across to surface the actual restore diagnostics.
        if (!restoreResults.OverallSuccess)
        {
            results.BuildEventArguments = restoreResults.BuildEventArguments;
            targetFrameworks = [];
            return false;
        }

        // The restore also evaluates the project, so read the real (condition-honored) target
        // frameworks from it - discovery for free, no separate evaluation build.
        targetFrameworks = EvaluatedTargetFrameworks(restoreResults);
        return true;
    }

    // When invoking multiple builds in succession (per-TFM builds, or a restore preceding
    // them), point any attached BinaryLogger at a suffixed path (e.g. the TFM or "restore")
    // so each invocation's binlog isn't overwritten by the next. The original path is
    // restored on dispose.
    private IDisposable WithSuffixedBinaryLogPaths(string? suffix, bool active)
    {
        if (!active || suffix is null)
        {
            return NullScope.Instance;
        }

        var previous = _binaryLogSuffix;
        _binaryLogSuffix = suffix;
        return new RestoreBinaryLogSuffix(this, previous);
    }

    // Inserts a suffix before the extension of a binlog path, e.g. "foo.binlog" + "restore" => "foo.restore.binlog".
    internal static string AddSuffixToBinaryLogPath(string path, string suffix)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        string extension = Path.GetExtension(path);
        string withoutExtension = Path.ChangeExtension(path, null);
        return $"{withoutExtension}.{suffix}{extension}";
    }

    // Builds the /bl arguments for the requested binary logs. The suffix is taken from the explicit
    // override when given (parallel per-framework builds pass their own so they don't race on the shared
    // _binaryLogSuffix field), otherwise from the ambient suffix set by WithSuffixedBinaryLogPaths.
    private IReadOnlyCollection<string> BinaryLogArguments(string? suffixOverride = null)
    {
        if (_binaryLogPaths.Count == 0)
        {
            return [];
        }

        string? suffix = suffixOverride ?? _binaryLogSuffix;
        return _binaryLogPaths
            .Select(bl =>
            {
                var path = suffix is { } s ? AddSuffixToBinaryLogPath(bl.Path, s) : bl.Path;
                return $"/bl:LogFile=\"{path}\";ProjectImports={bl.ImportsMode}";
            })
            .ToList();
    }

    private sealed class RestoreBinaryLogSuffix(ProjectAnalyzer analyzer, string? previous) : IDisposable
    {
        public void Dispose()
        {
            analyzer._binaryLogSuffix = previous;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }

    /// <inheritdoc/>
    public IAnalyzerResults Build(string targetFramework) =>
        Build(targetFramework, EnvironmentFactory.GetBuildEnvironment(targetFramework));

    /// <inheritdoc/>
    public IAnalyzerResults Build(string targetFramework, EnvironmentOptions environmentOptions) =>
        Build(
            targetFramework,
            EnvironmentFactory.GetBuildEnvironment(
                targetFramework,
                Guard.NotNull(environmentOptions)));

    /// <inheritdoc/>
    public IAnalyzerResults Build(string targetFramework, BuildEnvironment buildEnvironment)
    {
        Guard.NotNull(buildEnvironment);

        AnalyzerResults results = [];

        // Builds that pin a target framework can't restore themselves (see Restore).
        if (buildEnvironment.Restore && targetFramework is not null)
        {
            if (!Restore(buildEnvironment, results))
            {
                return results;
            }
            buildEnvironment = buildEnvironment.WithRestore(false);
        }

        return BuildTargets(buildEnvironment, targetFramework, buildEnvironment.TargetsToBuild, results);
    }

    /// <inheritdoc/>
    public IAnalyzerResults Build()
    {
        EnvironmentOptions options = new();
        return ProjectFile.IsMultiTargeted
            ? BuildMultiTargeted(targetFramework => EnvironmentFactory.GetBuildEnvironment(targetFramework, options), options.Restore)
            : Build((string?)null, options);
    }

    /// <inheritdoc/>
    public IAnalyzerResults Build(EnvironmentOptions environmentOptions)
    {
        Guard.NotNull(environmentOptions);
        return ProjectFile.IsMultiTargeted
            ? BuildMultiTargeted(targetFramework => EnvironmentFactory.GetBuildEnvironment(targetFramework, environmentOptions), environmentOptions.Restore)
            : Build((string?)null, environmentOptions);
    }

    /// <inheritdoc/>
    public IAnalyzerResults Build(BuildEnvironment buildEnvironment)
    {
        Guard.NotNull(buildEnvironment);
        return ProjectFile.IsMultiTargeted
            ? BuildMultiTargeted(_ => buildEnvironment, buildEnvironment.Restore)
            : Build((string?)null, buildEnvironment);
    }

    // Builds a multi-targeted project as one result per framework. The frameworks come from MSBuild's
    // evaluation, not the raw project XML: scanning the <TargetFrameworks> text is brittle (a project
    // that composes the list, e.g. a Windows-only
    // "<TargetFrameworks Condition="...">$(TargetFrameworks);net472</TargetFrameworks>", yields a phantom
    // framework literally named "$(TargetFrameworks)" and off-platform frameworks MSBuild would never
    // build). When restoring, the single up-front restore also evaluates the project, so the frameworks
    // are read from it for free; otherwise a lightweight no-restore evaluation is used. The per-framework
    // builds then run pinned without restore. This mirrors how Roslyn's MSBuildWorkspace enumerates
    // frameworks. Falls back to the XML scan only if no evaluated value is available.
    private IAnalyzerResults BuildMultiTargeted(Func<string?, BuildEnvironment> environmentFor, bool restore)
    {
        AnalyzerResults results = [];

        string[] targetFrameworks;
        if (restore)
        {
            if (!Restore(environmentFor(null), results, out targetFrameworks))
            {
                return results;
            }
        }
        else
        {
            targetFrameworks = EvaluatedTargetFrameworks(Build((string?)null, environmentFor(null).WithRestore(false)));
        }

        if (targetFrameworks.Length == 0)
        {
            targetFrameworks = ProjectFile.TargetFrameworks;
        }

        BuildTargetsPerFramework(targetFrameworks, targetFramework => environmentFor(targetFramework).WithRestore(false), results);

        return results;
    }

    // Reads the evaluated, semicolon-delimited TargetFrameworks MSBuild computed (conditions honored)
    // from a build's results, so the frameworks we build are exactly the ones MSBuild would.
    private static string[] EvaluatedTargetFrameworks(IAnalyzerResults results)
    {
        foreach (IAnalyzerResult result in results)
        {
            if (result.Properties.TryGetValue("TargetFrameworks", out string value)
                && !string.IsNullOrWhiteSpace(value))
            {
                string[] targetFrameworks = value.Split(
                    [';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (targetFrameworks.Length > 0)
                {
                    return targetFrameworks;
                }
            }
        }

        return [];
    }

    // Builds each target framework and merges the results into a single AnalyzerResults. A single framework
    // builds in-line; multiple frameworks build concurrently, each as an isolated out-of-process build with
    // its own pinned TargetFramework and binlog suffix, and their results are merged afterwards. Each build
    // runs in its own process with its own pipe/EventProcessor, and the only shared ProjectAnalyzer state it
    // reads (global properties, binlog paths) is not mutated during the build, so the builds are independent.
    private void BuildTargetsPerFramework(
        string[] targetFrameworks, Func<string?, BuildEnvironment> environmentFor, AnalyzerResults results)
    {
        if (targetFrameworks.Length <= 1)
        {
            foreach (string targetFramework in targetFrameworks)
            {
                BuildEnvironment buildEnvironment = environmentFor(targetFramework);
                BuildTargets(buildEnvironment, targetFramework, buildEnvironment.TargetsToBuild, results);
            }

            return;
        }

        AnalyzerResults[] perFramework = targetFrameworks
            .AsParallel()
            .Select(targetFramework =>
            {
                BuildEnvironment buildEnvironment = environmentFor(targetFramework);
                AnalyzerResults isolated = [];

                // Pass the binlog suffix explicitly rather than via the shared _binaryLogSuffix field, which
                // these concurrent builds would otherwise race on.
                BuildTargets(buildEnvironment, targetFramework, buildEnvironment.TargetsToBuild, isolated, binaryLogSuffix: targetFramework);
                return isolated;
            })
            .ToArray();

        foreach (AnalyzerResults framework in perFramework)
        {
            results.Add(framework.Results, framework.OverallSuccess);

            // The single BuildEventArguments slot can't hold every framework's events; keep the first
            // non-empty set so failure diagnostics remain available.
            if (results.BuildEventArguments.IsDefaultOrEmpty && !framework.BuildEventArguments.IsDefaultOrEmpty)
            {
                results.BuildEventArguments = framework.BuildEventArguments;
            }
        }
    }

    // This is where the magic happens - returns one result per result target framework
    private IAnalyzerResults BuildTargets(
        BuildEnvironment buildEnvironment, string targetFramework, string[] targetsToBuild, AnalyzerResults results,
        string? binaryLogSuffix = null)
    {
        using var cancellation = new CancellationTokenSource();

        using var pipeLogger = new AnonymousPipeLoggerServer(cancellation.Token);
        using var eventCollector = new BuildEventArgsCollector(pipeLogger);
        using var eventProcessor = new EventProcessor(Manager, this, true);
        eventProcessor.SubscribePipe(pipeLogger);

        // Run MSBuild
        int exitCode;

        var projectFile = IOPath.Parse(ProjectFile.Path);

        var props = BuildCommandProperties.Create(
            projectFile,
            targetFramework,
            buildEnvironment.GlobalProperties,
            Manager.GlobalProperties,
            _globalProperties);

        var cmd = BuildCommand.Create(
            buildEnvironment,
            projectFile,
            props,
            targetsToBuild,
            new LoggerConfiguration
            {
                ClientHandle = pipeLogger.GetClientHandle(),
                LogEverything = _binaryLogPaths.Count > 0,
            },
            BinaryLogArguments(binaryLogSuffix));

        using var processRunner = new ProcessRunner(
            cmd.Command,
            cmd.ToString(),
            buildEnvironment.WorkingDirectory ?? Path.GetDirectoryName(ProjectFile.Path)!,
            GetEffectiveEnvironmentVariables(buildEnvironment)!,
            Manager.LoggerFactory);

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
        exitCode = processRunner.ExitCode;
        results.BuildEventArguments = [.. eventCollector];

        // Collect the results
        results.Add(eventProcessor.Results, exitCode == 0 && eventProcessor.OverallSuccess);

        return results;
    }

    public void SetGlobalProperty(string key, string value)
    {
        _globalProperties[key] = value;
    }

    public void RemoveGlobalProperty(string key)
    {
        // Nulls are removed before passing to MSBuild and can be used to ignore values in lower-precedence collections
        _globalProperties[key] = null;
    }

    public void SetEnvironmentVariable(string key, string value)
    {
        _environmentVariables[key] = value;
    }

    // Note the order of precedence (from least to most)
    private Dictionary<string, string> GetEffectiveGlobalProperties(BuildEnvironment buildEnvironment)
        => GetEffectiveDictionary(
            true,  // Remove nulls to avoid passing null global properties. But null can be used in higher-precident dictionaries to ignore a lower-precident dictionary's value.
            buildEnvironment?.GlobalProperties,
            Manager.GlobalProperties,
            _globalProperties);

    // Note the order of precedence (from least to most)
    private Dictionary<string, string> GetEffectiveEnvironmentVariables(BuildEnvironment buildEnvironment)
        => GetEffectiveDictionary(
            false, // Don't remove nulls as a null value will unset the env var which may be set by a calling process.
            buildEnvironment?.EnvironmentVariables,
            Manager.EnvironmentVariables,
            _environmentVariables);

    private static Dictionary<string, string> GetEffectiveDictionary(
        bool removeNulls,
        params IReadOnlyDictionary<string, string>[] innerDictionaries)
    {
        Dictionary<string, string> effectiveDictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyDictionary<string, string> innerDictionary in innerDictionaries.Where(x => x != null))
        {
            foreach (KeyValuePair<string, string> pair in innerDictionary)
            {
                if (removeNulls && pair.Value == null)
                {
                    effectiveDictionary.Remove(pair.Key);
                }
                else
                {
                    effectiveDictionary[pair.Key] = pair.Value;
                }
            }
        }

        return effectiveDictionary;
    }

    /// <summary>
    /// Adds a binary logger that writes a binlog file for each build.
    /// </summary>
    /// <remarks>
    /// When a multi-targeted project is built without specifying a target framework,
    /// one build runs per target framework and each writes its own binlog with the
    /// target framework appended to the file name (e.g. <c>project.net8.0.binlog</c>)
    /// so the builds don't overwrite one another. When restore runs as a separate
    /// up-front invocation (any build that pins a target framework), it writes a
    /// <c>.restore</c>-suffixed binlog (e.g. <c>project.restore.binlog</c>).
    /// </remarks>
    /// <param name="binaryLogFilePath">
    /// The binlog file path, defaulting to the project path with a <c>.binlog</c> extension.
    /// </param>
    /// <param name="collectProjectImports">How MSBuild project imports are collected in the log.</param>
    public void AddBinaryLogger(
        string? binaryLogFilePath = null,
        BinaryLogImports collectProjectImports = BinaryLogImports.Embed) =>
        _binaryLogPaths.Add((
            binaryLogFilePath ?? Path.ChangeExtension(ProjectFile.Path, "binlog"),
            collectProjectImports.ToString()));
}
