using System.IO;
using Buildalyzer;
using Buildalyzer.TestTools;

namespace AnalyzerManager_specs;

public class Tracks_one_analyzer_per_project
{
    [Test]
    public void when_the_same_project_is_requested_by_differently_written_paths()
    {
        using var ctx = Context.ForProject(@"SdkNetStandardProject\SdkNetStandardProject.csproj");

        // The same file, written with a redundant parent segment. Analyzers are keyed by path, so
        // without normalization this hands back a second analyzer for the project already tracked.
        string detour = Path.Combine(
            ctx.Location.DirectoryName!,
            "..",
            ctx.Location.Directory!.Name,
            ctx.Location.Name);

        var analyzer = ctx.Manager.GetProject(ctx.Location.FullName);

        ctx.Manager.GetProject(detour).Should().BeSameAs(analyzer);
        ctx.Manager.Projects.Should().ContainSingle();
    }
}

public class Analyze
{
    [Test]
    public void throws_when_the_binary_log_cannot_be_replayed()
    {
        // A file MSBuild cannot read as a binary log: the replay exits without raising a single event, which
        // must surface as an error rather than as an empty set of results.
        string directory = Path.Combine(Path.GetTempPath(), $"buildalyzer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string binaryLog = Path.Combine(directory, "corrupt.binlog");
            File.WriteAllText(binaryLog, "this is not a binary log");

            var manager = new AnalyzerManager();

            manager.Invoking(m => m.Analyze(binaryLog))
                .Should().Throw<InvalidOperationException>()
                .WithMessage("*could not replay*corrupt.binlog*");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
