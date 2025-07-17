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

    public ProcessData Data => new(
        [.. Output],
        [.. Error]);

    private void OutputDataReceived(object? sender, DataReceivedEventArgs e) => Add(e.Data, Output);

    private void ErrorDataReceived(object? sender, DataReceivedEventArgs e) => Add(e.Data, Error);

    private static void Add(string? value, List<string> buffer)
    {
        if (value is { Length: > 0 })
        {
            buffer.Add(value);
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
}
