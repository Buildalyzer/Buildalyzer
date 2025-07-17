using Microsoft.CodeAnalysis.VisualBasic;

namespace Buildalyzer;

public sealed record VisualBasicCompilerCommand : RoslynBasedCompilerCommand<VisualBasicCommandLineArguments>
{
    /// <inheritdoc />
    public override CompilerLanguage Language => CompilerLanguage.VisualBasic;

    /// <inheritdoc cref="VisualBasicParseOptions.PreprocessorSymbols" />
    public ImmutableDictionary<string, object>? PreprocessorSymbols { get; init; }
}
