using Buildalyzer.IO;
using Buildalyzer.Logging;

namespace Buildalyzer;

/// <summary>A single build argument.</summary>
public sealed class BuildArgument
{
    private const string Dash = "-";
    private const string Slash = "/";
    private const string Colon = ":";

    /// <summary>/restore.</summary>
    public static readonly BuildArgument Restore = new(Slash, "restore");

    /// <summary>/noAutoResponse.</summary>
    public static readonly BuildArgument NoAutoResponse = new(Slash, "noAutoResponse");

    /// <summary>/noconsolelogger.</summary>
    public static readonly BuildArgument NoConsoleLogger = new(Slash, "noconsolelogger");

    /// <summary>/l.</summary>
    [Pure]
    public static BuildArgument Logger(bool isDotNet, LoggerConfiguration config) => new(
            isDotNet ? Dash : Slash,
            "l",
            $":{Guard.NotNull(config).LoggerType.Name},{EscapedString(config.LoggerPath.ToString())};{config.ClientHandle};{config.LogEverything}");

    /// <summary>/p (or -property).</summary>
    [Pure]
    public static BuildArgument? Property(
        bool isDotNet,
        IReadOnlyCollection<BuildCommandProperty> properties)
        => properties is { Count: > 0 }
        ? new(
            isDotNet ? Dash : Slash,
            isDotNet ? "p" : "property",
            Colon,
            string.Join(';', properties.Select(kvp => $"{kvp.Key}={EscapedString(kvp.Value)}")))
        : null;

    /// <summary>/target.</summary>
    [Pure]
    public static BuildArgument? Target(IReadOnlyCollection<string> targets)
        => targets is { Count: > 0 }
        ? new(Slash, "target", Colon, string.Join(';', targets))
        : null;

    /// <summary>a prefix-less path.</summary>
    [Pure]
    public static BuildArgument Path(in IOPath project) => new(null, EscapedString(project.ToString()));

    private BuildArgument(string? prefix, string name, string? splitter = null, string? value = null)
    {
        Prefix = prefix;
        Name = name;
        Splitter = splitter;
        Value = value;
    }

    /// <summary>The prefix of the argument (like / or -).</summary>
    public string? Prefix { get; init; }

    /// <summary>The name of the argument.</summary>
    public string Name { get; init; }

    /// <summary>The splitter of the argument.</summary>
    public string? Splitter { get; init; }

    /// <summary>The value of the argument.</summary>
    public string? Value { get; init; }

    /// <inheritdoc />
    [Pure]
    public override string ToString() => $"{Prefix}{Name}{Splitter}{Value}";

    [Pure]
    private static string EscapedString(string argument)
    {
        // Escape inner quotes
        argument = argument.Replace("\"", "\\\"");

        // Also escape trailing slashes so they don't escape the closing quote
        if (argument.EndsWith('\\'))
        {
            argument = $"{argument}\\";
        }

        // Surround with quotes
        return $"\"{argument}\"".Replace(";", "%3B");
    }
}
