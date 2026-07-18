using System.IO;

namespace Buildalyzer;

[DebuggerDisplay("{Language.Display()}: {Text}")]
public abstract record CompilerCommand
{
    /// <summary>The compiler lanuague.</summary>
    public abstract CompilerLanguage Language { get; }

    /// <summary>The original text of the compiler command.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The tokenized command line arguments (excluding the compiler executable).</summary>
    public ImmutableArray<string> Arguments { get; init; } = [];

    /// <summary>The location of the used compiler.</summary>
    public FileInfo? CompilerLocation { get; init; }

    /// <summary>The source files fed to the compiler.</summary>
    public ImmutableArray<string> SourceFiles { get; init; } = [];

    /// <summary>The additional files fed to the compiler.</summary>
    public ImmutableArray<string> AdditionalFiles { get; init; } = [];

    /// <summary>The embedded files.</summary>
    public ImmutableArray<string> EmbeddedFiles { get; init; } = [];

    /// <summary>The analyzer (assembly) references.</summary>
    public ImmutableArray<string> AnalyzerReferences { get; init; } = [];

    /// <summary>The analyzer config (.editorconfig) paths.</summary>
    public ImmutableArray<string> AnalyzerConfigPaths { get; init; } = [];

    /// <summary>The preprocessor symbol names.</summary>
    public ImmutableArray<string> PreprocessorSymbolNames { get; init; } = [];

    /// <summary>The metadata (assembly) references.</summary>
    public ImmutableArray<string> MetadataReferences { get; init; } = [];

    /// <summary>
    /// The aliases used for the metadata references (reference path to alias names).
    /// </summary>
    public ImmutableDictionary<string, ImmutableArray<string>> Aliases { get; init; } = ImmutableDictionary<string, ImmutableArray<string>>.Empty;

    /// <summary>
    /// The metadata references whose interop types are embedded (the <c>EmbedInteropTypes</c> metadata
    /// is <c>true</c>).
    /// </summary>
    public ImmutableHashSet<string> EmbedInteropTypes { get; init; } = ImmutableHashSet<string>.Empty;

    /// <inheritdoc />
    [Pure]
    public override string ToString() => Text ?? string.Empty;
}
