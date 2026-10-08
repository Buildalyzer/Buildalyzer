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
    /// <param name="projectPath">The project to restore.</param>
    /// <param name="allowFailure">
    /// Whether a failing restore is expected: NuGet still writes the assets file when it reports an
    /// error, which is exactly the state a test of a failed restore needs on disk.
    /// </param>
    public void Restore(string projectPath, bool allowFailure = false)
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

        // A multi-project restore spawns worker nodes that, with node reuse on, outlive the restore and
        // inherit its redirected output, so WaitForExit would block on the pipe until the idle node exits
        // (15 minutes). Make the nodes exit with the restore instead.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        (int exitCode, string output, string error) = Run(startInfo, "dotnet restore");
        if (exitCode != 0 && !allowFailure)
        {
            throw new InvalidOperationException(
                $"'dotnet restore' failed for {projectPath} (exit {exitCode}):{System.Environment.NewLine}{output}{System.Environment.NewLine}{error}");
        }
    }

    /// <summary>
    /// Starts a process and returns its exit code and captured output.
    /// </summary>
    /// <remarks>
    /// Both redirected pipes are drained concurrently (via the data-received events) rather than read to
    /// EOF one after the other: a child that fills the pipe nobody is reading blocks forever, and the
    /// parent blocks waiting for EOF on the other. <see cref="Process.WaitForExit()"/> without a timeout
    /// also waits for both readers to reach end of stream, so the captured text is complete.
    /// </remarks>
    private static (int ExitCode, string Output, string Error) Run(ProcessStartInfo startInfo, string description)
    {
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{description}'.");

        System.Text.StringBuilder output = new();
        System.Text.StringBuilder error = new();
        process.OutputDataReceived += (_, e) => output.AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => error.AppendLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        return (process.ExitCode, output.ToString(), error.ToString());
    }

    /// <summary>Adds an MSBuild item to an already-authored project.</summary>
    public static void AddItem(string projectPath, string itemType, string include)
    {
        ProjectRootElement root = ProjectRootElement.Open(projectPath)!;
        root.AddItem(itemType, include);
        root.Save();
    }

    /// <summary>Adds an MSBuild item with metadata to an already-authored project.</summary>
    public static void AddItem(string projectPath, string itemType, string include, IReadOnlyDictionary<string, string> metadata)
    {
        ProjectRootElement root = ProjectRootElement.Open(projectPath)!;
        ProjectItemElement item = root.AddItem(itemType, include);
        foreach ((string name, string value) in metadata)
        {
            item.AddMetadata(name, value);
        }

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
