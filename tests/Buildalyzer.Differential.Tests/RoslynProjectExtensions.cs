using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Buildalyzer.Differential.Tests;

/// <summary>
/// Normalises the parts of a Roslyn <see cref="Project"/> that we want to compare across the
/// two loaders. Everything is reduced to case-insensitive, order-independent sets of file
/// names (or simple scalars) so that MSBuildWorkspace and Buildalyzer can be diffed directly
/// with AwesomeAssertions' <c>BeEquivalentTo</c>.
/// </summary>
internal static class RoslynProjectExtensions
{
    public static string[] SourceFileNames(this Project project) =>
    [
        .. project.Documents
            .Where(d => d.FilePath is not null)
            .Select(d => FileName(d.FilePath!))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
    ];

    public static string[] AdditionalDocumentNames(this Project project) =>
    [
        .. project.AdditionalDocuments
            .Select(d => FileName(d.FilePath ?? d.Name))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
    ];

    public static string[] AnalyzerConfigDocumentNames(this Project project) =>
    [
        .. project.AnalyzerConfigDocuments
            .Select(d => FileName(d.FilePath ?? d.Name))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
    ];

    public static string[] MetadataReferenceNames(this Project project) =>
    [
        .. project.MetadataReferences
            .OfType<PortableExecutableReference>()
            .Where(r => r.FilePath is not null)
            .Select(r => FileName(r.FilePath!))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
    ];

    public static string[] AnalyzerReferenceNames(this Project project) =>
    [
        .. project.AnalyzerReferences
            .Select(r => FileName(r.FullPath ?? r.Display ?? string.Empty))
            .Where(x => x.Length > 0)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
    ];

    public static string[] ProjectReferenceNames(this Project project) =>
    [
        .. project.ProjectReferences
            .Select(r => project.Solution.GetProject(r.ProjectId)?.FilePath)
            .Where(x => x is not null)
            .Select(x => FileName(x!))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>The file names of every project loaded into the same workspace (the whole graph).</summary>
    public static string[] SolutionProjectNames(this Project project) =>
    [
        .. project.Solution.Projects
            .Where(p => p.FilePath is not null)
            .Select(p => FileName(p.FilePath!))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
    ];

    public static string[] PreprocessorSymbols(this Project project) => project.ParseOptions is CSharpParseOptions parse
        ? [.. parse.PreprocessorSymbolNames.OrderBy(x => x, StringComparer.Ordinal)]
        : [];

    public static CSharpCompilationOptions CSharpOptions(this Project project) =>
        (CSharpCompilationOptions)project.CompilationOptions!;

    public static CSharpParseOptions CSharpParse(this Project project) =>
        (CSharpParseOptions)project.ParseOptions!;

    private static string FileName(string path) => Path.GetFileName(path);
}
