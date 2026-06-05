using Microsoft.Build.Framework;

namespace Buildalyzer;

public class ProjectItem : IProjectItem
{
    public string ItemSpec { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    internal ProjectItem(ITaskItem taskItem)
    {
        ItemSpec = taskItem.ItemSpec;
        Metadata = taskItem.MetadataNames.Cast<string>().ToDictionary(x => x, x => taskItem.GetMetadata(x));
    }
}
