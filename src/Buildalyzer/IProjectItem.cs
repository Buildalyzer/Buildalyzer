namespace Buildalyzer;

public interface IProjectItem
{
    string ItemSpec { get; }
    IReadOnlyDictionary<string, string> Metadata { get; }
}
