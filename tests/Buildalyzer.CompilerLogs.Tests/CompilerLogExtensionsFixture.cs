using System.IO;
using Basic.CompilerLog.Util;
using Buildalyzer.TestTools;
using Microsoft.CodeAnalysis;

namespace Buildalyzer.CompilerLogs.Tests;

[TestFixture]
[NonParallelizable]
public class CompilerLogExtensionsFixture
{
    [Test]
    public void Creates_complog_from_design_time_build()
    {
        using var ctx = Context.ForProject(@"SdkNetStandardProject\SdkNetStandardProject.csproj");

        IAnalyzerResults results = ctx.Analyzer.Build();
        using var stream = new MemoryStream();

        CompilerLogCreateResult result = results.TryCreateCompilerLog(stream);

        result.Succeeded.Should().BeTrue(because: string.Join(System.Environment.NewLine, result.Diagnostics));
        result.CompilerCalls.Should().ContainSingle();

        // Round-trip: the complog alone must be able to rehydrate a compilation
        // that contains the project's types.
        stream.Position = 0;
        using var reader = CompilerLogReader.Create(stream, BasicAnalyzerKind.None);

        CompilationData data = reader.ReadAllCompilationData().Single();
        Compilation compilation = data.GetCompilationAfterGenerators();

        compilation.GetSymbolsWithName("Class1").Should().NotBeEmpty();
        compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }
}
