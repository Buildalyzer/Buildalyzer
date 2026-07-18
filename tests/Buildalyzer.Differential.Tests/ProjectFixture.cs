using System.Diagnostics;
using Microsoft.Build.Construction;
using Microsoft.Build.Utilities.ProjectCreation;

namespace Buildalyzer.Differential.Tests;

/// <summary>
/// Authors real project files in a throw-away temp directory using
/// <c>MSBuild.ProjectCreation</c>, restores them, and cleans everything up on dispose.
/// </summary>
/// <remarks>
/// The temp directory is deliberately outside the repository so the generated projects do
/// not inherit the repository's <c>Directory.Build.*</c> or analyzer configuration. A
/// <c>global.json</c> pins the exact SDK that <see cref="MSBuildRegistration"/> selected so
/// the out-of-process build (Buildalyzer) and the in-process build (MSBuildWorkspace) agree.
/// </remarks>
public sealed class ProjectFixture : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> NoSources = new Dictionary<string, string>();

    public ProjectFixture()
    {
        Root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "buildalyzer-diff", Guid.NewGuid().ToString("N")));
        Root.Create();

        File.WriteAllText(
            Path.Combine(Root.FullName, "global.json"),
            $$"""{ "sdk": { "version": "{{MSBuildRegistration.SdkVersion}}", "rollForward": "disable" } }""");

        // Isolate the temp tree from any Directory.Build.* files higher up the filesystem.
        File.WriteAllText(Path.Combine(Root.FullName, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(Root.FullName, "Directory.Build.targets"), "<Project />");
    }

    /// <summary>The temp directory that contains every generated project.</summary>
    public DirectoryInfo Root { get; }

    /// <summary>
    /// Creates an SDK-style project in its own sub-directory, writes the given source files
    /// next to it and returns the full path to the <c>.csproj</c>.
    /// </summary>
    public string AddProject(
        string name,
        Action<ProjectCreator> configure,
        IReadOnlyDictionary<string, string>? sources = null,
        string extension = ".csproj")
    {
        ArgumentNullException.ThrowIfNull(configure);

        DirectoryInfo directory = Root.CreateSubdirectory(name);
        string projectPath = Path.Combine(directory.FullName, name + extension);

        ProjectCreator creator = ProjectCreator.Create(projectPath, sdk: "Microsoft.NET.Sdk");
        configure(creator);
        creator.Save();

        foreach ((string file, string content) in sources ?? NoSources)
        {
            string filePath = Path.Combine(directory.FullName, file);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, content);
        }

        return projectPath;
    }

    /// <summary>
    /// Runs <c>dotnet restore</c> so that MSBuildWorkspace (which does not restore) has an
    /// assets file to perform its design-time build against.
    /// </summary>
    public void Restore(string projectPath)
    {
        ProcessStartInfo startInfo = new(MSBuildRegistration.DotnetExePath)
        {
            WorkingDirectory = Path.GetDirectoryName(projectPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add(projectPath);

        // Do not let MSBuild environment set by the in-process locator leak into the child.
        startInfo.Environment.Remove("MSBUILD_EXE_PATH");
        startInfo.Environment.Remove("MSBuildExtensionsPath");
        startInfo.Environment.Remove("MSBuildSDKsPath");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start 'dotnet restore'.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'dotnet restore' failed for {projectPath} (exit {process.ExitCode}):{System.Environment.NewLine}{output}{System.Environment.NewLine}{error}");
        }
    }

    /// <summary>Adds an MSBuild item to an already-authored project.</summary>
    public static void AddItem(string projectPath, string itemType, string include)
    {
        ProjectRootElement root = ProjectRootElement.Open(projectPath)!;
        root.AddItem(itemType, include);
        root.Save();
    }

    /// <summary>Adds a <c>ProjectReference</c> from one generated project to another.</summary>
    public static void AddProjectReference(string fromProjectPath, string toProjectPath)
    {
        string relative = Path.GetRelativePath(Path.GetDirectoryName(fromProjectPath) ?? ".", toProjectPath).Replace('/', '\\');
        AddItem(fromProjectPath, "ProjectReference", relative);
    }

    public void Dispose()
    {
        try
        {
            if (Root.Exists)
            {
                Root.Delete(recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; the OS temp reaper will handle anything left behind.
        }
    }
}
