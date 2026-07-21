using System.Diagnostics;

namespace Buildalyzer.Differential.Tests;

/// <summary>
/// Clones a real open-source repository at a pinned tag into a throw-away temp directory,
/// pins the exact SDK the test run selected, restores a project inside it, and cleans
/// everything up on dispose. Used by the real-world differential specs to check Buildalyzer
/// against MSBuildWorkspace on projects that neither the tests nor Buildalyzer authored.
/// </summary>
/// <remarks>
/// The clone is shallow (<c>--depth 1</c>) and pinned to a tag so a run is reproducible. The
/// repository's own <c>global.json</c> (if any) is overwritten with the SDK version that
/// <see cref="MSBuildRegistration"/> selected so the out-of-process build (Buildalyzer) and the
/// in-process build (MSBuildWorkspace) run on identical MSBuild — exactly as
/// <see cref="ProjectFixture"/> does for authored projects. A repository that cannot build under
/// that SDK is itself a real-world finding: restore will fail and the test will error with the
/// build output.
/// </remarks>
public sealed class OssRepositoryFixture : IDisposable
{
    private OssRepositoryFixture(DirectoryInfo root) => Root = root;

    /// <summary>The temp directory that contains the cloned repository.</summary>
    public DirectoryInfo Root { get; }

    /// <summary>
    /// Shallow-clones <paramref name="gitUrl"/> at <paramref name="tag"/> and pins the SDK the
    /// tests are running under so both loaders build on identical MSBuild.
    /// </summary>
    public static OssRepositoryFixture Clone(string gitUrl, string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        DirectoryInfo root = new(Path.Combine(Path.GetTempPath(), "buildalyzer-oss", Guid.NewGuid().ToString("N")));
        root.Create();

        try
        {
            Run(
                "git",
                ["clone", "--depth", "1", "--branch", tag, gitUrl, root.FullName],
                workingDirectory: Path.GetTempPath());

            // Pin the exact SDK MSBuildRegistration selected, overwriting whatever the repo shipped,
            // so restore/Buildalyzer (out of process) and MSBuildWorkspace (in process) agree. dotnet
            // resolves this global.json by walking up from the build's working directory.
            File.WriteAllText(
                Path.Combine(root.FullName, "global.json"),
                $$"""{ "sdk": { "version": "{{MSBuildRegistration.SdkVersion}}", "rollForward": "disable" } }""");

            return new OssRepositoryFixture(root);
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    /// <summary>Resolves a repo-relative project path (either slash style) to a full path on disk.</summary>
    public string ProjectPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string full = Path.GetFullPath(Path.Combine(Root.FullName, relativePath.Replace('\\', '/')));
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(
                $"The cloned repository does not contain a project at '{relativePath}'. Its layout may have "
                + "changed at the pinned tag.",
                full);
        }

        return full;
    }

    /// <summary>
    /// Runs <c>dotnet restore</c> so that MSBuildWorkspace (which does not restore) has an assets
    /// file to perform its design-time build against.
    /// </summary>
    public void Restore(string projectPath)
    {
        // The working directory must sit under the repo so the pinned global.json is picked up.
        Run(
            MSBuildRegistration.DotnetExePath,
            ["restore", projectPath],
            workingDirectory: Path.GetDirectoryName(projectPath)!,
            clearMSBuildEnvironment: true);
    }

    private static void Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool clearMSBuildEnvironment = false)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (clearMSBuildEnvironment)
        {
            // Do not let the MSBuild environment set by the in-process locator leak into the child.
            startInfo.Environment.Remove("MSBUILD_EXE_PATH");
            startInfo.Environment.Remove("MSBuildExtensionsPath");
            startInfo.Environment.Remove("MSBuildSDKsPath");
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{fileName} {string.Join(' ', arguments)}' failed (exit {process.ExitCode}):"
                + $"{System.Environment.NewLine}{output}{System.Environment.NewLine}{error}");
        }
    }

    public void Dispose() => TryDelete(Root);

    private static void TryDelete(DirectoryInfo directory)
    {
        try
        {
            if (directory.Exists)
            {
                directory.Delete(recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; the OS temp reaper will handle anything left behind.
        }
    }
}
