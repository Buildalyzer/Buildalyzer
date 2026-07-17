using XenoAtom.MsBuildPipeLogger;

namespace Buildalyzer;

public interface IAnalyzerResults : IReadOnlyCollection<IAnalyzerResult>
{
    /// <summary>The collected <see cref="PipeBuildEventArgs"/> during the analysis.</summary>
    ImmutableArray<PipeBuildEventArgs> BuildEventArguments { get; }

    IAnalyzerResult this[string targetFramework] { get; }

    bool OverallSuccess { get; }

    IEnumerable<IAnalyzerResult> Results { get; }

    IEnumerable<string> TargetFrameworks { get; }

    bool ContainsTargetFramework(string targetFramework);

    bool TryGetTargetFramework(string targetFramework, out IAnalyzerResult result);
}
