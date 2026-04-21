using System.Collections.Frozen;

namespace Buildalyzer.Environment;

public static class MsBuildProperties
{
    // MSBuild Project Loading
    public const string MSBuildExtensionsPath = nameof(MSBuildExtensionsPath);
    public const string MSBuildExtensionsPath32 = nameof(MSBuildExtensionsPath32);
    public const string MSBuildExtensionsPath64 = nameof(MSBuildExtensionsPath64);
    public const string MSBuildSDKsPath = nameof(MSBuildSDKsPath);
    public const string RoslynTargetsPath = nameof(RoslynTargetsPath);
    public const string SolutionDir = nameof(SolutionDir);
    public const string TargetFramework = nameof(TargetFramework);

    // Design-time Build
    // New project system (https://github.com/dotnet/project-system/blob/main/docs/design-time-builds.md#determining-whether-a-target-is-running-in-a-design-time-build)
    public const string DesignTimeBuild = nameof(DesignTimeBuild);

    // Legacy (https://github.com/dotnet/project-system/blob/main/docs/design-time-builds.md#determining-whether-a-target-is-running-in-a-design-time-build)
    public const string BuildingProject = nameof(BuildingProject);
    public const string BuildProjectReferences = nameof(BuildProjectReferences);
    public const string SkipCompilerExecution = nameof(SkipCompilerExecution);
    public const string ProvideCommandLineArgs = nameof(ProvideCommandLineArgs);
    public const string DisableRarCache = nameof(DisableRarCache);
    public const string AutoGenerateBindingRedirects = nameof(AutoGenerateBindingRedirects);
    public const string CopyBuildOutputToOutputDirectory = nameof(CopyBuildOutputToOutputDirectory);
    public const string CopyOutputSymbolsToOutputDirectory = nameof(CopyOutputSymbolsToOutputDirectory);
    public const string CopyDocumentationFileToOutputDirectory = nameof(CopyDocumentationFileToOutputDirectory);
    public const string ComputeNETCoreBuildOutputFiles = nameof(ComputeNETCoreBuildOutputFiles);
    public const string SkipCopyBuildProduct = nameof(SkipCopyBuildProduct);
    public const string AddModules = nameof(AddModules);
    public const string UseCommonOutputDirectory = nameof(UseCommonOutputDirectory);
    public const string GeneratePackageOnBuild = nameof(GeneratePackageOnBuild);

    // .NET Framework code analysis rulesets
    public const string CodeAnalysisRuleDirectories = nameof(CodeAnalysisRuleDirectories);
    public const string CodeAnalysisRuleSetDirectories = nameof(CodeAnalysisRuleSetDirectories);

    // NuGet
    public const string ResolveNuGetPackages = nameof(ResolveNuGetPackages);
    public const string NuGetRestoreTargets = nameof(NuGetRestoreTargets);

    // Others
    public const string GenerateResourceMSBuildArchitecture = nameof(GenerateResourceMSBuildArchitecture);
    public const string NonExistentFile = nameof(NonExistentFile);

    // See https://github.com/daveaglick/Buildalyzer/issues/211
    public const string NoAutoResponse = nameof(NoAutoResponse);

    /// <summary>
    /// Gets the MS Build properties equivalent to Visual Studio's DesignTime build.
    /// </summary>
    /// <remarks>
    /// The actual design-time tasks aren't available outside of Visual Studio,
    /// so we can't do a "real" design-time build and have to fake it with various global properties
    /// See https://github.com/dotnet/msbuild/blob/fb700f90493a0bf47623511edf28b1d6c114e4fa/src/Tasks/Microsoft.CSharp.CurrentVersion.targets#L320
    /// To diagnose build failures in design-time mode, generate a binary log and find the filing target,
    /// then see if there's a condition or property that can be used to modify it's behavior or turn it off.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> DesignTime = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [DesignTimeBuild] = "true",

        // Supports Framework projects: https://github.com/dotnet/project-system/blob/main/docs/design-time-builds.md#determining-whether-a-target-is-running-in-a-design-time-build
        [BuildingProject] = "false",
        [BuildProjectReferences] = "false",
        [SkipCompilerExecution] = "true",
        [DisableRarCache] = "true",
        [AutoGenerateBindingRedirects] = "false",
        [CopyBuildOutputToOutputDirectory] = "false",
        [CopyOutputSymbolsToOutputDirectory] = "false",
        [CopyDocumentationFileToOutputDirectory] = "false",

        // Prevents the CreateAppHost task from running, which doesn't add the apphost.exe to the files to copy
        [ComputeNETCoreBuildOutputFiles] = "false",
        [SkipCopyBuildProduct] = "true",
        [AddModules] = "false",

        // This is used in a condition to prevent copying in _CopyFilesMarkedCopyLocal
        [UseCommonOutputDirectory] = "true",

        // Prevent NuGet.Build.Tasks.Pack.targets from running the pack targets (since we didn't build anything)
        [GeneratePackageOnBuild] = "false",
    }
    .ToFrozenDictionary();
}
