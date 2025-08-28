using Buildalyzer.Construction;
using Buildalyzer.Environment;
using Microsoft.Build.Construction;
using Microsoft.Build.Logging;
using Microsoft.Extensions.Logging;

namespace Buildalyzer;

public interface IProjectAnalyzer
{
    IEnumerable<Microsoft.Build.Framework.ILogger> BuildLoggers { get; }

    EnvironmentFactory EnvironmentFactory { get; }

    /// <summary>
    /// The environment variables for MSBuild to be used for every build from this analyzer.
    /// </summary>
    /// <remarks>
    /// Additional environment variables may be added or changed by individual build environment.
    /// </remarks>
    IReadOnlyDictionary<string, string> EnvironmentVariables { get; }

    /// <summary>
    /// The global properties for MSBuild to be used for every build from this analyzer.
    /// </summary>
    /// <remarks>
    /// Additional global properties may be added or changed by individual build environment.
    /// </remarks>
    IReadOnlyDictionary<string, string> GlobalProperties { get; }

    /// <summary>
    /// Controls whether empty, invalid, and missing targets should be ignored during project load.
    /// </summary>
    bool IgnoreFaultyImports { get; set; }

    ILogger<ProjectAnalyzer> Logger { get; set; }

    AnalyzerManager Manager { get; }

    IProjectFile ProjectFile { get; }

    /// <summary>
    /// Gets a GUID for the project. This checks for a GUID from the
    /// solution (if originally provided). If this isn't available, it
    /// will generate a UUID GUID by hashing the project path relative to the solution path (so it's repeatable).
    /// </summary>
    Guid ProjectGuid { get; }

    ProjectInSolution? ProjectInSolution { get; }

    string SolutionDirectory { get; }

    /// <summary>
    /// Builds the project without specifying a target framework. In a multi-targeted project this will return a <see cref="AnalyzerResult"/> for each target framework.
    /// </summary>
    /// <returns>The result of the build process.</returns>
    IAnalyzerResults Build();

    /// <summary>
    /// Builds the project without specifying a target framework. In a multi-targeted project this will return a <see cref="AnalyzerResult"/> for each target framework.
    /// </summary>
    /// <param name="buildEnvironment">The build environment to use for the build.</param>
    /// <returns>The result of the build process.</returns>
    IAnalyzerResults Build(BuildEnvironment buildEnvironment);

    /// <summary>
    /// Builds the project without specifying a target framework. In a multi-targeted project this will return a <see cref="AnalyzerResult"/> for each target framework.
    /// </summary>
    /// <param name="environmentOptions">The environment options to use for the build.</param>
    /// <returns>The result of the build process.</returns>
    IAnalyzerResults Build(EnvironmentOptions environmentOptions);

    /// <summary>
    /// Builds a specific target framework. In a multi-targeted project this will still return a <see cref="AnalyzerResult"/> for each target framework,
    /// but the target framework(s) not specified may be empty.
    /// </summary>
    /// <param name="targetFramework">The target framework to build.</param>
    /// <returns>The result of the build process.</returns>
    IAnalyzerResults Build(string targetFramework);

    /// <summary>
    /// Builds a specific target framework. In a multi-targeted project this will still return a <see cref="AnalyzerResult"/> for each target framework,
    /// but the target framework(s) not specified may be empty.
    /// </summary>
    /// <param name="targetFramework">The target framework to build.</param>
    /// <param name="buildEnvironment">The build environment to use for the build.</param>
    /// <returns>The result of the build process.</returns>
    IAnalyzerResults Build(string targetFramework, BuildEnvironment buildEnvironment);

    /// <summary>
    /// Builds a specific target framework. In a multi-targeted project this will still return a <see cref="AnalyzerResult"/> for each target framework,
    /// but the target framework(s) not specified may be empty.
    /// </summary>
    /// <param name="targetFramework">The target framework to build.</param>
    /// <param name="environmentOptions">The environment options to use for the build.</param>
    /// <returns>The result of the build process.</returns>
    IAnalyzerResults Build(string targetFramework, EnvironmentOptions environmentOptions);

    /// <summary>
    /// Builds the requested target framework(s). In a multi-targeted project this will still return a <see cref="AnalyzerResult"/> for each target framework,
    /// but the target framework(s) not specified may be empty.
    /// </summary>
    /// <param name="targetFrameworks">The set of target frameworks to build.</param>
    /// <returns>A dictionary of target frameworks to <see cref="AnalyzerResult"/>.</returns>
    IAnalyzerResults Build(string[] targetFrameworks);

    /// <summary>
    /// Builds the requested target framework(s). In a multi-targeted project this will still return a <see cref="AnalyzerResult"/> for each target framework,
    /// but the target framework(s) not specified may be empty.
    /// </summary>
    /// <param name="targetFrameworks">The set of target frameworks to build.</param>
    /// <param name="buildEnvironment">The build environment to use for the build.</param>
    /// <returns>A dictionary of target frameworks to <see cref="AnalyzerResult"/>.</returns>
    IAnalyzerResults Build(string[] targetFrameworks, BuildEnvironment buildEnvironment);

    /// <summary>
    /// Builds the requested target framework(s). In a multi-targeted project this will still return a <see cref="AnalyzerResult"/> for each target framework,
    /// but the target framework(s) not specified may be empty.
    /// </summary>
    /// <param name="targetFrameworks">The set of target frameworks to build.</param>
    /// <param name="environmentOptions">The environment options to use for the build.</param>
    /// <returns>A dictionary of target frameworks to <see cref="AnalyzerResult"/>.</returns>
    IAnalyzerResults Build(string[] targetFrameworks, EnvironmentOptions environmentOptions);

    void AddBinaryLogger(
        string binaryLogFilePath = null,
        BinaryLogger.ProjectImportsCollectionMode collectProjectImports = BinaryLogger.ProjectImportsCollectionMode.Embed);

    /// <summary>
    /// Adds an MSBuild logger to the build. Note that this may have a large penalty on build performance.
    /// </summary>
    /// <remarks>
    /// Normally, the minimum required amount of log events are forwarded from the MSBuild process to Buildalyzer.
    /// By attaching arbitrary loggers, MSBuild must forward every log event so the logger has a chance to handle it.
    /// </remarks>
    /// <param name="logger">The logger to add.</param>
    void AddBuildLogger(Microsoft.Build.Framework.ILogger logger);

    /// <summary>
    /// Removes an MSBuild logger from the build.
    /// </summary>
    /// <param name="logger">The logger to remove.</param>
    void RemoveBuildLogger(Microsoft.Build.Framework.ILogger logger);

    void RemoveGlobalProperty(string key);

    void SetEnvironmentVariable(string key, string value);

    void SetGlobalProperty(string key, string value);
}
