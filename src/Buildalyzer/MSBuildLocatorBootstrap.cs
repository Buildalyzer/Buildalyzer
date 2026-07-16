using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace Buildalyzer;

/// <summary>
/// Registers the MSBuild assemblies from the installed .NET SDK before any <c>Microsoft.Build.*</c>
/// type is loaded.
/// </summary>
/// <remarks>
/// Buildalyzer compiles against a fixed <c>Microsoft.Build</c> version but reads binary-log events
/// streamed from the SDK's MSBuild (a potentially newer version). The binlog reader must be at least
/// as new as the writer, otherwise it rejects records it doesn't recognise. By excluding the runtime
/// assets of the <c>Microsoft.Build.*</c> packages and letting <see cref="MSBuildLocator"/> load them
/// from the SDK, the reader always matches the MSBuild that produced the events.
///
/// This runs as a module initializer so it executes before the first access to any Buildalyzer type
/// (and therefore before any <c>Microsoft.Build.*</c> assembly is loaded). The method deliberately
/// references only <see cref="MSBuildLocator"/>, which does not depend on the MSBuild assemblies.
/// </remarks>
internal static class MSBuildLocatorBootstrap
{
    // MSBuildLocator.RegisterDefaults() sets these process-wide environment variables so that an
    // in-process MSBuild can evaluate. Buildalyzer instead runs builds out-of-process with an
    // environment it constructs itself, and BuildEnvironment treats MSBUILD_EXE_PATH as a caller
    // override, so letting the locator's values persist would silently redirect every build (including
    // .NET Framework projects that need desktop MSBuild) at the SDK's MSBuild. We only need the locator's
    // in-process assembly resolver - which it stores internally, independent of these variables - so we
    // restore them after registering.
    private static readonly string[] LocatorEnvironmentVariables =
    [
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildSDKsPath",
    ];

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (MSBuildLocator.IsRegistered)
        {
            return;
        }

        var saved = LocatorEnvironmentVariables.ToDictionary(
            name => name,
            System.Environment.GetEnvironmentVariable);

        try
        {
            MSBuildLocator.RegisterDefaults();
        }
        catch (InvalidOperationException)
        {
            // The host process already loaded MSBuild assemblies (e.g. it is itself an MSBuild task
            // or registered MSBuildLocator differently). Use whatever is already loaded.
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                System.Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
