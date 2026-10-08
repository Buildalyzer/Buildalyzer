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
    /// That location is empty in a single-file or Native AOT app, where the logger dll has to be deployed
    /// next to the executable instead; the app's base directory is used there.
    /// </remarks>
    [Pure]
    [UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000:Avoid accessing Assembly file path when publishing as a single file",
        Justification = "The empty location of a single-file app is handled by falling back to the base directory.")]
    public static IOPath DefaultLoggerPath()
    {
        // An explicit override wins (e.g. when the logger dll is deployed elsewhere).
        if (System.Environment.GetEnvironmentVariable(Environment.EnvironmentVariables.LoggerPathDll)
            is { Length: > 0 } overridePath)
        {
            return IOPath.Parse(overridePath);
        }

        var location = typeof(LoggerConfiguration).Assembly.Location;
        var loggerDirectory = location.Length > 0
            ? System.IO.Path.GetDirectoryName(location)
            : AppContext.BaseDirectory;

        if (loggerDirectory is { Length: > 0 } directory)
        {
            return IOPath.Parse(System.IO.Path.Combine(directory, "Buildalyzer.Logger.dll"));
        }

        throw new ArgumentException("The dll of BuildalyzerLogger is required");
    }
}
