using Microsoft.Extensions.Logging;

namespace Buildalyzer;

public interface IAnalyzerManager
{
    ILoggerFactory? LoggerFactory { get; set; }

    IReadOnlyDictionary<string, IProjectAnalyzer> Projects { get; }

    /// <inheritdoc cref="SolutionInfo" />
    SolutionInfo? Solution { get; }

    /// <summary>
    /// Analyzes an MSBuild binary log file.
    /// </summary>
    /// <param name="binLogPath">The path to the binary log file.</param>
    /// <returns>A dictionary of target frameworks to <see cref="AnalyzerResult"/>.</returns>
    IAnalyzerResults Analyze(string binLogPath);

    IProjectAnalyzer? GetProject(string projectFilePath);

    void RemoveGlobalProperty(string key);

    void SetEnvironmentVariable(string key, string value);

    void SetGlobalProperty(string key, string value);
}
