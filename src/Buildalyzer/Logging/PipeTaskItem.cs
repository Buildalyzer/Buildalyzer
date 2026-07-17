using System.Collections;
using Microsoft.Build.Framework;
using XenoAtom.MsBuildPipeLogger;

namespace Buildalyzer.Logging;

/// <summary>
/// A minimal <see cref="ITaskItem"/> backed by a <see cref="PipeItem"/>, used to surface items delivered
/// over the (MSBuild-free) pipe through Buildalyzer's <see cref="ITaskItem"/>-based result model without
/// needing the MSBuild engine.
/// </summary>
internal sealed class PipeTaskItem : ITaskItem
{
    private readonly Dictionary<string, string> _metadata = new(StringComparer.OrdinalIgnoreCase);

    public PipeTaskItem(PipeItem item)
    {
        ItemSpec = item.EvaluatedInclude;
        foreach (var metadatum in item.Metadata)
        {
            _metadata[metadatum.Name] = metadatum.Value ?? string.Empty;
        }
    }

    public string ItemSpec { get; set; }

    public int MetadataCount => _metadata.Count;

    public ICollection MetadataNames => _metadata.Keys;

    public string GetMetadata(string metadataName) => _metadata.TryGetValue(metadataName, out var value) ? value : string.Empty;

    public void SetMetadata(string metadataName, string metadataValue) => _metadata[metadataName] = metadataValue;

    public void RemoveMetadata(string metadataName) => _metadata.Remove(metadataName);

    public void CopyMetadataTo(ITaskItem destinationItem)
    {
        foreach (var metadatum in _metadata)
        {
            destinationItem.SetMetadata(metadatum.Key, metadatum.Value);
        }
    }

    public IDictionary CloneCustomMetadata() => new Dictionary<string, string>(_metadata, StringComparer.OrdinalIgnoreCase);
}
