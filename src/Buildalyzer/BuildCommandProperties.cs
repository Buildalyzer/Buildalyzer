using Buildalyzer.Environment;
using Buildalyzer.IO;

namespace Buildalyzer;

/// <summary>Factory to create <see cref="BuildCommandProperty"/>s.</summary>
public static class BuildCommandProperties
{
    /// <summary>Creates <see cref="BuildCommandProperty"/>s.</summary>
    [Pure]
    public static ImmutableArray<BuildCommandProperty> Create(
        in IOPath projectFile,
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

        if (targetFramework is not null)
        {
            props[MsBuildProperties.TargetFramework] = targetFramework;
        }

        if (props.ContainsKey(MsBuildProperties.SkipCompilerExecution)
            && projectFile.File() is { } file && file.Extension.IsMatch(".fsproj"))
        {
            // We can't skip the compiler for design-time builds in F# (it causes strange errors regarding file copying)
            props.Remove(MsBuildProperties.SkipCompilerExecution);
        }

        return [.. props.Select(kvp => new BuildCommandProperty(kvp.Key, kvp.Value!))];
    }
}
