﻿using System;
using System.Collections.Generic;
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
            .StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase);

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

    public string WorkingDirectory { get; }

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
        IDictionary<string, string> additionalGlobalProperties = null,
        IDictionary<string, string> additionalEnvironmentVariables = null,
        string workingDirectory = null)
    {
        DesignTime = designTime;
        Restore = restore;
        TargetsToBuild = targetsToBuild ?? throw new ArgumentNullException(nameof(targetsToBuild));
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        WorkingDirectory = workingDirectory;

        // Check if we've already specified a path to MSBuild
        string envMsBuildExePath = System.Environment.GetEnvironmentVariable(Environment.EnvironmentVariables.MSBUILD_EXE_PATH);
        MsBuildExePath = !string.IsNullOrEmpty(envMsBuildExePath) && File.Exists(envMsBuildExePath)
            ? envMsBuildExePath : msBuildExePath;
        if (string.IsNullOrWhiteSpace(MsBuildExePath) && string.IsNullOrWhiteSpace(dotnetExePath))
        {
            throw new ArgumentNullException(nameof(msBuildExePath));
        }

        // The dotnet path defaults to "dotnet" - if it's null then the user changed it and we should warn them
        DotnetExePath = dotnetExePath ?? throw new ArgumentNullException(nameof(dotnetExePath));

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
            // The actual design-time tasks aren't available outside of Visual Studio,
            // so we can't do a "real" design-time build and have to fake it with various global properties
            // See https://github.com/dotnet/msbuild/blob/fb700f90493a0bf47623511edf28b1d6c114e4fa/src/Tasks/Microsoft.CSharp.CurrentVersion.targets#L320
            // To diagnose build failures in design-time mode, generate a binary log and find the filing target,
            // then see if there's a condition or property that can be used to modify it's behavior or turn it off
            _globalProperties.Add(MsBuildProperties.DesignTimeBuild, "true");
            _globalProperties.Add(MsBuildProperties.BuildingProject, "false"); // Supports Framework projects: https://github.com/dotnet/project-system/blob/main/docs/design-time-builds.md#determining-whether-a-target-is-running-in-a-design-time-build
            _globalProperties.Add(MsBuildProperties.BuildProjectReferences, "false");
            _globalProperties.Add(MsBuildProperties.SkipCompilerExecution, "true");
            _globalProperties.Add(MsBuildProperties.DisableRarCache, "true");
            _globalProperties.Add(MsBuildProperties.AutoGenerateBindingRedirects, "false");
            _globalProperties.Add(MsBuildProperties.CopyBuildOutputToOutputDirectory, "false");
            _globalProperties.Add(MsBuildProperties.CopyOutputSymbolsToOutputDirectory, "false");
            _globalProperties.Add(MsBuildProperties.CopyDocumentationFileToOutputDirectory, "false");
            _globalProperties.Add(MsBuildProperties.ComputeNETCoreBuildOutputFiles, "false"); // Prevents the CreateAppHost task from running, which doesn't add the apphost.exe to the files to copy
            _globalProperties.Add(MsBuildProperties.SkipCopyBuildProduct, "true");
            _globalProperties.Add(MsBuildProperties.AddModules, "false");
            _globalProperties.Add(MsBuildProperties.UseCommonOutputDirectory, "true");  // This is used in a condition to prevent copying in _CopyFilesMarkedCopyLocal
            _globalProperties.Add(MsBuildProperties.GeneratePackageOnBuild, "false");  // Prevent NuGet.Build.Tasks.Pack.targets from running the pack targets (since we didn't build anything)
        }
        _additionalGlobalProperties = CopyItems(_globalProperties, additionalGlobalProperties);

        // Set environment variables
        _environmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _additionalEnvironmentVariables = CopyItems(_environmentVariables, additionalEnvironmentVariables);
    }

    private Dictionary<string, string> CopyItems(Dictionary<string, string> destination, IDictionary<string, string> source)
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
    public BuildEnvironment WithTargetsToBuild(params string[] targets) =>
        new BuildEnvironment(
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