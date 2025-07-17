using Buildalyzer.TestTools;
using Buildalyzer.Workspaces;

namespace Failing_builds;

public class Analyzer_Build
{
    [Test]
    public void Detects_failing_build()
    {
        using var ctx = Context.ForProject(@"BuildWithError\BuildWithError.csproj");

        ctx.Invoking(c => ctx.Analyzer.GetWorkspace()).Should().NotThrow();
    }
}
