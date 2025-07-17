using Microsoft.CodeAnalysis.CSharp;

namespace Buildalyzer;

public sealed record CSharpCompilerCommand : RoslynBasedCompilerCommand<CSharpCommandLineArguments>
{
    /// <inheritdoc />
    public override CompilerLanguage Language => CompilerLanguage.CSharp;
}