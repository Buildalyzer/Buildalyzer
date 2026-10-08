using System.Threading;
using System.Threading.Tasks;
using Buildalyzer.Environment;
using XenoAtom.MsBuildPipeLogger;

namespace Buildalyzer.Logging;

/// <summary>Reads a pipe logger server for as long as the build process writing to it is running.</summary>
internal static class PipeLoggerDrain
{
    /// <summary>
    /// How long to let the reader hand over what it already holds once the build process has exited.
    /// Nothing can be written to the pipe after that point, so a reader with events still to deliver
    /// reaches the end of its stream promptly.
    /// </summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long to keep reading past the settle window. The pipe is drained while the build runs, so
    /// anything still in flight at that point is a tail; the bound is here so that a transport which never
    /// reports the end of its stream costs a pause rather than the whole analysis.
    /// </summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long to let the read unwind after the server has been disposed under it.</summary>
    private static readonly TimeSpan DisposeGrace = TimeSpan.FromSeconds(5);

    /// <summary>Reads every event <paramref name="server"/> receives while <paramref name="process"/> runs.</summary>
    /// <param name="server">The pipe logger server to read.</param>
    /// <param name="process">The build process writing to the pipe.</param>
    /// <param name="received">The events received so far; tells a read that has stalled from one that is behind.</param>
    /// <remarks>
    /// The read happens on its own thread rather than on the caller's, so that waiting for the build
    /// process never depends on the pipe reaching the end of its stream. A build that exits without ever
    /// writing to the pipe leaves the server holding its own copy of the client handle, so the read would
    /// otherwise wait for an end of file that cannot arrive.
    /// </remarks>
    public static void ReadUntilExit(IPipeLoggerServer server, ProcessRunner process, IReadOnlyCollection<PipeBuildEventArgs> received)
    {
        // A dedicated thread rather than a pool one: how soon the reader hands its events over decides
        // whether the wait below reads as a stall, and that should not turn on how busy the host's pool is.
        var read = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    server.ReadAll();
                }
                catch (ObjectDisposedException)
                {
                    // The server was disposed to bring this read to an end.
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // Wait for the build process - unless the read fails first. Nobody drains the pipe after that, so
        // once its buffer fills the logger's write blocks MSBuild, which then never exits: kill it and let
        // the failure surface below rather than wait for an exit that cannot come.
        if (Task.WaitAny([read, process.Exit]) == 0 && read.IsFaulted)
        {
            process.Kill();
        }

        process.WaitForExit();

        // The build process has gone, so no further build submission can connect. This is a no-op on an
        // anonymous pipe, where the read ends by itself when the client closes its handle, but it is what
        // ends the read on a transport that serves more than one connection.
        server.StopListening();

        if (!Completed(SettleTimeout) && !Drained())
        {
            // Tearing the transport down discards whatever the reader has not handed over yet, which is why
            // it is the backstop rather than the way this normally ends.
            server.Dispose();
            Completed(DisposeGrace);
        }

        if (read.IsCompleted)
        {
            // Surface a read failure unwrapped, as reading in line did, rather than as an AggregateException.
            read.GetAwaiter().GetResult();
        }

        // Whether the build wrote to the pipe at all is only worth asking once the settle window has
        // passed: at the moment the process exits the reader may be holding events it has not dispatched
        // yet, and reading "nothing received" as "nothing was written" there would throw them away. After
        // the window, a reader that has delivered nothing is parked on a first read that cannot reach the
        // end of the file - the server holds its own copy of the client's write handle until that read
        // returns - so there is nothing left to drain and nothing for the teardown to discard. One that has
        // delivered events may merely be behind, and gets the full drain.
        bool Drained() => received.Count > 0 && Completed(DrainTimeout);

        // Task.Wait would throw a read failure of its own accord, wrapped in an AggregateException, before
        // the unwrapping above got the chance to; waiting for the task rather than on it leaves the failure
        // unobserved until then.
        bool Completed(TimeSpan timeout) => Task.WaitAny([read], timeout) == 0;
    }
}
