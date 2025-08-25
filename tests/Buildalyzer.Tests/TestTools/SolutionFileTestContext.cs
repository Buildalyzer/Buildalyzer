using System.IO;

namespace Buildalyzer.TestTools;

/// <summary>Creates a test context for testing <see cref="IProjectAnalyzer"/>s.</summary>
/// <remarks>
/// The context ensures an fresh build (deletes previous artifacts in advance).
/// The context logs to the console in DEBUG mode.
/// </remarks>
public sealed class SolutionFileTestContext : Context
{
    internal SolutionFileTestContext(FileInfo solutionFile)
        : base(solutionFile, c => new(solutionFile.FullName, new AnalyzerManagerOptions { LogWriter = c.Log }))
    {
    }
}
