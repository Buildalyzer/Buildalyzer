namespace Buildalyzer.Environment;

public class EnvironmentOptions
{
    /// <summary>
    /// Indicates a preferences towards the build environment to use.
    /// The default is a preference for the .NET Core SDK.
    /// </summary>
    public EnvironmentPreference Preference { get; set; } = EnvironmentPreference.Core;

    /// <summary>
    /// The targets to build. Defaults to <c>["Compile"]</c>, which mirrors
    /// what Visual Studio's design-time builds drive: it pulls in
    /// <c>ResolveReferences</c>, <c>GenerateAssemblyInfo</c> and
    /// <c>CoreCompile</c> (so <c>Csc</c> emits <see cref="MsBuildProperties.ProvideCommandLineArgs"/>
    /// data Buildalyzer parses), but stops before <c>AfterCompile</c>,
    /// <c>CopyFilesToOutputDirectory</c> and <c>AfterBuild</c>. This avoids
    /// running <c>BeforeBuild</c>/<c>AfterBuild</c> hooks (and any
    /// third-party tasks they reach) which do not contribute to the data
    /// Buildalyzer surfaces.
    /// </summary>
    /// <remarks>
    /// See https://github.com/dotnet/project-system/blob/main/docs/design-time-builds.md.
    /// </remarks>
    public List<string> TargetsToBuild { get; } = ["Compile"];

    /// <summary>
    /// Indicates that a design-time build should be performed.
    /// The default value is <c>true</c>. When set, the global properties
    /// from <see cref="MsBuildProperties.DesignTime"/> are applied so that
    /// the targets in <see cref="TargetsToBuild"/> behave like Visual
    /// Studio's design-time build (no compiler execution, no output copy,
    /// no binding redirects, etc.).
    /// </summary>
    /// <remarks>
    /// See https://github.com/dotnet/project-system/blob/main/docs/design-time-builds.md.
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