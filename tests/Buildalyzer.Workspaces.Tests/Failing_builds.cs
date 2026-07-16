using Buildalyzer.TestTools;
using Buildalyzer.Workspaces;

namespace Failing_builds;

public class Analyzer_Build
{
    [Test]
    public void Detects_failing_build()
    {
        using var ctx = Context.ForProject(@"BuildWithError\BuildWithError.csproj");

        ctx.Invoking(_ => ctx.Analyzer.GetWorkspace()).Should().NotThrow();
    }

    [Test(Description = "When MSBuild fails before Csc runs, the workspace should still contain documents recovered from evaluation-time Compile items. https://github.com/Buildalyzer/Buildalyzer/issues/341")]
    public void Recovers_documents_when_build_fails_before_Csc()
    {
        using var ctx = Context.ForProject(@"FailBeforeCompile\FailBeforeCompile.csproj");

        var results = ctx.Analyzer.Build();
        var result = results.First();

        // The build must have failed before the compiler ran.
        result.Succeeded.Should().BeFalse();
        result.SourceFiles.Should().BeEmpty(ctx.Log.ToString());

        using var workspace = result.GetWorkspace();
        var project = workspace.CurrentSolution.Projects.Single();

        project.Documents.Select(d => d.Name).Should().Contain("Class1.cs", ctx.Log.ToString());
    }

    [Test(Description = "Preprocessor symbols should be recovered from DefineConstants when Csc never ran. https://github.com/Buildalyzer/Buildalyzer/issues/341")]
    public void Recovers_preprocessor_symbols_when_build_fails_before_Csc()
    {
        using var ctx = Context.ForProject(@"FailBeforeCompile\FailBeforeCompile.csproj");

        var result = ctx.Analyzer.Build().First();

        using var workspace = result.GetWorkspace();
        var parseOptions = workspace.CurrentSolution.Projects.Single().ParseOptions;

        parseOptions.Should().BeOfType<Microsoft.CodeAnalysis.CSharp.CSharpParseOptions>()
            .Which.PreprocessorSymbolNames.Should().Contain("CUSTOM_CONSTANT", ctx.Log.ToString());
    }
}
