using System.Collections.Concurrent;
using Buildalyzer.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Buildalyzer.Environment;

internal sealed class DotNetInfoResolver(ILoggerFactory? factory)
{
    private static readonly TimeSpan FallbackWaitTime = TimeSpan.FromSeconds(10);
    private readonly ILoggerFactory Factory = factory ?? NullLoggerFactory.Instance;
    private readonly ILogger Logger = (factory ?? NullLoggerFactory.Instance).CreateLogger<DotNetInfoResolver>();

    [Pure]
    public DotNetInfo Resolve(IOPath projectPath, IOPath dotNetExePath)
        => Cache.TryGetValue(projectPath, out var info)
            ? info
            : Execute(projectPath, dotNetExePath);

    [Pure]
    private DotNetInfo Execute(IOPath projectPath, IOPath dotNetExePath)
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
            Factory);

        processRunner.Start();
        processRunner.WaitForExit(GetWaitTime());

        var info = DotNetInfo.Parse(processRunner.Data.Output);
        Cache[projectPath] = info;
        return info;
    }

    [Pure]
    private int GetWaitTime()
    {
        if (int.TryParse(System.Environment.GetEnvironmentVariable(EnvironmentVariables.DOTNET_INFO_WAIT_TIME), out int waitTime))
        {
            Logger?.LogInformation("dotnet --info wait time is {WaitTime}ms", waitTime);
            return waitTime;
        }
        else
        {
            return (int)FallbackWaitTime.TotalMilliseconds;
        }
    }

    private readonly ConcurrentDictionary<IOPath, DotNetInfo> Cache = new();
}
