using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using Buildalyzer.Construction;
using Buildalyzer.Environment;
using Buildalyzer.IO;
using Buildalyzer.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    // The project the SDK generated for a file-based app, or null for a project that exists on disk.
    // It has to be on disk while MSBuild builds it, and only then; see VirtualProject.
    private readonly VirtualProject? _virtualProject;

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

        // A file-based app is a single .cs file with no project of its own, so the project to analyze is
        // the one the SDK generates for it. Generating it means evaluating it, which happens before any
        // BuildEnvironment exists (the project has to be parsed before it can be decided how to build
        // it), so it runs against the default dotnet - see VirtualProject on the mismatch that allows.
        _virtualProject = FileBasedApp.IsEntryPoint(path)
            ? new VirtualProjectResolver(Manager.LoggerFactory).Resolve(path, EnvironmentOptions.DefaultDotnetExePath)
            : null;

        ProjectFile = _virtualProject is { } virtualProject
            ? new ProjectFile(virtualProject)
            : new ProjectFile(path.ToString());
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

        // If the set of target frameworks is empty, build the default the same way Build() does,
        // so a multi-targeted project still builds per framework.
        if (targetFrameworks == null || targetFrameworks.Length == 0)
        {
            return BuildSingleOrMultiTargeted(targetFramework => EnvironmentFactory.GetBuildEnvironment(targetFramework, environmentOptions), environmentOptions.Restore);
        }

        AnalyzerResults results = [];

        // Builds that pin a target framework can't restore themselves (see Restore), so run
        // a single up-front restore with the project's own (outer) build environment. It
        // produces an assets file covering every framework, so one restore is enough.
        bool restore = environmentOptions.Restore && targetFrameworks.Any(t => t is not null);
        if (restore)
        {
            Restore(EnvironmentFactory.GetBuildEnvironment(null, environmentOptions), results);
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

        // If the set of target frameworks is empty, build the default the same way Build() does,
        // so a multi-targeted project still builds per framework.
        if (targetFrameworks == null || targetFrameworks.Length == 0)
        {
            return BuildSingleOrMultiTargeted(_ => buildEnvironment, buildEnvironment.Restore);
        }

        AnalyzerResults results = [];

        // Builds that pin a target framework can't restore themselves (see Restore), so run
        // a single up-front restore covering every framework.
        if (buildEnvironment.Restore && targetFrameworks.Any(t => t is not null))
        {
            Restore(buildEnvironment, results);
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
    // A failed restore does not stop the build. NuGet writes the assets file even when it reports an
    // error (a version conflict such as NU1109, say), and the SDK's ResolvePackageAssets then replays
    // that error during the build - where ContinueOnError=ErrorAndContinue lets the build carry on to
    // CoreCompile, so the compiler's real inputs are still captured. That is also what MSBuildWorkspace
    // does, as it never restores at all. Stopping at the restore instead would leave the result with
    // nothing but the restore's evaluation: no references, no analyzers, no generated sources. The
    // failure is kept in OverallSuccess, and its errors are logged as they arrive over the pipe.
    private void Restore(BuildEnvironment buildEnvironment, AnalyzerResults results) =>
        Restore(buildEnvironment, results, out _);

    private void Restore(BuildEnvironment buildEnvironment, AnalyzerResults results, out string[] targetFrameworks)
    {
        AnalyzerResults restoreResults = [];
        using (WithSuffixedBinaryLogPaths("restore", true))
        {
            BuildTargets(buildEnvironment.WithRestore(false), null, ["Restore"], restoreResults);
        }

        // Only carry over the success flag: the restore invocation's evaluation would
        // otherwise surface as an extra (empty) target framework result.
        results.Add([], restoreResults.OverallSuccess);

        if (!restoreResults.OverallSuccess)
        {
            Logger?.LogWarning(
                "Restore failed for {ProjectFile}; building against the existing assets file so the compiler's inputs are still captured.",
                ProjectFile.Path);
        }

        // The restore also evaluates the project, so read the real (condition-honored) target
        // frameworks from it - discovery for free, no separate evaluation build.
        targetFrameworks = EvaluatedTargetFrameworks(restoreResults);
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
    internal IReadOnlyCollection<string> BinaryLogArguments(string? suffixOverride = null)
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
            Restore(buildEnvironment, results);
            buildEnvironment = buildEnvironment.WithRestore(false);
        }

        BuildTargets(buildEnvironment, targetFramework, buildEnvironment.TargetsToBuild, results);

        // An unpinned build restores in-line (-restore), and MSBuild runs no build target at all when
        // that restore fails - the result is left with the restore's evaluation and no compiler
        // invocation. Build again without the restore switch so the build itself runs (see Restore for
        // why that works against the assets file a failed restore still writes). The retry merges into
        // the same results, so the restore's failure stays in OverallSuccess. A build that failed for
        // any other reason before reaching the compiler pays for one repeat of that failure.
        if (buildEnvironment.Restore && !results.OverallSuccess && !results.Any(r => r.Command.Length > 0))
        {
            Logger?.LogWarning(
                "The build of {ProjectFile} did not reach the compiler; retrying without restore against the existing assets file.",
                ProjectFile.Path);
            BuildTargets(buildEnvironment.WithRestore(false), targetFramework, buildEnvironment.TargetsToBuild, results);
        }

        return results;
    }

    /// <inheritdoc/>
    public IAnalyzerResults Build()
    {
        EnvironmentOptions options = new();
        return BuildSingleOrMultiTargeted(targetFramework => EnvironmentFactory.GetBuildEnvironment(targetFramework, options), options.Restore);
    }

    /// <inheritdoc/>
    public IAnalyzerResults Build(EnvironmentOptions environmentOptions)
    {
        Guard.NotNull(environmentOptions);
        return BuildSingleOrMultiTargeted(targetFramework => EnvironmentFactory.GetBuildEnvironment(targetFramework, environmentOptions), environmentOptions.Restore);
    }

    /// <inheritdoc/>
    public IAnalyzerResults Build(BuildEnvironment buildEnvironment)
    {
        Guard.NotNull(buildEnvironment);
        return BuildSingleOrMultiTargeted(_ => buildEnvironment, buildEnvironment.Restore);
    }

    // IsMultiTargeted is an XML scan, so a <TargetFrameworks> declared in an imported file
    // (e.g. Directory.Build.props) is invisible to it and the project starts down the
    // single-targeted path: one unpinned invocation. For a project that is in fact
    // multi-targeted that invocation runs against the cross-targeting outer build, where the
    // default Compile target doesn't exist (MSB4057) and the only per-framework results come
    // from the restore's evaluations - empty shells with no compiler invocation behind them.
    // The invocation's evaluation still yields the real TargetFrameworks value, though, so a
    // failed build that evaluated one is discarded and rebuilt per framework instead.
    // BuildMultiTargeted re-runs the restore rather than reusing the discarded attempt's: the
    // attempt's failure may *be* a restore failure, which the retry then surfaces properly.
    // A successful unpinned build is kept even if multi-targeted: custom TargetsToBuild that
    // exist in the outer build (e.g. Build) dispatch to the inner builds themselves.
    private IAnalyzerResults BuildSingleOrMultiTargeted(Func<string?, BuildEnvironment> environmentFor, bool restore)
    {
        if (ProjectFile.IsMultiTargeted)
        {
            return BuildMultiTargeted(environmentFor, restore);
        }

        IAnalyzerResults results = Build((string?)null, environmentFor(null));
        if (results.OverallSuccess || EvaluatedTargetFrameworks(results).Length == 0)
        {
            return results;
        }

        return BuildMultiTargeted(environmentFor, restore);
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
            Restore(environmentFor(null), results, out targetFrameworks);
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
    // from a build's results, so the frameworks we build are exactly the ones MSBuild would. Only the
    // cross-targeting outer evaluation - the one with no TargetFramework of its own - is consulted: a
    // project that overrides an imported <TargetFrameworks> (from Directory.Build.props, say) with its own
    // <TargetFramework> still evaluates with both set, but it is an ordinary single-targeted build, and
    // rebuilding it per listed framework would pin it to frameworks it never targets.
    internal static string[] EvaluatedTargetFrameworks(IAnalyzerResults results)
    {
        foreach (IAnalyzerResult result in results)
        {
            if (result.Properties.TryGetValue("TargetFramework", out string targetFramework)
                && !string.IsNullOrWhiteSpace(targetFramework))
            {
                continue;
            }

            if (result.Properties.TryGetValue("TargetFrameworks", out string value)
                && !string.IsNullOrWhiteSpace(value))
            {
                // Distinct, as MSBuild's own _ComputeTargetFrameworkItems is: a framework listed twice (a props
                // file composing "$(TargetFrameworks);net8.0" over a project that already lists net8.0, say)
                // would otherwise build twice, concurrently, into the same obj directory.
                string[] targetFrameworks =
                [
                    .. value
                        .Split([';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                ];
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

        AnalyzerResults[] perFramework;
        try
        {
            perFramework = targetFrameworks
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
        }
        catch (AggregateException exception) when (exception.InnerExceptions.Count == 1)
        {
            // A single failing build surfaces its own exception, as it does when the frameworks build
            // in-line, rather than PLINQ's wrapper around it.
            ExceptionDispatchInfo.Capture(exception.InnerExceptions[0]).Throw();
            throw;
        }

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

        // A file-based app's project only exists on disk while it is being built; concurrent per-framework
        // builds share the one file and it is removed once the last of them is done.
        using var materialized = _virtualProject is { } virtualProject
            ? virtualProject.Materialize(buildEnvironment.DotnetExePath, Logger ?? NullLogger<ProjectAnalyzer>.Instance)
            : NullScope.Instance;

        using var pipeLogger = new AnonymousPipeLoggerServer(cancellation.Token);
        using var eventCollector = new BuildEventArgsCollector(pipeLogger);
        using var eventProcessor = new EventProcessor(Manager, this, true);
        eventProcessor.SubscribePipe(pipeLogger);

        // Run MSBuild
        int exitCode;

        var projectFile = IOPath.Parse(ProjectFile.Path);

        var props = BuildCommandProperties.Create(
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

        processRunner.Start();

        // The drain ends the read once MSBuild has gone, including the case where it exits without ever
        // writing to the pipe (a startup failure): the server keeps its own copy of the client handle open
        // there, so the read never sees the end of the file.
        PipeLoggerDrain.ReadUntilExit(pipeLogger, processRunner, eventCollector);
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
    /// <exception cref="ArgumentException">The path contains a <c>;</c> or a <c>"</c>.</exception>
    public void AddBinaryLogger(
        string? binaryLogFilePath = null,
        BinaryLogImports collectProjectImports = BinaryLogImports.Embed)
    {
        string path = binaryLogFilePath ?? Path.ChangeExtension(ProjectFile.Path, "binlog");

        // MSBuild splits the /bl parameters on ';' without regard to quotes, and takes the log file name
        // verbatim - it does not unescape %3B the way it does a project path - so there is no spelling of
        // either character that reaches the binary logger intact. Fail here rather than with MSB1029 later.
        if (path.IndexOfAny([';', '"']) >= 0)
        {
            throw new ArgumentException(
                $"MSBuild cannot write a binary log to a path containing ';' or '\"': {path}",
                nameof(binaryLogFilePath));
        }

        // Rooted here, against this process' current directory. MSBuild writes the log from its own working
        // directory - the project's, or EnvironmentOptions.WorkingDirectory - so a relative path handed over
        // as-is would land there rather than where the caller is, as it did when the logger ran in-process.
        _binaryLogPaths.Add((Path.GetFullPath(path), collectProjectImports.ToString()));
    }
}
