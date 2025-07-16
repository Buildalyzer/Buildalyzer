using Microsoft.Build.Framework;

namespace Buildalyzer.Handling;

public class BuildEventHandlerContext
{
    public ImmutableArray<BuildEventArgs> Events { get; } = [];

    public Dictionary<int, ProjectAnalysis> Projects { get; } = [];

    public List<BuildEventArgs> Skipped { get; } = [];
}
