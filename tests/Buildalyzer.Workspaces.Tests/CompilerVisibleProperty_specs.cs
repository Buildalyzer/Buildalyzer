using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Buildalyzer.TestTools;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Buildalyzer.Workspaces.Tests;

[TestFixture]
[NonParallelizable]
public class CompilerVisibleProperty
{
    [Test]
    public async Task is_exposed()
    {
        using var ctx = Context.ForProject(@"CompilerVisibleProperty\CompilerVisibleProperty.csproj");

        using var workspace = ctx.Analyzer.GetWorkspace();

        var project = workspace.CurrentSolution.Projects.Single();

        var compilation = await project.GetCompilationAsync();
        var options = compilation!.Options.WithSpecificDiagnosticOptions([KeyValuePair.Create("TEST01", ReportDiagnostic.Warn)]);

        var diagnostics = await compilation
            .WithOptions(options)
            .WithAnalyzers([new ReportCustomProperty()])
            .GetAllDiagnosticsAsync(default);

        diagnostics[^1].ToString().Should().BeEquivalentTo("warning TEST01: Has custom property with value 'Custom value'");
    }
}

#pragma warning disable

[DiagnosticAnalyzer(LanguageNames.CSharp)]
file sealed class ReportCustomProperty : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Descriptor = new(
        id: "TEST01",
        title: "Has custom property",
        messageFormat: "Has custom property with value '{0}'",
        category: "Test",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Descriptor];

    public override void Initialize(AnalysisContext context) => context.RegisterCompilationAction(Report);

    private static void Report(CompilationAnalysisContext context)
    {
        var value = context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.CustomProperty", out var v)
            ? v
            : null;

        var issue = Diagnostic.Create(Descriptor, null, value);
        context.ReportDiagnostic(issue);
    }
}
