
using System.ComponentModel;
using System.IO;

namespace Buildalyzer.IO;

/// <summary>Represents an (IO) path.</summary>
[TypeConverter(typeof(Conversion.IOPathTypeConverter))]
public readonly struct IOPath : IEquatable<IOPath>, IFormattable
{
    /// <summary>Represents none/an empty path.</summary>
    public static readonly IOPath Empty;

    /// <inheritdoc cref="Path.DirectorySeparatorChar" />
    public static char DirectorySeparatorChar => Path.DirectorySeparatorChar;

    /// <summary>Returns true if the file system is case sensitive.</summary>
    public static readonly bool IsCaseSensitive = InitCaseSensitivity();

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string _path;

    private IOPath(string path) => _path = path;

    /// <summary>Indicates that the path has been set.</summary>
    public bool HasValue => _path is not null;

    /// <summary>Creates a <see cref="DirectoryInfo"/> based on the path.</summary>
    [Pure]
    public DirectoryInfo? Directory() => HasValue ? new(ToString()) : null;

    /// <summary>Creates a <see cref="FileInfo"/> based on the path.</summary>
    [Pure]
    public FileInfo? File() => HasValue ? new(ToString()) : null;

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

    [Pure]
    private static bool InitCaseSensitivity()
        => !new FileInfo(typeof(IOPath).Assembly.Location.ToUpperInvariant()).Exists;
}
