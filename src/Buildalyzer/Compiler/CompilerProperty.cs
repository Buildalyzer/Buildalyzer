namespace Buildalyzer;

/// <summary>Represents a compiler property and its value.</summary>
public readonly struct CompilerProperty(string key, object value)
{
    /// <summary>Gets the key of the compiler property.</summary>
    public readonly string Key = key;

    /// <summary>Gets the value of the compiler property.</summary>
    public readonly object Value = value;

    /// <summary>Gets the string value of the compiler property.</summary>
    public string StringValue => Value?.ToString() ?? string.Empty;

    /// <summary>Gets the type of the value of the compiler property.</summary>
    public Type? ValueType => Value?.GetType();

    /// <inheritdoc />
    public override string ToString() => $"{Key}: {Value}";
}
