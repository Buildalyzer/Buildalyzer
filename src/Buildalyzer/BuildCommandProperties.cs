using Buildalyzer.Environment;

namespace Buildalyzer;

/// <summary>Factory to create <see cref="BuildCommandProperty"/>s.</summary>
internal static class BuildCommandProperties
{
    /// <summary>Creates <see cref="BuildCommandProperty"/>s.</summary>
    [Pure]
    public static ImmutableArray<BuildCommandProperty> Create(
        string? targetFramework,
        params IEnumerable<KeyValuePair<string, string?>>[] properties)
    {
        Guard.NotNull(properties);

        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in properties.SelectMany(p => p))
        {
            if (kvp.Value is null)
            {
                props.Remove(kvp.Key);
            }
            else
            {
                props[kvp.Key] = kvp.Value;
            }
        }

        if (targetFramework is { Length: > 0 })
        {
            props[MsBuildProperties.TargetFramework] = targetFramework;
        }

        return [.. props.Select(kvp => new BuildCommandProperty(kvp.Key, kvp.Value))];
    }
}
