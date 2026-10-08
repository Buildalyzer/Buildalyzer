using System.Diagnostics;
using System.IO;
using Buildalyzer;
using Buildalyzer.TestTools;

namespace Parallel_binary_logs;

public class Analyze_binary_log
{
    /// <summary>
    /// A binary log recorded from a parallel build (<c>/m</c>) interleaves the events of everything that was
    /// in flight at the time, and the inner builds of a multi-targeted project are in flight together. The
    /// project used here holds its second inner build open until after the first one has compiled and
    /// finished, so the recorded order is start A, start B, compile A, finish A, compile B, finish B: a
    /// project finishing while another is still open. That is what an event processor tracking "the current
    /// project" as a stack cannot express - the finish pops B, and each build's compiler inputs land on
    /// whichever result happens to be on top rather than on its own.
    /// </summary>
    [Test]
    public void Attributes_interleaved_inner_builds_to_their_own_result()
    {
        using ProjectFileTestContext context = Context.ForProject("ParallelMultiTargetProject/ParallelMultiTargetProject.csproj");
        string binaryLogPath = Path.Combine(Path.GetTempPath(), $"buildalyzer-parallel-{Guid.NewGuid():N}.binlog");

        try
        {
            BuildInParallel(context.Location, binaryLogPath);

            IAnalyzerResults results = context.Manager.Analyze(binaryLogPath);

            SourceFileNames(results, "net8.0").Should().Contain("Net8Only.cs").And.NotContain("NetStandardOnly.cs");
            SourceFileNames(results, "netstandard2.0").Should().Contain("NetStandardOnly.cs").And.NotContain("Net8Only.cs");
        }
        finally
        {
            if (File.Exists(binaryLogPath))
            {
                File.Delete(binaryLogPath);
            }
        }
    }

    /// <summary>
    /// <c>net8.0</c> and <c>net8.0-windows</c> have the same <c>TargetFrameworkMoniker</c>; only the
    /// platform moniker tells them apart. A binary log replays both inner builds through one event
    /// processor, so keying results by moniker would fold them into a single result.
    /// </summary>
    [Test]
    public void Keeps_inner_builds_that_differ_only_by_platform_apart()
    {
        using ProjectFileTestContext context = Context.ForProject("PlatformMultiTargetProject/PlatformMultiTargetProject.csproj");
        string binaryLogPath = Path.Combine(Path.GetTempPath(), $"buildalyzer-platform-{Guid.NewGuid():N}.binlog");

        try
        {
            BuildInParallel(context.Location, binaryLogPath);

            IAnalyzerResults results = context.Manager.Analyze(binaryLogPath);

            SourceFileNames(results, "net8.0").Should().Contain("PortableOnly.cs").And.NotContain("WindowsOnly.cs");
            SourceFileNames(results, "net8.0-windows").Should().Contain("WindowsOnly.cs").And.NotContain("PortableOnly.cs");
        }
        finally
        {
            if (File.Exists(binaryLogPath))
            {
                File.Delete(binaryLogPath);
            }
        }
    }

    private static string[] SourceFileNames(IAnalyzerResults results, string targetFramework)
    {
        IAnalyzerResult result = results.Single(r => r.TargetFramework == targetFramework);
        return [.. result.SourceFiles.Select(Path.GetFileName)];
    }

    /// <summary>Builds the project with MSBuild's multi-processor scheduling, recording a binary log.</summary>
    /// <remarks>
    /// The build runs through the SDK rather than through Buildalyzer, which schedules one build per target
    /// framework and so never has two inner builds running at once.
    /// <para>
    /// Task-input logging is what puts the compiler's resolved inputs in the log, and it is a trait read
    /// once when a process starts - so the build has to happen in processes started here: node reuse off,
    /// and the MSBuild server (the test host runs with <c>MSBUILDUSESERVER=1</c>) out of the picture.
    /// <c>Rebuild</c> rather than <c>Build</c> because an up-to-date project skips <c>CoreCompile</c>
    /// altogether, and a log with no compiler inputs at all would assert exactly like a mis-attributed one.
    /// </para>
    /// </remarks>
    private static void BuildInParallel(FileInfo projectFile, string binaryLogPath)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = projectFile.Directory!.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectFile.FullName);
        startInfo.ArgumentList.Add("-t:Rebuild");
        startInfo.ArgumentList.Add("-m:4");
        startInfo.ArgumentList.Add("-nr:false");
        startInfo.ArgumentList.Add($"-bl:{binaryLogPath}");
        startInfo.Environment["MSBUILDLOGTASKINPUTS"] = "1";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["MSBUILDUSESERVER"] = "0";

        // Do not let the test host's own MSBuild environment pick the MSBuild that runs the build.
        startInfo.Environment.Remove("MSBUILD_EXE_PATH");
        startInfo.Environment.Remove("MSBuildExtensionsPath");
        startInfo.Environment.Remove("MSBuildSDKsPath");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start 'dotnet build'.");

        // Drain both pipes concurrently; a child that fills the one nobody is reading blocks forever. Each
        // stream gets its own buffer because the two callbacks run on different threads, and a StringBuilder
        // shared between them is not safe to append to concurrently; they are joined after the build has
        // exited, which is also when WaitForExit() has seen both readers reach the end of their stream.
        System.Text.StringBuilder output = new();
        System.Text.StringBuilder error = new();
        process.OutputDataReceived += (_, e) => output.AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => error.AppendLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        process.ExitCode.Should().Be(0, because: output.Append(error).ToString());
    }
}
