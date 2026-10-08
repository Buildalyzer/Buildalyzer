using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Buildalyzer.Environment;
using Buildalyzer.Logging;
using XenoAtom.MsBuildPipeLogger;

namespace Buildalyzer.Tests.Logging;

public class PipeLoggerDrain_specs
{
    [Test(Description = "A read that fails must not leave the analysis waiting on a build that can no longer exit")]
    public void Surfaces_a_failed_read_instead_of_waiting_for_the_process()
    {
        // A process that outlives the test unless the drain ends it: MSBuild in the real case, blocked on
        // a pipe write that nobody reads any more.
        (string fileName, string arguments) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ("ping", "-n 61 127.0.0.1")
            : ("sleep", "60");
        using var process = new ProcessRunner(fileName, arguments, Path.GetTempPath(), [], null);
        process.Start();

        using var server = new FailingServer();
        var stopwatch = Stopwatch.StartNew();

        Action read = () => PipeLoggerDrain.ReadUntilExit(server, process, []);

        read.Should().Throw<InvalidOperationException>().WithMessage("The pipe is broken.");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        process.Exit.IsCompleted.Should().BeTrue();
    }

    private sealed class FailingServer : IPipeLoggerServer
    {
        public PipeBuildEventArgs? Read() => throw new InvalidOperationException("The pipe is broken.");

        public void ReadAll() => throw new InvalidOperationException("The pipe is broken.");

        public void StopListening()
        {
        }

        public void Dispose()
        {
        }
    }
}
