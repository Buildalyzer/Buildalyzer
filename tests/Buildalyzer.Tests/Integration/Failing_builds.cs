using Buildalyzer.Environment;
using Buildalyzer.TestTools;
using FluentAssertions;

namespace Failing_builds;

public class Analyzer_Build
{
    [Test]
    public void Detects_failing_build_on_single_target()
    {
        using var ctx = Context.ForProject(@"BuildWithError\BuildWithError.csproj");
        var results = ctx.Analyzer.Build(new EnvironmentOptions());

        Console.WriteLine(ctx.Log);

        results.OverallSuccess.Should().BeFalse();
        results.Should().AllSatisfy(r => r.Succeeded.Should().BeFalse());
    }

    [Test]
    public void Detects_failing_build_on_multi_targets()
    {
        using var ctx = Context.ForProject(@"BuildWithError\BuildWithError.MultiTarget.csproj");
        var results = ctx.Analyzer.Build(new EnvironmentOptions());

        Console.WriteLine(ctx.Log);

        results.OverallSuccess.Should().BeFalse();
        results.Should().AllSatisfy(r => r.Succeeded.Should().BeFalse());
    }
}
