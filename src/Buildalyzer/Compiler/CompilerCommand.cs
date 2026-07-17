using System.IO;
using Buildalyzer.IO;

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
    public ImmutableArray<IOPath> SourceFiles { get; init; } = [];

    /// <summary>The additional files fed to the compiler.</summary>
    public ImmutableArray<IOPath> AdditionalFiles { get; init; } = [];

    /// <summary>The embedded files.</summary>
    public ImmutableArray<IOPath> EmbeddedFiles { get; init; } = [];

    /// <summary>The analyzer (assembly) references.</summary>
    public ImmutableArray<IOPath> AnalyzerReferences { get; init; } = [];

    /// <summary>The analyzer config (.editorconfig) paths.</summary>
    public ImmutableArray<IOPath> AnalyzerConfigPaths { get; init; } = [];

    /// <summary>The preprocessor symbol names.</summary>
    public ImmutableArray<string> PreprocessorSymbolNames { get; init; } = [];

    /// <summary>The metadata (assembly) references.</summary>
    public ImmutableArray<string> MetadataReferences { get; init; } = [];

    /// <summary>
    /// The aliases used for the metadata references (reference path to alias names).
    /// </summary>
    public ImmutableDictionary<string, ImmutableArray<string>> Aliases { get; init; } = ImmutableDictionary<string, ImmutableArray<string>>.Empty;

    /// <inheritdoc />
    [Pure]
    public override string ToString() => Text ?? string.Empty;
}
