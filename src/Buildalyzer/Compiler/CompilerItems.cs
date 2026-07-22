#pragma warning disable CA1710 // Identifiers should have correct suffix
// CompilerItems describes the type the best.

namespace Buildalyzer;

/// <summary>Represents compiler item key and its values.</summary>
[DebuggerDisplay("{Key}, Count = {Count}")]
[DebuggerTypeProxy(typeof(Diagnostics.CollectionDebugView<IProjectItem>))]
public readonly struct CompilerItems(string key, IReadOnlyCollection<IProjectItem> values) : IReadOnlyCollection<IProjectItem>
{
    /// <summary>Gets the compiler item key.</summary>
    public readonly string Key = key;

    /// <summary>Gets the compiler item values.</summary>
    public IReadOnlyCollection<IProjectItem> Values { get => field ?? []; } = values;

    /// <inheritdoc />
    public int Count => Values.Count;

    /// <inheritdoc />
    public IEnumerator<IProjectItem> GetEnumerator() => Values.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
