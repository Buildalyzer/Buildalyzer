#pragma warning disable CA1710 // Identifiers should have correct suffix
// CompilerItems describes the type the best.

using Microsoft.Build.Framework;

namespace Buildalyzer;

/// <summary>Represents compiler item key and its values.</summary>
[DebuggerDisplay("{Key}, Count = {Count}")]
[DebuggerTypeProxy(typeof(Diagnostics.CollectionDebugView<ITaskItem>))]
public readonly struct CompilerItems(string key, IReadOnlyCollection<ITaskItem> values) : IReadOnlyCollection<ITaskItem>
{
    private readonly IReadOnlyCollection<ITaskItem> _values = values;

    /// <summary>Ghets the compiler item key.</summary>
    public readonly string Key = key;

    /// <summary>Gets the compiler item values.</summary>
    public IReadOnlyCollection<ITaskItem> Values => _values ?? [];

    /// <inheritdoc />
    public int Count => Values.Count;

    /// <inheritdoc />
    public IEnumerator<ITaskItem> GetEnumerator() => Values.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
