using Buildalyzer.Environment;
using Buildalyzer.Handling;
using Buildalyzer.TestTools;

namespace Failing_builds;

public class Analyzer_Build
{
    [Test]
    public void Detects_failing_build_on_single_target()
    {
        using var ctx = Context.ForProject(@"BuildWithError\BuildWithError.csproj");
        var results = ctx.Analyzer.Build(new EnvironmentOptions() { DesignTime = false });

        var summeries = BuildEventHandlers.Default.Handle(results.BuildEventArguments);
        var summary = summeries.Single(s => s.Command!.SourceFiles.Any());

        summary.Errors.Should().HaveCount(3);

        results.OverallSuccess.Should().BeFalse();
        results.Should().AllSatisfy(r => r.Succeeded.Should().BeFalse());
    }

    [Test]
    public void Detects_failing_build_on_multi_targets()
    {
        using var ctx = Context.ForProject(@"BuildWithError\BuildWithError.MultiTarget.csproj");
        var results = ctx.Analyzer.Build(new EnvironmentOptions() { DesignTime = false });

        results.OverallSuccess.Should().BeFalse();
        results.Should().AllSatisfy(r => r.Succeeded.Should().BeFalse());
    }
}
