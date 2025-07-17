using System.IO;

namespace Buildalyzer.Environment;

/// <summary>
/// An immutable representation of a particular build environment (paths, properties, etc).
/// </summary>
public sealed class BuildEnvironment
{
    // https://docs.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.runtimeinformation.frameworkdescription
    // .NET "Core" will return ".NET Core" up to 3.x and ".NET" for > 5
    public static bool IsRunningOnCore =>
        !System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
            .Replace(" ", string.Empty)
            .Trim()
            .IsMatchStart(".NETFramework");

    private readonly Dictionary<string, string> _globalProperties;
    private readonly Dictionary<string, string> _environmentVariables;

    // Used for cloning
    private readonly IDictionary<string, string> _additionalGlobalProperties;
    private readonly IDictionary<string, string> _additionalEnvironmentVariables;

    /// <summary>
    /// Indicates that a design-time build should be performed.
    /// </summary>
    /// <remarks>
    /// See https://github.com/dotnet/project-system/blob/master/docs/design-time-builds.md.
    /// </remarks>
    public bool DesignTime { get; }

    /// <summary>
    /// Runs the restore target prior to any other targets using the MSBuild <c>restore</c> switch.
    /// </summary>
    public bool Restore { get; }

    public string[] TargetsToBuild { get; }

    public string MsBuildExePath { get; }

    public string DotnetExePath { get; }

    public string? WorkingDirectory { get; }

    /// <summary>
    /// Indicates if the <c>-noAutoResponse</c> argument should be set (the default is <c>true</c>).
    /// This is required if a <c>.rsp</c> file might conflict with the command-line arguments and binary
    /// logger that Buildalyzer uses. Setting this to false will omit the <c>-noAutoResponse</c> argument
    /// but might also result in failed builds or incomplete information being sent to Buildalyzer.
    /// See https://docs.microsoft.com/en-us/visualstudio/msbuild/msbuild-response-files.
    /// </summary>
    public bool NoAutoResponse { get; set; } = true;

    public IEnumerable<string> Arguments { get; }

    public IReadOnlyDictionary<string, string> GlobalProperties => _globalProperties;

    public IReadOnlyDictionary<string, string> EnvironmentVariables => _environmentVariables;

    public BuildEnvironment(
        bool designTime,
        bool restore,
        string[] targetsToBuild,
        string msBuildExePath,
        string dotnetExePath,
        IEnumerable<string> arguments,
        IDictionary<string, string>? additionalGlobalProperties = null,
        IDictionary<string, string>? additionalEnvironmentVariables = null,
        string? workingDirectory = null)
    {
        DesignTime = designTime;
        Restore = restore;
        TargetsToBuild = Guard.NotNull(targetsToBuild);
        Arguments = Guard.NotNull(arguments);
        WorkingDirectory = workingDirectory;

        // Check if we've already specified a path to MSBuild
        string? envMsBuildExePath = System.Environment.GetEnvironmentVariable(Environment.EnvironmentVariables.MSBUILD_EXE_PATH);
        MsBuildExePath = !string.IsNullOrEmpty(envMsBuildExePath) && File.Exists(envMsBuildExePath)
            ? envMsBuildExePath : msBuildExePath;
        if (string.IsNullOrWhiteSpace(MsBuildExePath) && string.IsNullOrWhiteSpace(dotnetExePath))
        {
            throw new ArgumentNullException(nameof(msBuildExePath));
        }

        // The dotnet path defaults to "dotnet" - if it's null then the user changed it and we should warn them
        DotnetExePath = Guard.NotNull(dotnetExePath);

        // Set global properties
        _globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { MsBuildProperties.ProvideCommandLineArgs, "true" },

            // Workaround for a problem with resource files, see https://github.com/dotnet/sdk/issues/346#issuecomment-257654120
            { MsBuildProperties.GenerateResourceMSBuildArchitecture, "CurrentArchitecture" },

            // MsBuildProperties.SolutionDir will get set by ProjectAnalyzer
        };
        if (DesignTime)
        {
            _globalProperties = CopyItems(_globalProperties, MsBuildProperties.DesignTime);
        }
        _additionalGlobalProperties = CopyItems(_globalProperties, additionalGlobalProperties);

        // Set environment variables
        _environmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _additionalEnvironmentVariables = CopyItems(_environmentVariables, additionalEnvironmentVariables);
    }

    private Dictionary<string, string> CopyItems(Dictionary<string, string> destination, IEnumerable<KeyValuePair<string, string>> source)
    {
        if (source != null)
        {
            foreach (KeyValuePair<string, string> item in source)
            {
                destination[item.Key] = item.Value;
            }

            // Copy to a new dictionary in case the source dictionary is mutated
            return new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
        }
        return null;
    }

    /// <summary>
    /// Clones the build environment with a different set of build targets.
    /// </summary>
    /// <param name="targets">
    /// The targets that should be used to build the project.
    /// Specifying an empty array indicates that the <see cref="ProjectAnalyzer"/> should
    /// return a <see cref="Microsoft.Build.Execution.ProjectInstance"/> without building the project.
    /// </param>
    /// <returns>A new build environment with the specified targets.</returns>
    public BuildEnvironment WithTargetsToBuild(params string[] targets) => new(
            DesignTime,
            Restore,
            targets,
            MsBuildExePath,
            DotnetExePath,
            Arguments,
            _additionalGlobalProperties,
            _additionalEnvironmentVariables,
            WorkingDirectory);
}