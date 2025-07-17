namespace Buildalyzer.Environment;

/// <summary>Information about the .NET environment.</summary>
/// <remarks>
/// Retrieved via `dotnet --info`.
/// </remarks>
public sealed class DotNetInfo
{
    /// <summary>The version of the SDK.</summary>
    public Version? SdkVersion { get; init; }

    /// <summary>The name of the operating system.</summary>
    public string? OSName { get; init; }

    /// <summary>The platform of the operating system.</summary>
    public string? OSPlatform { get; init; }

    /// <summary>The version of the operating system.</summary>
    public Version? OSVersion { get; init; }

    /// <summary>The RID of the operating system.</summary>
    public string? RID { get; init; }

    /// <summary>The base path to the .NET environment.</summary>
    public string? BasePath { get; init; }

    /// <summary>The location of the global.json.</summary>
    public string? GlobalJson { get; init; }

    /// <summary>The installed SDK's.</summary>
    public ImmutableDictionary<string, string> SDKs { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>The installed Runtimes.</summary>
    public ImmutableDictionary<string, string> Runtimes { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>Parses the input.</summary>
    [Pure]
    public static DotNetInfo Parse(string? s)
        => Parse(s?.Split([System.Environment.NewLine], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []);

    /// <summary>Parses the input.</summary>
    [Pure]
    public static DotNetInfo Parse(IEnumerable<string>? lines)
        => DotNetInfoParser.Parse(lines ?? []);
}
