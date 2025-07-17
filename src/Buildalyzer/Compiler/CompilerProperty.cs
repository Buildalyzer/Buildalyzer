
namespace Buildalyzer;

public readonly struct CompilerProperty
{
    public CompilerProperty(string key, object value)
    {
        Key = key;
        Value = value;
    }

    public readonly string Key;

    public readonly object Value;

    public string StringValue => Value?.ToString() ?? string.Empty;

    public Type? ValueType => Value?.GetType();

    public override string ToString() => $"{Key}: {Value}";
}
