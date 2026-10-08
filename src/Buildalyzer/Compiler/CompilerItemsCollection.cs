namespace Buildalyzer;

[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(Diagnostics.CollectionDebugView<CompilerItems>))]
public sealed class CompilerItemsCollection : IReadOnlyCollection<CompilerItems>
{
    /// <summary>Gets an empty <see cref="CompilerItemsCollection"/>.</summary>
    public static readonly CompilerItemsCollection Empty = new();

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly Dictionary<string, IReadOnlyCollection<IProjectItem>> _values = new(StringComparer.OrdinalIgnoreCase);

    private CompilerItemsCollection()
    {
    }

    public CompilerItemsCollection(IEnumerable<KeyValuePair<string, IReadOnlyCollection<IProjectItem>>> values)
    {
        _values = values.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _values.Count;

    [Pure]
    public CompilerItems? TryGet(string key)
        => _values.TryGetValue(key, out IReadOnlyCollection<IProjectItem>? values)
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
    internal static CompilerItemsCollection FromPipeItems(IReadOnlyList<XenoAtom.MsBuildPipeLogger.PipeItem> items)
    {
        CompilerItemsCollection collection = new CompilerItemsCollection();
        foreach (var item in items)
        {
            if (item.ItemType is { Length: > 0 } key)
            {
                if (!collection._values.TryGetValue(key, out IReadOnlyCollection<IProjectItem>? values)
                    || values is not List<IProjectItem> editable)
                {
                    editable = [];
                    collection._values[key] = editable;
                }

                editable.Add(new Logging.PipeProjectItem(item));
            }
        }

        return collection;
    }
}
