using System.Collections.Concurrent;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

namespace Buildalyzer;

[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(Diagnostics.CollectionDebugView<BuildEventArgs>))]
internal sealed class BuildEventArgsCollector : IReadOnlyCollection<BuildEventArgs>, IDisposable
{
    public BuildEventArgsCollector(EventArgsDispatcher server)
    {
        Server = server;
        Server.AnyEventRaised += EventRaised;
    }

    /// <inheritdoc />
    public int Count => Bag.Count;

    /// <summary>Indicates that no events has been collected.</summary>
    public bool IsEmpty => Count == 0;

    /// <inheritdoc />
    /// <remarks>
    /// Ordered by timestamp, as the bag does not ensure the chronical order itself.
    /// </remarks>
    [Pure]
    public IEnumerator<BuildEventArgs> GetEnumerator() => Bag.OrderBy(e => e.Timestamp).GetEnumerator();

    /// <inheritdoc />
    [Pure]
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void EventRaised(object? sender, BuildEventArgs e) => Bag.Add(e);

    private readonly EventArgsDispatcher Server;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly ConcurrentBag<BuildEventArgs> Bag = [];

    /// <inheritdoc />
    public void Dispose()
    {
        if (!Disposed)
        {
            Server.AnyEventRaised -= EventRaised;
            Bag.Clear();
            Disposed = true;
        }
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private bool Disposed;
}
