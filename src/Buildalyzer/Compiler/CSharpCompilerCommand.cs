namespace Buildalyzer;

public sealed record CSharpCompilerCommand : CompilerCommand
{
    /// <inheritdoc />
    public override CompilerLanguage Language => CompilerLanguage.CSharp;
}
