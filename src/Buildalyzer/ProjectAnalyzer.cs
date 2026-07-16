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
using MsBuildPipeLogger;
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

        // Create a new build environment for each target
        AnalyzerResults results = [];
        bool perTfmBinlog = targetFrameworks.Length > 1;
        bool restored = false;
        foreach (string targetFramework in targetFrameworks)
        {
            BuildEnvironment buildEnvironment = EnvironmentFactory.GetBuildEnvironment(targetFramework, environmentOptions);

            // Restore evaluates every framework in <TargetFrameworks> regardless of the
            // TargetFramework global property, so one restore covers all iterations.
            if (restored && buildEnvironment.Restore)
            {
                buildEnvironment = buildEnvironment.WithRestore(false);
            }

            using (WithPerTfmBinaryLogPaths(targetFramework, perTfmBinlog))
            {
                BuildTargets(buildEnvironment, targetFramework, buildEnvironment.TargetsToBuild, results);
            }
            restored = true;
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
        bool restored = false;
        foreach (string targetFramework in targetFrameworks)
        {
            // Restore evaluates every framework in <TargetFrameworks> regardless of the
            // TargetFramework global property, so one restore covers all iterations.
            BuildEnvironment tfmBuildEnvironment = restored && buildEnvironment.Restore
                ? buildEnvironment.WithRestore(false)
                : buildEnvironment;

            using (WithPerTfmBinaryLogPaths(targetFramework, perTfmBinlog))
            {
                BuildTargets(tfmBuildEnvironment, targetFramework, tfmBuildEnvironment.TargetsToBuild, results);
            }
            restored = true;
        }

        return results;
    }

    // When invoking multiple per-TFM builds in succession, point any attached
    // BinaryLogger at a TFM-suffixed path so each iteration's binlog isn't
    // overwritten by the next. The original path is restored on dispose.
    private IDisposable WithPerTfmBinaryLogPaths(string? targetFramework, bool active)
    {
        if (!active || targetFramework is null)
        {
            return NullScope.Instance;
        }

        List<(BinaryLogger Logger, string OriginalParameters)> snapshots = [];
        foreach (BinaryLogger logger in _buildLoggers.OfType<BinaryLogger>())
        {
            string original = logger.Parameters;
            snapshots.Add((logger, original));
            logger.Parameters = AddTargetFrameworkToBinaryLogPath(original, targetFramework);
        }

        return new RestoreBinaryLogPaths(snapshots);
    }

    // BinaryLogger.Parameters is a semicolon-separated list where the log file is either a
    // bare path ending in ".binlog" or a "LogFile=" segment (possibly quoted), alongside
    // other segments like "ProjectImports=Embed". Only the log file segment is rewritten.
    internal static string AddTargetFrameworkToBinaryLogPath(string parameters, string targetFramework)
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
            segments[i] = $"{prefix}{quote}{withoutExtension}.{targetFramework}{extension}{quote}";
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
    public IAnalyzerResults Build(string targetFramework, BuildEnvironment buildEnvironment) =>
        BuildTargets(
            Guard.NotNull(buildEnvironment),
            targetFramework,
            buildEnvironment.TargetsToBuild,
            []);

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
    /// so the builds don't overwrite one another.
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
