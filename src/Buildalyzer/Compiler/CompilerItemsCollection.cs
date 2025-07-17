using Microsoft.Build.Framework;

namespace Buildalyzer;

[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(Diagnostics.CollectionDebugView<CompilerItems>))]
public sealed class CompilerItemsCollection : IReadOnlyCollection<CompilerItems>
{
    /// <summary>Gets an empty <see cref="CompilerItemsCollection"/>.</summary>
    public static readonly CompilerItemsCollection Empty = new();

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly Dictionary<string, IReadOnlyCollection<ITaskItem>> _values = new Dictionary<string, IReadOnlyCollection<ITaskItem>>(StringComparer.OrdinalIgnoreCase);

    private CompilerItemsCollection()
    {
    }

    public CompilerItemsCollection(IEnumerable<KeyValuePair<string, IReadOnlyCollection<ITaskItem>>> values)
    {
        _values = values.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _values.Count;

    [Pure]
    public CompilerItems? TryGet(string key)
        => _values.TryGetValue(key, out IReadOnlyCollection<ITaskItem>? values)
            ? new CompilerItems(key, values)
            : null;

    [Pure]
    public IEnumerator<CompilerItems> GetEnumerator()
    {
        return Select().GetEnumerator();

        IEnumerable<CompilerItems> Select() => _values.Select(kvp => new CompilerItems(kvp.Key, kvp.Value));
    }

    [Pure]
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    [Pure]
    internal static CompilerItemsCollection FromDictionaryEntries(IEnumerable? properties)
    {
        CompilerItemsCollection props = new CompilerItemsCollection();

        foreach (DictionaryEntry entry in properties.ToDictionaryEntries())
        {
            if (entry.Key?.ToString() is { Length: > 0 } key && entry.Value is ITaskItem task)
            {
                if (!props._values.TryGetValue(key, out IReadOnlyCollection<ITaskItem>? values)
                    || values is not List<ITaskItem> editable)
                {
                    editable = new List<ITaskItem>();
                    props._values[key] = editable;
                }
                editable.Add(task);
            }
        }
        return props;
    }
}
