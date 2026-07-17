namespace Buildalyzer;

public static class Compiler
{
    public static class CommandLine
    {
        [Pure]
        public static string[]? SplitCommandLineIntoArguments(string? commandLine, CompilerLanguage language) => language switch
        {
            CompilerLanguage.CSharp => RoslynCommandLineParser.SplitCommandLineIntoArguments(commandLine, "csc", "csc.dll", "csc.exe"),
            CompilerLanguage.VisualBasic => RoslynCommandLineParser.SplitCommandLineIntoArguments(commandLine, "vbc", "vbc.dll", "vbc.exe"),
            CompilerLanguage.FSharp => FSharpCommandLineParser.SplitCommandLineIntoArguments(commandLine),
            _ => throw new NotSupportedException($"The {language} language is not supported."),
        };
    }
}
