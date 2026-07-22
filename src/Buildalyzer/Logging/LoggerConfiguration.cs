using Buildalyzer.IO;
using Buildalyzer.Logger;

namespace Buildalyzer.Logging;

/// <summary>Configuration for the pipe logger in the command line.</summary>
internal sealed record LoggerConfiguration
{
    /// <summary>The type of pipe logger (default is <see cref="BuildalyzerLogger"/>).</summary>
    public string LoggerType { get; init; } = "BuildalyzerLogger";

    /// <summary>The path to the logger assembly.</summary>
    public IOPath LoggerPath { get; init; } = DefaultLoggerPath();

    /// <summary>The client handle.</summary>
    public string ClientHandle { get; init; } = string.Empty;

    /// <summary>Should everything be logged (default is true).</summary>
    public bool LogEverything { get; init; } = true;

    /// <summary>Gets the default logger path.</summary>
    /// <remarks>
    /// Deliberately avoids referencing the <see cref="BuildalyzerLogger"/> type: touching it forces the JIT to
    /// load its base chain (PipeLogger : Microsoft.Build.Utilities.Logger), which would drag
    /// Microsoft.Build.Utilities.Core into Buildalyzer's own process at runtime. The logger assembly is copied
    /// next to Buildalyzer.dll by the ProjectReference, so the path is derived from this assembly's location.
    /// </remarks>
    [Pure]
    public static IOPath DefaultLoggerPath()
    {
        // An explicit override wins (e.g. when the logger dll is deployed elsewhere).
        if (System.Environment.GetEnvironmentVariable(Environment.EnvironmentVariables.LoggerPathDll)
            is { Length: > 0 } overridePath)
        {
            return IOPath.Parse(overridePath);
        }

        if (System.IO.Path.GetDirectoryName(typeof(LoggerConfiguration).Assembly.Location)
            is { Length: > 0 } directory)
        {
            return IOPath.Parse(System.IO.Path.Combine(directory, "Buildalyzer.Logger.dll"));
        }

        throw new ArgumentException("The dll of BuildalyzerLogger is required");
    }
}
