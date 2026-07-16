using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Buildalyzer.Construction;
using Buildalyzer.Environment;
using Buildalyzer.IO;
using Buildalyzer.Logging;
using Microsoft.Build.Construction;
using Microsoft.Build.Logging;
using Microsoft.Extensions.Logging;
using XenoAtom.MsBuildPipeLogger;
using ILogger = Microsoft.Build.Framework.ILogger;

namespace Buildalyzer;

public class ProjectAnalyzer : IProjectAnalyzer
{
    private readonly List<ILogger> _buildLoggers = [];

    // Project-specific global properties and environment variables
    private readonly ConcurrentDictionary<string, string> _globalProperties = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, string> _environmentVariables = new(StringComparer.OrdinalIgnoreCase);

    public AnalyzerManager Manager { get; }

    public IProjectFile ProjectFile { get; }

    public EnvironmentFactory EnvironmentFactory { get; }

    public string SolutionDirectory { get; }

    public ProjectInfo? Project { get; }

    [Obsolete("Use Project instead.")]
    public ProjectInSolution? ProjectInSolution => Project?.Reference as ProjectInSolution;

    /// <inheritdoc/>
    public Guid ProjectGuid { get; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> GlobalProperties => GetEffectiveGlobalProperties(null);

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> EnvironmentVariables => GetEffectiveEnvironmentVariables(null);

    public IEnumerable<ILogger> BuildLoggers => _buildLoggers;

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
        SolutionDirectory = (string.IsNullOrEmpty(manager.SolutionFilePath)
            ? path.File()!.Directory.FullName : Path.GetDirectoryName(manager.SolutionFilePath)) + Path.DirectorySeparatorChar;

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
        bool perTfmBinlog = targetFrameworks.Length > 1;

        // Builds that pin a target framework can't restore themselves (see Restore), so run
        // a single up-front restore with the project's own (outer) build environment. It
        // produces an assets file covering every framework, so one restore is enough.
        bool restore = environmentOptions.Restore && targetFrameworks.Any(t => t is not null);
        if (restore && !Restore(EnvironmentFactory.GetBuildEnvironment(null, environmentOptions), results))
        {
            return results;
        }

        // Create a new build environment for each target
        foreach (string targetFramework in targetFrameworks)
        {
            BuildEnvironment buildEnvironment = EnvironmentFactory.GetBuildEnvironment(targetFramework, environmentOptions);
            if (restore)
            {
                buildEnvironment = buildEnvironment.WithRestore(false);
            }

            using (WithSuffixedBinaryLogPaths(targetFramework, perTfmBinlog))
            {
                BuildTargets(buildEnvironment, targetFramework, buildEnvironment.TargetsToBuild, results);
            }
        }

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
        bool perTfmBinlog = targetFrameworks.Length > 1;

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

        foreach (string targetFramework in targetFrameworks)
        {
            using (WithSuffixedBinaryLogPaths(targetFramework, perTfmBinlog))
            {
                BuildTargets(buildEnvironment, targetFramework, buildEnvironment.TargetsToBuild, results);
            }
        }

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
    private bool Restore(BuildEnvironment buildEnvironment, AnalyzerResults results)
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
        }

        return restoreResults.OverallSuccess;
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

        List<(BinaryLogger Logger, string OriginalParameters)> snapshots = [];
        foreach (BinaryLogger logger in _buildLoggers.OfType<BinaryLogger>())
        {
            string original = logger.Parameters;
            snapshots.Add((logger, original));
            logger.Parameters = AddSuffixToBinaryLogPath(original, suffix);
        }

        return new RestoreBinaryLogPaths(snapshots);
    }

    // BinaryLogger.Parameters is a semicolon-separated list where the log file is either a
    // bare path ending in ".binlog" or a "LogFile=" segment (possibly quoted), alongside
    // other segments like "ProjectImports=Embed". Only the log file segment is rewritten.
    internal static string AddSuffixToBinaryLogPath(string parameters, string suffix)
    {
        if (string.IsNullOrEmpty(parameters))
        {
            return parameters;
        }

        string[] segments = parameters.Split(';');
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            string prefix = string.Empty;
            if (segment.StartsWith("LogFile=", StringComparison.OrdinalIgnoreCase))
            {
                prefix = segment[.."LogFile=".Length];
                segment = segment[prefix.Length..];
            }

            string path = segment.Trim('"');
            if (!path.EndsWith(".binlog", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string quote = path.Length == segment.Length ? string.Empty : "\"";
            string extension = Path.GetExtension(path);
            string withoutExtension = Path.ChangeExtension(path, null);
            segments[i] = $"{prefix}{quote}{withoutExtension}.{suffix}{extension}{quote}";
            return string.Join(";", segments);
        }

        return parameters;
    }

    private sealed class RestoreBinaryLogPaths(List<(BinaryLogger Logger, string OriginalParameters)> snapshots) : IDisposable
    {
        public void Dispose()
        {
            foreach (var (logger, original) in snapshots)
            {
                logger.Parameters = original;
            }
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
    public IAnalyzerResults Build() =>
        ProjectFile.IsMultiTargeted
            ? Build(ProjectFile.TargetFrameworks)
            : Build((string?)null);

    /// <inheritdoc/>
    public IAnalyzerResults Build(EnvironmentOptions environmentOptions) =>
        ProjectFile.IsMultiTargeted
            ? Build(ProjectFile.TargetFrameworks, environmentOptions)
            : Build((string?)null, environmentOptions);

    /// <inheritdoc/>
    public IAnalyzerResults Build(BuildEnvironment buildEnvironment) =>
        ProjectFile.IsMultiTargeted
            ? Build(ProjectFile.TargetFrameworks, buildEnvironment)
            : Build((string?)null, buildEnvironment);

    // This is where the magic happens - returns one result per result target framework
    private IAnalyzerResults BuildTargets(
        BuildEnvironment buildEnvironment, string targetFramework, string[] targetsToBuild, AnalyzerResults results)
    {
        using var cancellation = new CancellationTokenSource();

        using var pipeLogger = new AnonymousPipeLoggerServer(cancellation.Token);
        using var eventCollector = new BuildEventArgsCollector(pipeLogger);
        using var eventProcessor = new EventProcessor(Manager, this, BuildLoggers, pipeLogger, true);

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
                LogEverything = _buildLoggers.Count > 0,
            });

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
    /// Adds a <see cref="BinaryLogger"/> that writes a binlog file for each build.
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
        BinaryLogger.ProjectImportsCollectionMode collectProjectImports = BinaryLogger.ProjectImportsCollectionMode.Embed) =>
        AddBuildLogger(new BinaryLogger
        {
            Parameters = binaryLogFilePath ?? Path.ChangeExtension(ProjectFile.Path, "binlog"),
            CollectProjectImports = collectProjectImports,
            Verbosity = Microsoft.Build.Framework.LoggerVerbosity.Diagnostic
        });

    /// <inheritdoc/>
    public void AddBuildLogger(ILogger logger) => _buildLoggers.Add(Guard.NotNull(logger));

    /// <inheritdoc/>
    public void RemoveBuildLogger(ILogger logger) => _buildLoggers.Remove(Guard.NotNull(logger));
}
