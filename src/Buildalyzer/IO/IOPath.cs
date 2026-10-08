using System.ComponentModel;
using System.IO;

namespace Buildalyzer.IO;

/// <summary>Represents an (IO) path.</summary>
/// <remarks>
/// Internal path-normalization helper: paths are accepted from and handed back to consumers
/// as <see cref="string"/>; <see cref="IOPath"/> only exists to give the internals a canonical
/// form with file-system-aware equality.
/// </remarks>
[TypeConverter(typeof(Conversion.IOPathTypeConverter))]
internal readonly struct IOPath : IEquatable<IOPath>, IFormattable
{
    /// <summary>Represents none/an empty path.</summary>
    public static readonly IOPath Empty;

    /// <inheritdoc cref="Path.DirectorySeparatorChar" />
    public static char DirectorySeparatorChar => Path.DirectorySeparatorChar;

    /// <summary>Returns true if the file system is case sensitive.</summary>
    public static readonly bool IsCaseSensitive = InitCaseSensitivity();

    /// <summary>Compares path strings the way the current file system does.</summary>
    /// <remarks>
    /// Ordinal on a case-sensitive file system, case-insensitive elsewhere. Key and de-duplicate
    /// paths with this rather than with <see cref="StringComparer.OrdinalIgnoreCase"/>: on Linux two
    /// paths that differ only by case are two different files and must not be conflated.
    /// </remarks>
    public static readonly StringComparer Comparer = IsCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <inheritdoc cref="Comparer" />
    public static readonly StringComparison Comparison = IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string _path;

    private IOPath(string path) => _path = path;

    /// <summary>Returns true if the path is not empty.</summary>
    public bool HasValue => _path is { Length: > 0 };

    /// <summary>Creates a <see cref="DirectoryInfo"/> based on the path.</summary>
    [Pure]
    public DirectoryInfo? Directory() => HasValue ? new(ToString()) : null;

    /// <summary>Creates a <see cref="FileInfo"/> based on the path.</summary>
    [Pure]
    public FileInfo? File() => HasValue ? new(ToString()) : null;

    /// <summary>
    /// Resolves the path to its rooted, canonical form (see <see cref="Path.GetFullPath(string)"/>):
    /// relative paths are made absolute against the current directory and <c>.</c>/<c>..</c> segments
    /// are collapsed. An empty path stays empty.
    /// </summary>
    [Pure]
    public IOPath Root() => HasValue ? Parse(Path.GetFullPath(ToString())) : Empty;

    /// <summary>Creates a new path.</summary>
    [Pure]
    public IOPath Combine(params string[] paths)
        => _path is null
            ? Parse(Path.Combine(paths))
            : Parse(Path.Combine(_path, Path.Combine(paths)));

    /// <inheritdoc />
    [Pure]
    public override bool Equals([NotNullWhen(true)] object? obj)
        => obj is IOPath other && Equals(other);

    /// <inheritdoc />
    [Pure]
    public bool Equals(IOPath other) => Equals(other, IsCaseSensitive);

    /// <inheritdoc />
    [Pure]
    public bool Equals(IOPath other, bool caseSensitive)
        => caseSensitive
        ? _path == other._path
        : _path.IsMatch(other._path);

    /// <inheritdoc />
    [Pure]
    public override int GetHashCode()
        => IsCaseSensitive
            ? _path?.GetHashCode() ?? 0
            : _path?.ToUpperInvariant().GetHashCode() ?? 0;

    /// <inheritdoc />
    [Pure]
    public override string ToString() => ToString(null, null);

    /// <inheritdoc />
    [Pure]
    public string ToString(string? format, IFormatProvider? formatProvider) => format switch
    {
        "/" => _path ?? string.Empty,
        "\\" => (_path ?? string.Empty).Replace('/', '\\'),
        null => (_path ?? string.Empty).Replace('/', DirectorySeparatorChar),
        _ => throw new FormatException($"The format '{format}' is a not supported directory separator char."),
    };

    [Pure]
    public static IOPath Parse(string? s)
        => s?.Trim() is { Length: > 0 } p
        ? new(p.Replace('\\', '/'))
        : Empty;

    /// <summary>Probes whether the file system the app lives on is case sensitive.</summary>
    /// <remarks>
    /// Asks whether a file that is known to exist is still found when its path is upper-cased.
    /// <see cref="System.Reflection.Assembly.Location" /> is empty in a single-file or Native AOT
    /// app, so the running executable is probed there instead.
    /// </remarks>
    [Pure]
    [UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000:Avoid accessing Assembly file path when publishing as a single file",
        Justification = "The empty location of a single-file app is handled by falling back to the executable.")]
    private static bool InitCaseSensitivity()
    {
        var probe = typeof(IOPath).Assembly.Location is { Length: > 0 } assembly
            ? assembly
            : System.Environment.ProcessPath;

        return probe is { Length: > 0 } file
            ? !new FileInfo(file.ToUpperInvariant()).Exists
            : !new DirectoryInfo(AppContext.BaseDirectory.ToUpperInvariant()).Exists;
    }
}
