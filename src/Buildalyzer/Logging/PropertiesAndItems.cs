namespace Buildalyzer.Logging;

internal sealed class PropertiesAndItems
{
    public required CompilerProperties Properties { get; init; }

    public required CompilerItemsCollection Items { get; init; }
}
