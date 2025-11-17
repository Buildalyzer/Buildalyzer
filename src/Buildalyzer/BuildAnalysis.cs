using Microsoft.Build.Framework;

namespace Buildalyzer;

[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(Diagnostics.CollectionDebugView<ProjectAnalysis>))]
public sealed class BuildAnalysis(ImmutableArray<ProjectAnalysis> projects) :
    IReadOnlyCollection<ProjectAnalysis>,
    IAnalyzerResults
{
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public readonly ImmutableArray<ProjectAnalysis> Projects = projects;

    IAnalyzerResult IAnalyzerResults.this[string targetFramework] => throw new NotImplementedException();

    /// <inheritdoc />
    public int Count => Projects.Length;

    ImmutableArray<BuildEventArgs> IAnalyzerResults.BuildEventArguments =>
    [
        .. Projects.SelectMany(p => p.Events)
    ];

    bool IAnalyzerResults.OverallSuccess => throw new NotImplementedException();

    /// <inheritdoc />
    IEnumerable<IAnalyzerResult> IAnalyzerResults.Results
        => Projects.Where(p => p.Command is { } && p.TargetFramework is { Length: > 0 });

    IEnumerable<string> IAnalyzerResults.TargetFrameworks => throw new NotImplementedException();

    int IReadOnlyCollection<IAnalyzerResult>.Count => throw new NotImplementedException();

    /// <inheritdoc />
    [Pure]
    public IEnumerator<ProjectAnalysis> GetEnumerator() => Projects.AsEnumerable().GetEnumerator();

    bool IAnalyzerResults.ContainsTargetFramework(string targetFramework)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    [Pure]
    IEnumerator IEnumerable.GetEnumerator()=> GetEnumerator();

    IEnumerator<IAnalyzerResult> IEnumerable<IAnalyzerResult>.GetEnumerator()
    {
        throw new NotImplementedException();
    }

    bool IAnalyzerResults.TryGetTargetFramework(string targetFramework, out IAnalyzerResult result)
    {
        throw new NotImplementedException();
    }
}
