using System.Threading;
using System.Threading.Tasks;

namespace Buildalyzer;

/// <summary>Collects the <see cref="ProcessData"/> durring a <see cref="System.Diagnostics.Process"/>.</summary>
[DebuggerDisplay("ExitCode = {Process.ExitCode}, Output = {Process.Output.Length}, Error = {Process.Error.Length}")]
internal sealed class ProcessDataCollector : IDisposable
{
    private readonly Process Process;
    private readonly List<string> Output = [];
    private readonly List<string> Error = [];

    public ProcessDataCollector(Process process)
    {
        Process = process;
        Process.OutputDataReceived += OutputDataReceived;
        Process.ErrorDataReceived += ErrorDataReceived;
    }

    public ProcessData Data
    {
        get
        {
            // Under the same lock as the additions: the handlers can still be delivering lines after the
            // process has exited, and copying a list another thread is appending to is not safe.
            lock (Output)
            {
                return new([.. Output], [.. Error]);
            }
        }
    }

    /// <summary>Waits for both redirected streams to report that they have ended.</summary>
    /// <returns><c>true</c> if both ended within <paramref name="millisecondsTimeout"/>.</returns>
    public bool WaitForCompletion(int millisecondsTimeout) => Completed.Task.Wait(millisecondsTimeout);

    private void OutputDataReceived(object? sender, DataReceivedEventArgs e) => Add(e.Data, Output);

    private void ErrorDataReceived(object? sender, DataReceivedEventArgs e) => Add(e.Data, Error);

    private void Add(string? value, List<string> buffer)
    {
        // A null line is the end-of-stream sentinel, raised once for each redirected stream.
        if (value is null)
        {
            if (Interlocked.Decrement(ref Streams) == 0)
            {
                Completed.TrySetResult(true);
            }
        }
        else if (value.Length > 0)
        {
            lock (Output)
            {
                buffer.Add(value);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!Disposed)
        {
            Process.OutputDataReceived -= OutputDataReceived;
            Process.ErrorDataReceived -= ErrorDataReceived;
            Disposed = true;
        }
    }

    private bool Disposed;

    // A task rather than a wait handle: a stream can report its end while this is being disposed, and
    // completing a task that nobody is waiting on is harmless where setting a disposed handle is not.
    private readonly TaskCompletionSource<bool> Completed = new();

    /// <summary>The number of redirected streams that have yet to report their end.</summary>
    private int Streams = 2;
}
