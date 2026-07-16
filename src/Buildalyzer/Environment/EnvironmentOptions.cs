namespace Buildalyzer.Environment;

public class EnvironmentOptions
{
    /// <summary>
    /// Indicates a preferences towards the build environment to use.
    /// The default is a preference for the .NET Core SDK.
    /// </summary>
    public EnvironmentPreference Preference { get; set; } = EnvironmentPreference.Core;

    /// <summary>
    /// The targets to build. Defaults to <c>["Compile"]</c>, the target driven by
    /// design-time builds in Visual Studio and Roslyn's <c>MSBuildWorkspace</c>.
    /// </summary>
    /// <remarks>
    /// Targets hooked into the wider <c>Build</c> closure (such as <c>BeforeBuild</c>/<c>AfterBuild</c>
    /// hooks and WPF's <c>MarkupCompilePass2</c>) do not run under <c>Compile</c>.
    /// Callers that need the source files those targets generate should set this
    /// to <c>["Build"]</c>. See https://github.com/dotnet/project-system/blob/main/docs/design-time-builds.md.
    /// </remarks>
    public List<string> TargetsToBuild { get; } = ["Compile"];

    /// <summary>
    /// Indicates that a design-time build should be performed. The default is <c>true</c>.
    /// </summary>
    /// <remarks>
    /// See https://github.com/dotnet/project-system/blob/main/docs/design-time-builds.md.
    /// Note that some SDKs rely on the design-time properties this sets to generate sources
    /// under the default <see cref="TargetsToBuild"/> — e.g. WPF only runs its design-time
    /// markup compilation (which generates the XAML <c>*.g.cs</c> files) when
    /// <c>DesignTimeBuild</c> is set. When setting this to <c>false</c>, also set
    /// <see cref="TargetsToBuild"/> to <c>["Build"]</c> to run full code generation.
    /// </remarks>
    public bool DesignTime { get; set; } = true;

    /// <summary>
    /// Runs the restore target prior to any other targets using the MSBuild <c>restore</c> switch.
    /// </summary>
    /// <remarks>
    /// See https://github.com/Microsoft/msbuild/pull/2414.
    /// </remarks>
    public bool Restore { get; set; } = true;

    /// <summary>
    /// The full path to the <c>dotnet</c> executable you want to use for the build when building
    /// projects using the .NET Core SDK. Defaults to <c>dotnet</c> which will look in folders
    /// specified in the path environment variable.
    /// </summary>
    /// <remarks>
    /// Set this to something else to customize the .NET Core runtime you want to use (I.e., preview versions).
    /// </remarks>
    public string DotnetExePath { get; set; } = "dotnet";

    /// <summary>
    /// The global MSBuild properties to set.
    /// </summary>
    public IDictionary<string, string> GlobalProperties { get; } = new Dictionary<string, string>();

    /// <summary>
    /// Environment variables to set.
    /// </summary>
    public IDictionary<string, string> EnvironmentVariables { get; } = new Dictionary<string, string>();

    /// <summary>
    /// Additional MSBuild command-line arguments to use.
    /// </summary>
    public IList<string> Arguments { get; } = [];

    /// <summary>
    /// Specifies an alternate working directory to use for the build instead of the project file directory.
    /// Set to (or keep as) null to use the directory of the project file being built as the working directory.
    /// </summary>
    public string WorkingDirectory { get; set; }
}
