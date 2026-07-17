using System.IO;
using System.Runtime.InteropServices;
using Buildalyzer.Construction;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

        var msBuildExePath = options.EnvironmentVariables.TryGetValue(EnvironmentVariables.MSBUILD_EXE_PATH, out var path)
            ? path
            : Path.Combine(dotnetPath, "MSBuild.dll");

        // Clone the options global properties dictionary so we can add to it
        Dictionary<string, string> additionalGlobalProperties = new Dictionary<string, string>(options.GlobalProperties);

        // Required to force CoreCompile target when it calculates everything is already built.
        // CoreCompile's Inputs include $(NonExistentFile); pointing it at a path that does not
        // exist makes MSBuild see CoreCompile as out-of-date on every invocation, so Csc fires
        // its TaskCommandLineEventArgs (under SkipCompilerExecution=true) every time.
        additionalGlobalProperties.Add(MsBuildProperties.NonExistentFile, Path.Combine("__NonExistentSubDir__", "__NonExistentFile__"));

        // Clone the options global properties dictionary so we can add to it
        Dictionary<string, string> additionalEnvironmentVariables = new Dictionary<string, string>(options.EnvironmentVariables);

        // (Re)set the environment variables that dotnet sets
        // See https://github.com/dotnet/cli/blob/0a4ad813ff971f549d34ac4ebc6c8cca9a741c36/src/Microsoft.DotNet.Cli.Utils/MSBuildForwardingAppWithoutLogging.cs#L22-L28
        // Especially important if a global.json is used because dotnet may set these to the latest, but then we'll call a msbuild.dll for the global.json version
        additionalEnvironmentVariables.TryAdd(EnvironmentVariables.MSBuildExtensionsPath, dotnetPath);
        additionalEnvironmentVariables.TryAdd(EnvironmentVariables.MSBuildSDKsPath, Path.Combine(dotnetPath, "Sdks"));
        additionalEnvironmentVariables.TryAdd(EnvironmentVariables.COREHOST_TRACE, "0");

        // Have MSBuild generate task-input parameter events so the compiler task's resolved Sources/References
        // are available as structured items. The BuildalyzerLogger opts into their delivery via
        // IEventSource4.IncludeTaskInputs() (no diagnostic verbosity needed) and forwards only the compiler's
        // input item groups. MSBuild reads this variable through Traits at node startup, so node reuse is off.
        additionalEnvironmentVariables.TryAdd("MSBUILDLOGTASKINPUTS", "1");
        additionalEnvironmentVariables.TryAdd(EnvironmentVariables.MSBUILDDISABLENODEREUSE, "1");

        additionalEnvironmentVariables.TryAdd(
            EnvironmentVariables.DOTNET_HOST_PATH,
            Path.GetFullPath(Path.Combine(dotnetPath, "..", "..", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet")));

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

        // Required to force CoreCompile target when it calculates everything is already built.
        // CoreCompile's Inputs include $(NonExistentFile); pointing it at a path that does not
        // exist makes MSBuild see CoreCompile as out-of-date on every invocation, so Csc fires
        // its TaskCommandLineEventArgs (under SkipCompilerExecution=true) every time.
        additionalGlobalProperties.Add(MsBuildProperties.NonExistentFile, Path.Combine("__NonExistentSubDir__", "__NonExistentFile__"));

        if (!options.EnvironmentVariables.TryGetValue(EnvironmentVariables.MSBUILD_EXE_PATH, out var msBuildExePath)
            && !GetFrameworkMsBuildExePath(out msBuildExePath))
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

    // Locate .NET Framework MSBuild by polling the Visual Studio install directories. This previously started
    // from ToolLocationHelper.GetPathToBuildToolsFile, but that pulled in Microsoft.Build.Utilities.Core just for
    // one Windows-only lookup; the poll (adapted from MSBuildStructuredLog's locator) covers VS 2017+ full and
    // Build Tools installs, which is what GetPathToBuildToolsFile resolves to on modern machines anyway.
    // https://github.com/KirillOsenkov/MSBuildStructuredLog/blob/4649f55f900a324421bad5a714a2584926a02138/src/StructuredLogViewer/MSBuildLocator.cs
    private static bool GetFrameworkMsBuildExePath(out string msBuildExePath)
    {
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

        return !string.IsNullOrEmpty(msBuildExePath);
    }

    private bool OnlyTargetsFramework(string? targetFramework)
        => targetFramework == null
            ? _projectFile.TargetFrameworks.TrueForAll(IsFrameworkTargetFramework)
            : IsFrameworkTargetFramework(targetFramework);

    // Internal for testing
    // Determines whether a TFM requires desktop .NET Framework. .NET Standard (netstandard*), .NET Core
    // (netcoreapp*) and .NET 5+ (netX[.Y][-platform], major >= 5) are not Framework; everything else -
    // including desktop Framework monikers (net11..net48) and the more exotic legacy TFMs (netcore*, netmf,
    // sl*, wp*, uap*) - is treated as .NET Framework, matching the previous NuGet.Frameworks-based behaviour.
    internal static bool IsFrameworkTargetFramework(string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            return false;
        }

        string tfm = targetFramework.Trim();

        if (tfm.IsMatchStart("netstandard") || tfm.IsMatchStart("netcoreapp"))
        {
            return false;
        }

        // A plain versioned "net" TFM: "net" immediately followed by a version digit.
        if (tfm.IsMatchStart("net") && tfm.Length > 3 && char.IsDigit(tfm[3]))
        {
            string version = tfm[3..];
            int dot = version.IndexOf('.');

            // Dotted versions carry the full major before the dot (net5.0, net10.0); packed versions encode
            // the major as the first digit (net45 -> 4.5, net472 -> 4.7.2, net5 -> 5).
            int major = dot > 0 && int.TryParse(version[..dot], out int dotted)
                ? dotted
                : version[0] - '0';

            // .NET 5 and later are not desktop .NET Framework.
            return major < 5;
        }

        return true;
    }
}
