using Buildalyzer;
using Buildalyzer.Logging;

namespace ProjectAnalyzer_specs;

public class Evaluated_target_frameworks
{
    // A framework listed twice (a props file composing "$(TargetFrameworks);net8.0" over a project that
    // already lists net8.0, say) builds once, as MSBuild's own _ComputeTargetFrameworkItems has it; two
    // concurrent builds pinned to the same framework would write to the same obj directory.
    [Test]
    public void are_distinct()
    {
        var result = new AnalyzerResult(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "project.csproj"), new AnalyzerManager(), null);
        result.ProcessProject(new PropertiesAndItems
        {
            Properties = new CompilerProperties([new("TargetFrameworks", "net8.0; net8.0;NET9.0;net9.0;")]),
            Items = CompilerItemsCollection.Empty,
        });
        AnalyzerResults results = [];
        results.Add([result], overallSuccess: true);

        ProjectAnalyzer.EvaluatedTargetFrameworks(results).Should().Equal("net8.0", "NET9.0");
    }
}

public class Suffixed_binary_log_paths
{
    [TestCase("project.binlog", "net8.0", "project.net8.0.binlog")]
    [TestCase(@"C:\logs\project.binlog", "net8.0", @"C:\logs\project.net8.0.binlog")]
    [TestCase("project.binlog", "restore", "project.restore.binlog")]
    [TestCase("my project.binlog", "net8.0", "my project.net8.0.binlog")]
    public void append_suffix_before_the_extension(string path, string suffix, string expected)
        => ProjectAnalyzer.AddSuffixToBinaryLogPath(path, suffix)
            .Should().Be(expected);

    [Test]
    public void leave_empty_paths_untouched()
        => ProjectAnalyzer.AddSuffixToBinaryLogPath(string.Empty, "net8.0")
            .Should().Be(string.Empty);
}

public class Binary_logger_paths
{
    // MSBuild splits /bl parameters on ';' and takes the file name verbatim (no %3B unescaping), so
    // these can't be passed through and are rejected up front rather than failing the build with MSB1029.
    [TestCase("logs/a;b.binlog")]
    [TestCase("logs/a\"b.binlog")]
    public void reject_characters_msbuild_cannot_receive(string path)
    {
        using var ctx = Buildalyzer.TestTools.Context.ForProject(@"SdkNetStandardProject\SdkNetStandardProject.csproj");
        var analyzer = ctx.Manager.GetProject(ctx.Location.FullName);

        analyzer.Invoking(a => a.AddBinaryLogger(path))
            .Should().Throw<ArgumentException>()
            .WithParameterName("binaryLogFilePath");
    }

    // MSBuild writes the log from its own working directory (the project's), so a relative path is rooted
    // here, against the caller's, as it was when the logger ran in-process.
    [Test]
    public void are_rooted_against_the_current_directory()
    {
        using var ctx = Buildalyzer.TestTools.Context.ForProject(@"SdkNetStandardProject\SdkNetStandardProject.csproj");
        var analyzer = (ProjectAnalyzer)ctx.Manager.GetProject(ctx.Location.FullName);

        analyzer.AddBinaryLogger("logs/project.binlog");

        analyzer.BinaryLogArguments().Should().ContainSingle()
            .Which.Should().Be($"/bl:LogFile=\"{System.IO.Path.GetFullPath("logs/project.binlog")}\";ProjectImports=Embed");
    }
}
