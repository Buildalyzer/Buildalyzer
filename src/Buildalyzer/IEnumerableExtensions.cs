namespace Buildalyzer;

internal static class IEnumerableExtensions
{
    [Pure]
    internal static IEnumerable<DictionaryEntry> ToDictionaryEntries(this IEnumerable? enumerable)
        => enumerable?
            .Cast<object>()
            .Select(AsDictionaryEntry)
        ?? [];

    private static DictionaryEntry AsDictionaryEntry(object? obj) => obj switch
    {
        DictionaryEntry entry => entry,
        KeyValuePair<string, object?> kvp => new(kvp.Key, kvp.Value),
        KeyValuePair<string, string?> kvp => new(kvp.Key, kvp.Value),
        KeyValuePair<object, object?> kvp => new(kvp.Key, kvp.Value),
        _ => throw new InvalidOperationException($"Could not determine enumerable dictionary entry type for {obj?.GetType()}."),
    };
}
