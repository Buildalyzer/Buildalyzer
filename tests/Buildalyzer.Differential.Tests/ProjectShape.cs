using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Buildalyzer.Differential.Tests;

/// <summary>
/// The complete observable structure of a Roslyn <see cref="Project"/>, flattened to plain
/// values so two loaders can be diffed in one <c>BeEquivalentTo</c> and every difference is
/// reported at once: identity, documents (with folders), references (with aliases and
/// interop flags), analyzers, and every scalar compilation and parse option.
/// </summary>
internal sealed record ProjectShape(
    string Name,
    string AssemblyName,
    string Language,
    string? FilePath,
    string? OutputFilePath,
    string? OutputRefFilePath,
    string? DefaultNamespace,
    DocumentShape[] Documents,
    DocumentShape[] AdditionalDocuments,
    DocumentShape[] AnalyzerConfigDocuments,
    ProjectReferenceShape[] ProjectReferences,
    MetadataReferenceShape[] MetadataReferences,
    AnalyzerReferenceShape[] AnalyzerReferences,
    SortedDictionary<string, string?> CompilationOptions,
    SortedDictionary<string, string?> ParseOptions);

internal sealed record DocumentShape(string Name, string? FilePath, string Folders, string? SourceCodeKind);

internal sealed record ProjectReferenceShape(string? TargetFilePath, string? TargetName, string Aliases, bool EmbedInteropTypes);

internal sealed record MetadataReferenceShape(string? FilePath, string? Display, string Aliases, bool EmbedInteropTypes, string Kind);

internal sealed record AnalyzerReferenceShape(string? Display, string? FullPath);

internal static class ProjectShapeExtensions
{
    /// <summary>Every project in the solution, ordered by file path then name so the two sides line up.</summary>
    public static ProjectShape[] Shape(this Solution solution) =>
    [
        .. solution.Projects
            .Select(Shape)
            .OrderBy(p => p.FilePath, StringComparer.Ordinal)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
    ];

    public static ProjectShape Shape(this Project project) => new(
        project.Name,
        project.AssemblyName,
        project.Language,
        NormalizePath(project.FilePath),
        NormalizePath(project.OutputFilePath),
        NormalizePath(project.OutputRefFilePath),
        project.DefaultNamespace,
        Documents(project.Documents),
        Documents(project.AdditionalDocuments),
        Documents(project.AnalyzerConfigDocuments),
        ProjectReferences(project),
        MetadataReferences(project),
        AnalyzerReferences(project),
        CompilationOptions(project.CompilationOptions),
        ParseOptions(project.ParseOptions));

    private static DocumentShape[] Documents(IEnumerable<TextDocument> documents) =>
    [
        .. documents
            .Select(d => new DocumentShape(
                d.Name,
                NormalizePath(d.FilePath),
                string.Join("/", d.Folders),
                (d as Document)?.SourceCodeKind.ToString()))
            .OrderBy(d => d.FilePath, StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
    ];

    private static ProjectReferenceShape[] ProjectReferences(Project project) =>
    [
        .. project.ProjectReferences
            .Select(r =>
            {
                Project? target = project.Solution.GetProject(r.ProjectId);
                return new ProjectReferenceShape(
                    NormalizePath(target?.FilePath),
                    target?.Name,
                    Aliases(r.Aliases),
                    r.EmbedInteropTypes);
            })
            .OrderBy(r => r.TargetFilePath, StringComparer.Ordinal)
            .ThenBy(r => r.TargetName, StringComparer.Ordinal)
            .ThenBy(r => r.Aliases, StringComparer.Ordinal)
    ];

    private static MetadataReferenceShape[] MetadataReferences(Project project) =>
    [
        .. project.MetadataReferences
            .Select(r => new MetadataReferenceShape(
                NormalizePath((r as PortableExecutableReference)?.FilePath),
                NormalizePath(r.Display),
                Aliases(r.Properties.Aliases),
                r.Properties.EmbedInteropTypes,
                r.Properties.Kind.ToString()))
            .OrderBy(r => r.FilePath, StringComparer.Ordinal)
            .ThenBy(r => r.Display, StringComparer.Ordinal)
            .ThenBy(r => r.Aliases, StringComparer.Ordinal)
    ];

    private static AnalyzerReferenceShape[] AnalyzerReferences(Project project) =>
    [
        .. project.AnalyzerReferences
            .Select(r => new AnalyzerReferenceShape(r.Display, NormalizePath(r.FullPath)))
            .OrderBy(r => r.FullPath, StringComparer.Ordinal)
            .ThenBy(r => r.Display, StringComparer.Ordinal)
    ];

    private static SortedDictionary<string, string?> CompilationOptions(CompilationOptions? options)
    {
        SortedDictionary<string, string?> shape = new(StringComparer.Ordinal)
        {
            ["Type"] = options?.GetType().FullName,
        };
        if (options is null)
        {
            return shape;
        }

        shape["OutputKind"] = Value(options.OutputKind);
        shape["ModuleName"] = options.ModuleName;
        shape["MainTypeName"] = options.MainTypeName;
        shape["ScriptClassName"] = options.ScriptClassName;
        shape["OptimizationLevel"] = Value(options.OptimizationLevel);
        shape["CheckOverflow"] = Value(options.CheckOverflow);
        shape["Platform"] = Value(options.Platform);
        shape["GeneralDiagnosticOption"] = Value(options.GeneralDiagnosticOption);
        shape["WarningLevel"] = Value(options.WarningLevel);
        shape["ConcurrentBuild"] = Value(options.ConcurrentBuild);
        shape["Deterministic"] = Value(options.Deterministic);
        shape["CryptoKeyFile"] = NormalizePath(options.CryptoKeyFile);
        shape["CryptoKeyContainer"] = options.CryptoKeyContainer;
        shape["DelaySign"] = options.DelaySign?.ToString();
        shape["PublicSign"] = Value(options.PublicSign);
        shape["MetadataImportOptions"] = Value(options.MetadataImportOptions);
        shape["SpecificDiagnosticOptions"] = Map(options.SpecificDiagnosticOptions);

        if (options is CSharpCompilationOptions cs)
        {
            shape["AllowUnsafe"] = Value(cs.AllowUnsafe);
            shape["NullableContextOptions"] = Value(cs.NullableContextOptions);
            shape["Usings"] = string.Join(",", cs.Usings);
        }

        if (options is VisualBasicCompilationOptions vb)
        {
            shape["OptionExplicit"] = Value(vb.OptionExplicit);
            shape["OptionInfer"] = Value(vb.OptionInfer);
            shape["OptionStrict"] = Value(vb.OptionStrict);
            shape["OptionCompareText"] = Value(vb.OptionCompareText);
            shape["RootNamespace"] = vb.RootNamespace;
            shape["GlobalImports"] = string.Join(",", vb.GlobalImports.Select(i => i.Name).OrderBy(x => x, StringComparer.Ordinal));
            shape["EmbedVbCoreRuntime"] = Value(vb.EmbedVbCoreRuntime);
        }

        return shape;
    }

    private static SortedDictionary<string, string?> ParseOptions(ParseOptions? options)
    {
        SortedDictionary<string, string?> shape = new(StringComparer.Ordinal)
        {
            ["Type"] = options?.GetType().FullName,
        };
        if (options is null)
        {
            return shape;
        }

        shape["Kind"] = Value(options.Kind);
        shape["DocumentationMode"] = Value(options.DocumentationMode);
        shape["Features"] = Map(options.Features);
        shape["PreprocessorSymbols"] = string.Join(",", options.PreprocessorSymbolNames.OrderBy(x => x, StringComparer.Ordinal));

        if (options is CSharpParseOptions cs)
        {
            shape["LanguageVersion"] = Value(cs.LanguageVersion);
        }

        if (options is VisualBasicParseOptions vb)
        {
            shape["LanguageVersion"] = Value(vb.LanguageVersion);
            shape["PreprocessorSymbolValues"] = Map(vb.PreprocessorSymbols.Select(s => new KeyValuePair<string, object?>(s.Key, s.Value)));
        }

        return shape;
    }

    private static string Value<T>(T value) where T : struct => value.ToString()!;

    private static string Aliases(ImmutableArray<string> aliases) =>
        aliases.IsDefaultOrEmpty ? string.Empty : string.Join(",", aliases.OrderBy(x => x, StringComparer.Ordinal));

    private static string Map<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>> map) where TKey : notnull =>
        string.Join(",", map.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}"));

    // Paths are compared exactly as each loader reports them. MSBuildWorkspace hands out canonical full paths
    // (Roslyn's command-line parser resolves and collapses every input), and consumers key documents and
    // references by that string, so an un-normalized "../" spelling of the same file is a real divergence -
    // one that a Path.GetFullPath here would silently paper over.
    private static string? NormalizePath(string? path) => path;
}
