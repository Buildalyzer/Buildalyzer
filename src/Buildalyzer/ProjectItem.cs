namespace Buildalyzer;

public class ProjectItem : IProjectItem
{
    public string ItemSpec { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    internal ProjectItem(IProjectItem item)
    {
        ItemSpec = item.ItemSpec;
        Metadata = item.Metadata;
    }
}
