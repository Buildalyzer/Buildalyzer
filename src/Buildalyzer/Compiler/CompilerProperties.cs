namespace Buildalyzer;

[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(Diagnostics.CollectionDebugView<CompilerProperty>))]
#pragma warning disable CA1710 // Identifiers should have correct suffix

// CompilerProperties describes the type the best.
public sealed class CompilerProperties : IReadOnlyCollection<CompilerProperty>
#pragma warning restore CA1710 // Identifiers should have correct suffix
{
    /// <summary>Gets empty <see cref="CompilerProperties"/>.</summary>
    public static readonly CompilerProperties Empty = new();

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

    private CompilerProperties()
    {
    }

    public CompilerProperties(IEnumerable<KeyValuePair<string, object>> values)
    {
        _values = values.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _values.Count;

    [Pure]
    public CompilerProperty? TryGet(string key)
        => _values.TryGetValue(key, out object? value)
            ? new CompilerProperty(key, value)
            : null;

    [Pure]
    public IEnumerator<CompilerProperty> GetEnumerator()
    {
        return Select().GetEnumerator();

        IEnumerable<CompilerProperty> Select() => _values.Select(kvp => new CompilerProperty(kvp.Key, kvp.Value));
    }

    [Pure]
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    [Pure]
    internal static CompilerProperties FromPipeProperties(IReadOnlyList<XenoAtom.MsBuildPipeLogger.PipeProperty> properties)
    {
        CompilerProperties props = new CompilerProperties();
        foreach (var property in properties)
        {
            if (property.Name is { Length: > 0 } key && property.Value is { } value)
            {
                props._values[key] = value;
            }
        }

        return props;
    }
}
