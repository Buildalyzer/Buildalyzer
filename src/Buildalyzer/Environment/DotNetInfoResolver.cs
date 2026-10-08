using System.Collections.Concurrent;
using System.IO;
using Buildalyzer.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Buildalyzer.Environment;

/// <summary>Runs <c>dotnet --info</c> for a project, once per SDK selection.</summary>
/// <remarks>
/// One resolver is shared by every project of an <see cref="AnalyzerManager"/>. Each build environment
/// asks for the info (a multi-targeted project asks once for its restore and again for every framework),
/// so without a shared cache a solution runs it once per build - and those runs contend with the builds
/// themselves.
/// </remarks>
internal sealed class DotNetInfoResolver
{
    private static readonly TimeSpan FallbackWaitTime = TimeSpan.FromSeconds(10);

    // Keyed by the dotnet executable and the global.json that governs the project, which between them
    // decide which SDK `dotnet --info` reports: the host picks the SDK from the nearest global.json above
    // the working directory, or the latest installed SDK when there is none. Projects under the same
    // global.json (or under none) therefore share one invocation. Lazy so concurrent builds wait on the
    // one invocation rather than each starting their own.
    private readonly ConcurrentDictionary<(string DotNetExePath, string GlobalJson), Lazy<DotNetInfo>> Cache = new();

    [Pure]
    public DotNetInfo Resolve(IOPath projectPath, IOPath dotNetExePath, ILoggerFactory? factory)
    {
        var key = (dotNetExePath.ToString(), FindGlobalJson(projectPath) ?? string.Empty);
        var entry = Cache.GetOrAdd(key, _ => new(() => Execute(projectPath, dotNetExePath, factory ?? NullLoggerFactory.Instance)));
        DotNetInfo info;
        try
        {
            info = entry.Value;
        }
        catch
        {
            // Lazy would otherwise hand the same exception to every later caller.
            Cache.TryRemove(new(key, entry));
            throw;
        }

        // Don't hold on to a failed run (it timed out, say): the next project gets to try again.
        if (info.BasePath is null && info.Runtimes.IsEmpty)
        {
            Cache.TryRemove(new(key, entry));
        }

        return info;
    }

    /// <summary>Finds the global.json the .NET host would use for a project, if there is one.</summary>
    private static string? FindGlobalJson(IOPath projectPath)
    {
        for (var directory = projectPath.File()?.Directory; directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "global.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [Pure]
    private static DotNetInfo Execute(IOPath projectPath, IOPath dotNetExePath, ILoggerFactory factory)
    {
        // Ensure that we set the DOTNET_CLI_UI_LANGUAGE environment variable to "en-US" before
        // running 'dotnet --info'. Otherwise, we may get localized results
        // Also unset some MSBuild variables, see https://github.com/OmniSharp/omnisharp-roslyn/blob/df160f86ce906bc566fe3e04e38a4077bd6023b4/src/OmniSharp.Abstractions/Services/DotNetCliService.cs#L36
        var environmentVariables = new Dictionary<string, string?>
        {
            [EnvironmentVariables.DOTNET_CLI_UI_LANGUAGE] /*.*/ = "en-US",
            [EnvironmentVariables.MSBUILD_EXE_PATH] /*.......*/ = null,
            [EnvironmentVariables.COREHOST_TRACE] /*.........*/ = "0",
            [MsBuildProperties.MSBuildExtensionsPath] /*.....*/ = null,
        };

        // global.json may change the version, so need to set working directory
        using var processRunner = new ProcessRunner(
            dotNetExePath.ToString(),
            "--info",
            projectPath.File().Directory!.FullName,
            environmentVariables,
            factory);

        processRunner.Start();
        var logger = factory.CreateLogger<DotNetInfoResolver>();
        if (!processRunner.WaitForExit(GetWaitTime(logger)))
        {
            // Partial output is no answer; an empty one isn't cached, so the next project tries again.
            logger.LogWarning("`{DotNetExePath} --info` did not exit in time", dotNetExePath);
            processRunner.Kill();
            return DotNetInfo.Parse((string?)null);
        }

        return DotNetInfo.Parse(processRunner.Data.Output);
    }

    [Pure]
    private static int GetWaitTime(ILogger logger)
    {
        if (int.TryParse(System.Environment.GetEnvironmentVariable(EnvironmentVariables.DOTNET_INFO_WAIT_TIME), out int waitTime))
        {
            logger.LogInformation("dotnet --info wait time is {WaitTime}ms", waitTime);
            return waitTime;
        }
        else
        {
            return (int)FallbackWaitTime.TotalMilliseconds;
        }
    }
}
