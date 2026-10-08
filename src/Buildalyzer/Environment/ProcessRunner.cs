using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Buildalyzer.Environment;

internal sealed class ProcessRunner : IDisposable
{
    private readonly ILogger Logger;
    private readonly ProcessDataCollector Collector;

    /// <summary>What to write to the process' standard input once it has started, if anything.</summary>
    private readonly string? StandardInput;

    public int ExitCode => Process.ExitCode;

    public ProcessData Data => Collector.Data;

    private Process Process { get; }

    public Action Exited { get; set; }

    /// <summary>
    /// Completes once the process has exited. Unlike <see cref="System.Diagnostics.Process.WaitForExitAsync"/> this does
    /// not also wait for the redirected output streams to reach end of file (see <see cref="WaitForExit()"/>).
    /// </summary>
    public Task Exit => ExitSource.Task;

    private readonly TaskCompletionSource<bool> ExitSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProcessRunner(
        string fileName,
        string arguments,
        string workingDirectory,
        Dictionary<string, string?> environmentVariables,
        ILoggerFactory? loggerFactory,
        string? standardInput = null)
    {
        Logger = loggerFactory?.CreateLogger<ProcessRunner>() ?? NullLogger<ProcessRunner>.Instance;
        StandardInput = standardInput;
        Process = new Process
        {
            StartInfo =
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null
            },

            // Raises Process.Exited immediately instead of when checked via .WaitForExit() or .HasExited
            EnableRaisingEvents = true,
        };

        // Copy over environment variables. A null value unsets the variable in the child: it is removed
        // rather than assigned, because on the .NET 8 runtime a null entry in StartInfo.Environment is
        // passed to the child as an empty string (only .NET 9 and later treat it as absent).
        if (environmentVariables != null)
        {
            foreach (KeyValuePair<string, string> variable in environmentVariables)
            {
                if (variable.Value is null)
                {
                    Process.StartInfo.Environment.Remove(variable.Key);
                }
                else
                {
                    Process.StartInfo.Environment[variable.Key] = variable.Value;
                }
            }
        }

        Process.OutputDataReceived += OutputDataReceived;
        Process.ErrorDataReceived += ErrorDataReceived;
        Process.Exited += OnExit;

        Collector = new(Process);
    }

    public ProcessRunner Start()
    {
        Process.Start();
        Process.BeginOutputReadLine();
        Process.BeginErrorReadLine();

        // Closing the stream is part of the message: a process that reads until end of input (such as
        // `dotnet run-api`) only responds and exits once it has seen it.
        if (StandardInput is { } input)
        {
            using var writer = Process.StandardInput;
            writer.WriteLine(input);
        }

        Logger.LogDebug(
            "Started process {ProcessId}: \"{FileName}\" {Arguments}{NewLine}",
            Process.Id,
            Process.StartInfo.FileName,
            Process.StartInfo.Arguments,
            System.Environment.NewLine);
        return this;
    }

    /// <summary>Waits for the process to exit.</summary>
    /// <remarks>
    /// Deliberately not <see cref="Process.WaitForExit()"/>: with output redirected that overload also waits
    /// for the standard output and error streams to reach end of file, and those streams outlive the build.
    /// MSBuild leaves task host nodes running after it exits and they inherited the write end, so end of file
    /// can arrive minutes later - a build that finished in seconds would block here until it does. The
    /// timeout overload waits on the process alone (which is why the documentation tells you to follow it
    /// with the parameterless one to flush the handlers), so loop on that instead. The output collected here
    /// only feeds debug logging, so losing whatever tail is still in flight costs nothing.
    /// </remarks>
    public void WaitForExit()
    {
        while (!Process.WaitForExit(WaitInterval))
        {
        }
    }

    /// <summary>Waits for the process to exit, and for its output to be collected, up to a timeout.</summary>
    public bool WaitForExit(int timeout)
    {
        bool exited = Process.WaitForExit(timeout);
        if (exited)
        {
            // The process has gone, but its asynchronous output handlers may not have run yet and callers of
            // this overload read Data. Wait on the collector rather than calling the parameterless
            // WaitForExit() the documentation suggests, whose wait for end of file is unbounded (see above).
            Collector.WaitForCompletion(timeout);
        }
        return exited;
    }

    /// <summary>Waits for the redirected output streams to end, up to a timeout, so that <see cref="Data"/> is complete.</summary>
    /// <remarks>
    /// Bounded for the reason <see cref="WaitForExit()"/> gives: a process that left children holding the
    /// write end keeps the streams open past its own exit. Call it after the process has exited, when what is
    /// left is the tail the handlers have not delivered yet.
    /// </remarks>
    public bool WaitForOutput(int timeout) => Collector.WaitForCompletion(timeout);

    private void OutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            Logger.LogDebug("{Data}{NewLine}", e.Data, NewLine);
        }
    }

    private void ErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            Logger.LogError("{Data}{NewLine}", e.Data, NewLine);
        }
    }

    /// <summary>Terminates the process and everything it started, if it is still running.</summary>
    public void Kill()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited in the meantime.
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        ExitSource.TrySetResult(true);
        Exited?.Invoke();
        Logger.LogDebug(
            "Process {Id} exited with code {ExitCode}{NewLine}",
            Process.Id,
            Process.ExitCode,
            NewLine);
    }

    public void Dispose()
    {
        Process.OutputDataReceived -= OutputDataReceived;
        Process.ErrorDataReceived -= ErrorDataReceived;
        Process.Exited -= OnExit;
        Process.Close();
        Collector.Dispose();
    }

    private static string NewLine => System.Environment.NewLine;

    /// <summary>How long a single wait on the process handle lasts before it is repeated.</summary>
    private const int WaitInterval = 60_000;
}
