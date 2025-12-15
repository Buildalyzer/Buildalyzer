using System.Diagnostics;
using System.IO;
using Buildalyzer.Environment;
using Buildalyzer.Handling;
using Grammr;

namespace Buildalyzer.TestTools;

/// <summary>Creates a test context for testing <see cref="IProjectAnalyzer"/>s.</summary>
/// <remarks>
/// The context ensures an fresh build (deletes previous artifacts in advance).
/// The context logs to the console in DEBUG mode.
/// </remarks>
public sealed class ProjectFileTestContext : Context
{
    internal ProjectFileTestContext(FileInfo projectFile)
        : base(projectFile, c => new(new AnalyzerManagerOptions { LogWriter = c.Log }))
    {
        Analyzer = Manager.GetProject(Location.FullName);

        AddBinaryLogger(Analyzer);
        DeleteSubDirectory("bin");
        DeleteSubDirectory("obj");
    }

    public IProjectAnalyzer Analyzer { get; }

    public override BuildAnalysis Build(EnvironmentOptions? options = null)
    {
        var results = Analyzer.Build(options ?? new EnvironmentOptions() { DesignTime = false });
        var analysis = BuildEventHandlers.Default.Handle(results.BuildEventArguments);
        return new BuildAnalysis(analysis);
    }

    [Conditional("BinaryLog")]
    internal void AddBinaryLogger(IProjectAnalyzer analyzer)
    {
        analyzer.AddBinaryLogger(Path.Combine(@"C:\Temp\", Path.ChangeExtension(Location.Name, ".core.binlog")));
    }
}
