using Buildalyzer.IO;
using Buildalyzer.Logger;

namespace Buildalyzer.Logging;

/// <summary>Configuration for the pipe logger in the command line.</summary>
public sealed record LoggerConfiguration
{
    /// <summary>The type of pipe logger (default is <see cref="BuildalyzerLogger"/>).</summary>
    public string LoggerType { get; init; } = typeof(BuildalyzerLogger).Name;

    /// <summary>The path the logger assembly.</summary>
    public IOPath LoggerPath { get; init; } = DefaultLoggerPath();

    /// <summary>The client handle.</summary>
    public string ClientHandle { get; init; } = string.Empty;

    /// <summary>Should if everything be logged (default is true).</summary>
    public bool LogEverything { get; init; } = true;

    /// <summary>Gets the default logger path</summary>
    [Pure]
    public static IOPath DefaultLoggerPath()
        => (typeof(BuildalyzerLogger).Assembly.Location
        ?? System.Environment.GetEnvironmentVariable(Environment.EnvironmentVariables.LoggerPathDll))
        is { Length: > 0 } path
            ? IOPath.Parse(path)
            : throw new ArgumentException($"The dll of {nameof(BuildalyzerLogger)} is required");
}
