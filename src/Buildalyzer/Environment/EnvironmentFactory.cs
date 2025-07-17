using System.IO;
using System.Runtime.InteropServices;
using Buildalyzer.Construction;
using Microsoft.Build.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Frameworks;

namespace Buildalyzer.Environment;

public class EnvironmentFactory
{
    private readonly IAnalyzerManager _manager;
    private readonly IProjectFile _projectFile;
    private readonly ILogger Logger;

    internal EnvironmentFactory(IAnalyzerManager manager, IProjectFile projectFile)
    {
        _manager = manager;
        _projectFile = projectFile;
        Logger = _manager.LoggerFactory?.CreateLogger<EnvironmentFactory>() ?? NullLogger<EnvironmentFactory>.Instance;
    }

    public BuildEnvironment? GetBuildEnvironment() =>
        GetBuildEnvironment(null, null);

    public BuildEnvironment? GetBuildEnvironment(string? targetFramework) =>
        GetBuildEnvironment(targetFramework, null);

    public BuildEnvironment? GetBuildEnvironment(EnvironmentOptions? options) =>
        GetBuildEnvironment(null, options);

    public BuildEnvironment? GetBuildEnvironment(string? targetFramework, EnvironmentOptions? options)
    {
        options ??= new EnvironmentOptions();
        BuildEnvironment? buildEnvironment;

        // Use the .NET Framework if that's the preference
        // ...or if this project file uses a target known to require .NET Framework
        // ...or if this project ONLY targets .NET Framework ("net" followed by a digit)
        if (options.Preference == EnvironmentPreference.Framework
            || _projectFile.RequiresNetFramework
            || (_projectFile.UsesSdk && OnlyTargetsFramework(targetFramework)))
        {
            buildEnvironment = CreateFrameworkEnvironment(options) ?? CreateCoreEnvironment(options);
        }
        else
        {
            // Otherwise, use a Core environment if it can be found
            buildEnvironment = CreateCoreEnvironment(options) ?? CreateFrameworkEnvironment(options);
        }

        return buildEnvironment ?? throw new InvalidOperationException("Could not find build environment");
    }

    // Based on code from OmniSharp
    // https://github.com/OmniSharp/omnisharp-roslyn/blob/78ccc8b4376c73da282a600ac6fb10fce8620b52/src/OmniSharp.Abstractions/Services/DotNetCliService.cs
    private BuildEnvironment? CreateCoreEnvironment(EnvironmentOptions options)
    {
        // Get paths
        var resolver = new DotNetInfoResolver(_manager.LoggerFactory);
        var info = resolver.Resolve(IO.IOPath.Parse(_projectFile.Path), IO.IOPath.Parse(options.DotnetExePath));

        if ((info.BasePath ?? info.Runtimes.Values.FirstOrDefault()) is not { } dotnetPath)
        {
            Logger.LogWarning("Could not locate SDK path in `{DotnetPath} --info` results", options.DotnetExePath);
            return null;
        }

        string msBuildExePath = Path.Combine(dotnetPath, "MSBuild.dll");
        if (options.EnvironmentVariables.ContainsKey(EnvironmentVariables.MSBUILD_EXE_PATH))
        {
            msBuildExePath = options.EnvironmentVariables[EnvironmentVariables.MSBUILD_EXE_PATH];
        }

        // Clone the options global properties dictionary so we can add to it
        Dictionary<string, string> additionalGlobalProperties = new Dictionary<string, string>(options.GlobalProperties);

        // Required to force CoreCompile target when it calculates everything is already built
        // This can happen if the file wasn't previously generated (Clean only cleans what was in that file)
        // Only required if we're not running a design-time build (otherwise the targets will be replaced anyway)
        if (options.TargetsToBuild.Contains("Clean", StringComparer.OrdinalIgnoreCase))
        {
            additionalGlobalProperties.Add(MsBuildProperties.NonExistentFile, Path.Combine("__NonExistentSubDir__", "__NonExistentFile__"));
        }

        // Clone the options global properties dictionary so we can add to it
        Dictionary<string, string> additionalEnvironmentVariables = new Dictionary<string, string>(options.EnvironmentVariables);

        // (Re)set the environment variables that dotnet sets
        // See https://github.com/dotnet/cli/blob/0a4ad813ff971f549d34ac4ebc6c8cca9a741c36/src/Microsoft.DotNet.Cli.Utils/MSBuildForwardingAppWithoutLogging.cs#L22-L28
        // Especially important if a global.json is used because dotnet may set these to the latest, but then we'll call a msbuild.dll for the global.json version
        if (!additionalEnvironmentVariables.ContainsKey(EnvironmentVariables.MSBuildExtensionsPath))
        {
            additionalEnvironmentVariables.Add(EnvironmentVariables.MSBuildExtensionsPath, dotnetPath);
        }
        if (!additionalEnvironmentVariables.ContainsKey(EnvironmentVariables.MSBuildSDKsPath))
        {
            additionalEnvironmentVariables.Add(EnvironmentVariables.MSBuildSDKsPath, Path.Combine(dotnetPath, "Sdks"));
        }
        if (!additionalEnvironmentVariables.ContainsKey(EnvironmentVariables.COREHOST_TRACE))
        {
            additionalEnvironmentVariables.Add(EnvironmentVariables.COREHOST_TRACE, "0");
        }
        if (!additionalEnvironmentVariables.ContainsKey(EnvironmentVariables.DOTNET_HOST_PATH))
        {
            additionalEnvironmentVariables.Add(
                EnvironmentVariables.DOTNET_HOST_PATH,
                Path.GetFullPath(Path.Combine(dotnetPath, "..", "..", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet")));
        }

        return new BuildEnvironment(
            options.DesignTime,
            options.Restore,
            [.. options.TargetsToBuild],
            msBuildExePath,
            options.DotnetExePath,
            options.Arguments,
            additionalGlobalProperties,
            additionalEnvironmentVariables,
            options.WorkingDirectory);
    }

    private BuildEnvironment? CreateFrameworkEnvironment(EnvironmentOptions options)
    {
        // Clone the options global properties dictionary so we can add to it
        Dictionary<string, string> additionalGlobalProperties = new Dictionary<string, string>(options.GlobalProperties);

        // Required to force CoreCompile target when it calculates everything is already built
        // This can happen if the file wasn't previously generated (Clean only cleans what was in that file)
        // Only required if we're not running a design-time build (otherwise the targets will be replaced anyway)
        if (options.TargetsToBuild.Contains("Clean", StringComparer.OrdinalIgnoreCase))
        {
            additionalGlobalProperties.Add(MsBuildProperties.NonExistentFile, Path.Combine("__NonExistentSubDir__", "__NonExistentFile__"));
        }

        string msBuildExePath;
        if (options.EnvironmentVariables.ContainsKey(EnvironmentVariables.MSBUILD_EXE_PATH))
        {
            msBuildExePath = options.EnvironmentVariables[EnvironmentVariables.MSBUILD_EXE_PATH];
        }
        else if (!GetFrameworkMsBuildExePath(out msBuildExePath))
        {
            Logger.LogWarning("Couldn't find a .NET Framework MSBuild path");
            return null;
        }

        // This is required to trigger NuGet package resolution and regeneration of project.assets.json
        additionalGlobalProperties.Add(MsBuildProperties.ResolveNuGetPackages, "true");

        return new BuildEnvironment(
            options.DesignTime,
            options.Restore,
            [.. options.TargetsToBuild],
            msBuildExePath,
            options.DotnetExePath,
            options.Arguments,
            additionalGlobalProperties,
            options.EnvironmentVariables,
            options.WorkingDirectory);
    }

    private bool GetFrameworkMsBuildExePath(out string msBuildExePath)
    {
        msBuildExePath = ToolLocationHelper.GetPathToBuildToolsFile("msbuild.exe", ToolLocationHelper.CurrentToolsVersion);
        if (string.IsNullOrEmpty(msBuildExePath))
        {
            // Could not find the tools path, possibly due to https://github.com/Microsoft/msbuild/issues/2369
            // Try to poll for it. From https://github.com/KirillOsenkov/MSBuildStructuredLog/blob/4649f55f900a324421bad5a714a2584926a02138/src/StructuredLogViewer/MSBuildLocator.cs
            List<DirectoryInfo> msBuildDirectories = [];

            // Search in the x86 program files
            string programFilesX86 = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);
            DirectoryInfo vsX86Directory = new DirectoryInfo(Path.Combine(programFilesX86, "Microsoft Visual Studio"));
            if (vsX86Directory.Exists)
            {
                msBuildDirectories.AddRange(vsX86Directory.GetDirectories("MSBuild", SearchOption.AllDirectories));
            }

            // Also search in x64 since VS 2022 and on is now 64-bit
            string programFiles = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
            DirectoryInfo vsDirectory = new DirectoryInfo(Path.Combine(programFiles, "Microsoft Visual Studio"));
            if (vsDirectory.Exists)
            {
                msBuildDirectories.AddRange(vsDirectory.GetDirectories("MSBuild", SearchOption.AllDirectories));
            }

            // Now order by write time to get the latest MSBuild
            msBuildExePath = msBuildDirectories
                .SelectMany(msBuildDir => msBuildDir.GetFiles("MSBuild.exe", SearchOption.AllDirectories))
                .OrderByDescending(msBuild => msBuild.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        return !string.IsNullOrEmpty(msBuildExePath);
    }

    private bool OnlyTargetsFramework(string? targetFramework)
        => targetFramework == null
            ? _projectFile.TargetFrameworks.TrueForAll(IsFrameworkTargetFramework)
            : IsFrameworkTargetFramework(targetFramework);

    // Internal for testing
    // Because the .NET Core/.NET 5 TFMs are better defined, we just check if this is one of them and then negate
    internal static bool IsFrameworkTargetFramework(string targetFramework)
    {
        NuGetFramework tfm = NuGetFramework.Parse(targetFramework);
        return tfm.Framework != ".NETStandard"
            && tfm.Framework != ".NETCoreApp";
    }
}