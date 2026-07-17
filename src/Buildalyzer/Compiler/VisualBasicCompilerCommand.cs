namespace Buildalyzer;

public sealed record VisualBasicCompilerCommand : CompilerCommand
{
    /// <inheritdoc />
    public override CompilerLanguage Language => CompilerLanguage.VisualBasic;
}
