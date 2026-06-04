using Microsoft.Build.Framework;

namespace Buildalyzer;

public interface IAnalyzerResults : IReadOnlyCollection<IAnalyzerResult>
{
    /// <summary>The collected <see cref="BuildEventArgs"/> during the analysis.</summary>
    ImmutableArray<BuildEventArgs> BuildEventArguments { get; }

    IAnalyzerResult this[string targetFramework] { get; }

    bool OverallSuccess { get; }

    IEnumerable<IAnalyzerResult> Results { get; }

    IEnumerable<string> TargetFrameworks { get; }

    bool ContainsTargetFramework(string targetFramework);

    bool TryGetTargetFramework(string targetFramework, out IAnalyzerResult result);
}
