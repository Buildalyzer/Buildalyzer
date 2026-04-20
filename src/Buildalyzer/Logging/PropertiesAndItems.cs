using Microsoft.Build.Framework;

namespace Buildalyzer.Logging;

internal sealed class PropertiesAndItems
{
    public required CompilerProperties Properties { get; init; }

    public required CompilerItemsCollection Items { get; init; }

    [Pure]
    public static PropertiesAndItems? TryCreate(ProjectStartedEventArgs e, IReadOnlyDictionary<int, PropertiesAndItems> results) => e switch
    {
        { Properties.HasAny: true } => New(e),
        { BuildEventContext.EvaluationId: { } id } when results.TryGetValue(id, out var existing) => existing,
        _ => null,
    };

    [Pure]
    private static PropertiesAndItems New(ProjectStartedEventArgs e) => new()
    {
        Properties = CompilerProperties.FromDictionaryEntries(e.Properties),
        Items = CompilerItemsCollection.FromDictionaryEntries(e.Items),
    };
}
