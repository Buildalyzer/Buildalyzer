#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks

namespace System.Threading.Tasks;

/// <summary>Etensions on <see cref="Task"/>.</summary>
internal static class BuildalyzerTaskExtensions
{
    /// <summary>Runs a task synchronously.</summary>
    [Pure]
    public static TResult Sync<TResult>(this Task<TResult> task) => TaskFactory
        .StartNew(() => task)
        .Unwrap()
        .GetAwaiter()
        .GetResult();

    private static readonly TaskFactory TaskFactory = new(
        CancellationToken.None,
        TaskCreationOptions.None,
        TaskContinuationOptions.None,
        TaskScheduler.Default);
}
