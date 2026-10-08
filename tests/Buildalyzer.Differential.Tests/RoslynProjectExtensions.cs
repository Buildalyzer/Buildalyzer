using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Buildalyzer.Differential.Tests;

/// <summary>
/// Normalises the parts of a Roslyn <see cref="Project"/> that we want to compare across the
/// two loaders. The <c>*Paths</c> helpers reduce each collection to an order-independent set of
/// paths - exactly as reported, not canonicalized - so MSBuildWorkspace and Buildalyzer can be diffed
/// directly with AwesomeAssertions' <c>BeEquivalentTo</c>; the <c>*Names</c> helpers give bare file
/// names for "contains X" checks. Both loaders run against the same checkout, so full paths are the
/// right equality: comparing names alone would hide a document or reference resolved from the wrong
/// directory, and canonicalizing would hide a "../" spelling of the right one.
/// </summary>
internal static class RoslynProjectExtensions
{
    public static string[] SourceFilePaths(this Project project) =>
        Paths(project.Documents.Select(d => d.FilePath));

    public static string[] SourceFileNames(this Project project) =>
        Names(project.SourceFilePaths());

    public static string[] AdditionalDocumentPaths(this Project project) =>
        Paths(project.AdditionalDocuments.Select(d => d.FilePath ?? d.Name));

    public static string[] AdditionalDocumentNames(this Project project) =>
        Names(project.AdditionalDocumentPaths());

    public static string[] AnalyzerConfigDocumentPaths(this Project project) =>
        Paths(project.AnalyzerConfigDocuments.Select(d => d.FilePath ?? d.Name));

    public static string[] AnalyzerConfigDocumentNames(this Project project) =>
        Names(project.AnalyzerConfigDocumentPaths());

    public static string[] MetadataReferencePaths(this Project project) =>
        Paths(project.MetadataReferences.OfType<PortableExecutableReference>().Select(r => r.FilePath));

    public static string[] MetadataReferenceNames(this Project project) =>
        Names(project.MetadataReferencePaths());

    public static string[] AnalyzerReferencePaths(this Project project) =>
        Paths(project.AnalyzerReferences.Select(r => r.FullPath ?? r.Display));

    public static string[] AnalyzerReferenceNames(this Project project) =>
        Names(project.AnalyzerReferencePaths());

    public static string[] ProjectReferencePaths(this Project project) =>
        Paths(project.ProjectReferences.Select(r => project.Solution.GetProject(r.ProjectId)?.FilePath));

    public static string[] ProjectReferenceNames(this Project project) =>
        Names(project.ProjectReferencePaths());

    /// <summary>The paths of every project loaded into the same workspace (the whole graph).</summary>
    public static string[] SolutionProjectPaths(this Project project) =>
        Paths(project.Solution.Projects.Select(p => p.FilePath));

    /// <summary>The file names of every project loaded into the same workspace (the whole graph).</summary>
    public static string[] SolutionProjectNames(this Project project) =>
        Names(project.SolutionProjectPaths());

    public static string[] PreprocessorSymbols(this Project project) => project.ParseOptions is { } parse
        ? [.. parse.PreprocessorSymbolNames.OrderBy(x => x, StringComparer.Ordinal)]
        : [];

    public static CSharpCompilationOptions CSharpOptions(this Project project) =>
        (CSharpCompilationOptions)project.CompilationOptions!;

    public static CSharpParseOptions CSharpParse(this Project project) =>
        (CSharpParseOptions)project.ParseOptions!;

    // Deliberately not run through Path.GetFullPath: MSBuildWorkspace reports canonical full paths, and a
    // "../" spelling of the same file on the Buildalyzer side is a divergence these tests exist to catch.
    private static string[] Paths(IEnumerable<string?> paths) =>
    [
        .. paths
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
    ];

    private static string[] Names(IEnumerable<string> paths) =>
    [
        .. paths
            .Select(p => Path.GetFileName(p))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
    ];
}
