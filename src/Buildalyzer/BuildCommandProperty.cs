namespace Buildalyzer;

/// <summary>Represents a build command property and its value.</summary>
public readonly struct BuildCommandProperty(string key, string value)
{
    /// <summary>Gets the key of the build command property.</summary>
    public readonly string Key = key;

    /// <summary>Gets the value of the build command property.</summary>
    public readonly string Value = value;

    /// <inheritdoc />
    public override string ToString() => $"{Key}={Value}";
}
