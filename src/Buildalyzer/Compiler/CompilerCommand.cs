using System.IO;
using Buildalyzer.IO;
using Microsoft.CodeAnalysis;

namespace Buildalyzer;

[DebuggerDisplay("{Language.Display()}: {Text}")]
public abstract record CompilerCommand
{
    /// <summary>The compiler lanuague.</summary>
    public abstract CompilerLanguage Language { get; }

    /// <summary>The original text of the compiler command.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The parsed command line arguments.</summary>
    public ImmutableArray<string> Arguments { get; init; } = [];

    /// <summary>The location of the used compiler.</summary>
    public FileInfo? CompilerLocation { get; init; }

    /// <inheritdoc  cref="CommandLineArguments.Errors" />
    public ImmutableArray<Diagnostic> Errors { get; init; } = [];

    /// <inheritdoc  cref="CommandLineArguments.SourceFiles" />
    public ImmutableArray<IOPath> SourceFiles { get; init; } = [];

    /// <inheritdoc  cref="CommandLineArguments.AdditionalFiles" />
    public ImmutableArray<IOPath> AdditionalFiles { get; init; } = [];

    /// <inheritdoc  cref="CommandLineArguments.EmbeddedFiles" />
    public ImmutableArray<IOPath> EmbeddedFiles { get; init; } = [];

    /// <inheritdoc  cref="CommandLineArguments.AnalyzerReferences" />
    public ImmutableArray<IOPath> AnalyzerReferences { get; init; } = [];

    /// <inheritdoc  cref="CommandLineArguments.AnalyzerConfigPaths" />
    public ImmutableArray<IOPath> AnalyzerConfigPaths { get; init; } = [];

    /// <inheritdoc  cref="ParseOptions.PreprocessorSymbolNames" />
    public ImmutableArray<string> PreprocessorSymbolNames { get; init; } = [];

    /// <inheritdoc cref="CommandLineArguments.MetadataReferences" />
    public ImmutableArray<string> MetadataReferences { get; init; } = [];

    /// <summary>
    /// The aliases used in the command line arguments.
    /// </summary>
    public ImmutableDictionary<string, ImmutableArray<string>> Aliases { get; init; } = ImmutableDictionary<string, ImmutableArray<string>>.Empty;

    /// <inheritdoc />
    [Pure]
    public override string ToString() => Text ?? string.Empty;
}
