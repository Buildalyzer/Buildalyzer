using System.Collections.Concurrent;
using XenoAtom.MsBuildPipeLogger;

namespace Buildalyzer;

[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(Diagnostics.CollectionDebugView<PipeBuildEventArgs>))]
internal sealed class BuildEventArgsCollector : IReadOnlyCollection<PipeBuildEventArgs>, IDisposable
{
    public BuildEventArgsCollector(PipeEventDispatcher server)
    {
        Server = server;
        Server.AnyEventRaised += EventRaised;
    }

    /// <inheritdoc />
    public int Count => Bag.Count;

    /// <summary>Indicates that no events has been collected.</summary>
    public bool IsEmpty => Count == 0;

    /// <inheritdoc />
    public IEnumerator<PipeBuildEventArgs> GetEnumerator() => Bag.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void EventRaised(PipeBuildEventArgs e) => Bag.Add(e);

    private readonly PipeEventDispatcher Server;

    private readonly ConcurrentBag<PipeBuildEventArgs> Bag = [];

    public void Dispose()
    {
        if (!Disposed)
        {
            Server.AnyEventRaised -= EventRaised;
            Disposed = true;
        }
    }

    private bool Disposed;
}
