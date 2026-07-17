using XenoAtom.MsBuildPipeLogger;

namespace Buildalyzer.Logging;

/// <summary>
/// A minimal <see cref="IProjectItem"/> backed by a <see cref="PipeItem"/>, used to surface items delivered
/// over the (MSBuild-free) pipe through Buildalyzer's result model without needing any MSBuild types.
/// </summary>
internal sealed class PipeProjectItem : IProjectItem
{
    public PipeProjectItem(PipeItem item)
    {
        ItemSpec = item.EvaluatedInclude;
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var metadatum in item.Metadata)
        {
            metadata[metadatum.Name] = metadatum.Value ?? string.Empty;
        }

        Metadata = metadata;
    }

    public string ItemSpec { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }
}
