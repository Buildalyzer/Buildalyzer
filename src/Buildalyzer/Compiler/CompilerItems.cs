
using Microsoft.Build.Framework;

namespace Buildalyzer;

[DebuggerDisplay("{Key}, Count = {Count}")]
[DebuggerTypeProxy(typeof(Diagnostics.CollectionDebugView<ITaskItem>))]
#pragma warning disable CA1710 // Identifiers should have correct suffix

// CompilerItems describes the type the best.
public readonly struct CompilerItems : IReadOnlyCollection<ITaskItem>
#pragma warning restore CA1710 // Identifiers should have correct suffix
{
    private readonly IReadOnlyCollection<ITaskItem> _values;

    public CompilerItems(string key, IReadOnlyCollection<ITaskItem> values)
    {
        Key = key;
        _values = values;
    }

    public readonly string Key;

    public IReadOnlyCollection<ITaskItem> Values => _values ?? [];

    public int Count => Values.Count;

    public IEnumerator<ITaskItem> GetEnumerator() => Values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
