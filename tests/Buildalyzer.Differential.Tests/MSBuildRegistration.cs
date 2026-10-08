using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace Buildalyzer.Differential.Tests;

/// <summary>
/// Registers the .NET SDK's MSBuild with <see cref="MSBuildLocator"/> before any
/// <c>Microsoft.Build.*</c> type is touched. This is required so that both
/// <c>MSBuild.ProjectCreation</c> (used to author the test projects) and
/// <c>MSBuildWorkspace</c> (used as the reference project loader) run against a single,
/// consistent copy of MSBuild loaded from the SDK.
/// </summary>
/// <remarks>
/// The module initializer runs when this assembly is loaded, ahead of any test code, which
/// guarantees the locator is registered before MSBuild is loaded. The SDK that the locator
/// selects is captured so the generated projects can pin the exact same SDK via a
/// <c>global.json</c>; that keeps the out-of-process build performed by Buildalyzer and the
/// in-process build performed by MSBuildWorkspace on identical footing.
/// </remarks>
internal static class MSBuildRegistration
{
    /// <summary>The exact SDK version selected by the locator (e.g. <c>10.0.301</c>).</summary>
    public static string SdkVersion { get; private set; } = string.Empty;

    /// <summary>The <c>dotnet</c> executable that owns the selected SDK.</summary>
    public static string DotnetExePath { get; private set; } = "dotnet";

    [ModuleInitializer]
    public static void Register()
    {
        if (MSBuildLocator.IsRegistered)
        {
            return;
        }

        VisualStudioInstance instance = MSBuildLocator.RegisterDefaults();

        // For a .NET SDK, MSBuildPath is the SDK directory itself (e.g. .../dotnet/sdk/10.0.301).
        // Its name is the exact SDK version string and its grandparent is the dotnet root.
        DirectoryInfo sdkDirectory = new(instance.MSBuildPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        SdkVersion = sdkDirectory.Name;

        DirectoryInfo? dotnetRoot = sdkDirectory.Parent?.Parent;
        if (dotnetRoot is not null)
        {
            string exe = Path.Combine(dotnetRoot.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(exe))
            {
                DotnetExePath = exe;
            }
        }
    }
}
