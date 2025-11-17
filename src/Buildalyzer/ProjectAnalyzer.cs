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

    public ProjectInSolution ProjectInSolution { get; }

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
    internal ProjectAnalyzer(AnalyzerManager manager, string projectFilePath, ProjectInSolution projectInSolution)
    {
        Manager = manager;
        Logger = Manager.LoggerFactory?.CreateLogger<ProjectAnalyzer>();
        ProjectFile = new ProjectFile(projectFilePath);
        EnvironmentFactory = new EnvironmentFactory(Manager, ProjectFile);
        ProjectInSolution = projectInSolution;
        SolutionDirectory = (string.IsNullOrEmpty(manager.SolutionFilePath)
            ? Path.GetDirectoryName(projectFilePath) : Path.GetDirectoryName(manager.SolutionFilePath)) + Path.DirectorySeparatorChar;

        // Get (or create) a project GUID
        ProjectGuid = projectInSolution == null
            ? Buildalyzer.ProjectGuid.Create(ProjectFile.Name)
            : Guid.Parse(projectInSolution.ProjectGuid);

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
        foreach (string targetFramework in targetFrameworks)
        {
            BuildEnvironment buildEnvironment = EnvironmentFactory.GetBuildEnvironment(targetFramework, environmentOptions);
            BuildTargets(buildEnvironment, targetFramework, buildEnvironment.TargetsToBuild, results);
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
        foreach (string targetFramework in targetFrameworks)
        {
            BuildTargets(buildEnvironment, targetFramework, buildEnvironment.TargetsToBuild, results);
        }

        return results;
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
    public IAnalyzerResults Build() => Build((string)null);

    /// <inheritdoc/>
    public IAnalyzerResults Build(EnvironmentOptions environmentOptions) => Build((string)null, environmentOptions);

    /// <inheritdoc/>
    public IAnalyzerResults Build(BuildEnvironment buildEnvironment) => Build((string)null, buildEnvironment);

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
