using System.IO;
using Buildalyzer.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;

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

        [Pure]
        public static CompilerCommand Parse(DirectoryInfo? baseDir, string commandLine, CompilerLanguage language)
        {
            var tokens = SplitCommandLineIntoArguments(commandLine, language) ?? throw new FormatException("Commandline could not be parsed.");
            var location = new FileInfo(tokens[0]);
            var args = tokens[1..];

            return Parse(baseDir?.ToString(), location.Directory?.ToString(), args, language) with
            {
                Text = commandLine,
                CompilerLocation = location,
                Arguments = [.. args],
            };

            static CompilerCommand Parse(string? baseDir, string? root, string[] args, CompilerLanguage language) => language switch
            {
                CompilerLanguage.CSharp => CSharpParser.Parse(args, baseDir, root),
                CompilerLanguage.VisualBasic => VisualBasicParser.Parse(args, baseDir, root),
                CompilerLanguage.FSharp => FSharpParser.Parse(args),
                _ => throw new NotSupportedException($"The {language} language is not supported."),
            };
        }
    }

    private static class CSharpParser
    {
        [Pure]
        public static CSharpCompilerCommand Parse(string[] args, string? baseDir, string? root)
        {
            var arguments = CSharpCommandLineParser.Default.Parse(args, baseDir, root);
            var command = new CSharpCompilerCommand()
            {
                CommandLineArguments = arguments,
            };
            return RoslynParser.Enrich(command, arguments);
        }
    }

    private static class VisualBasicParser
    {
        [Pure]
        public static VisualBasicCompilerCommand Parse(string[] args, string? baseDir, string? root)
        {
            var arguments = VisualBasicCommandLineParser.Default.Parse(args, baseDir, root);
            var command = new VisualBasicCompilerCommand()
            {
                CommandLineArguments = arguments,
                PreprocessorSymbols = arguments.ParseOptions.PreprocessorSymbols.ToImmutableDictionary(),
            };
            return RoslynParser.Enrich(command, arguments);
        }
    }

    private static class FSharpParser
    {
        [Pure]
        public static FSharpCompilerCommand Parse(string[] args)
        {
            var sourceFiles = args.Where(a => a[0] != '-').Select(IOPath.Parse);
            var preprocessorSymbolNames = args.Where(a => a.StartsWith("--define:")).Select(a => a[9..]);
            var metadataReferences = args.Where(a => a.StartsWith("-r:")).Select(a => a[3..]);

            return new()
            {
                MetadataReferences = [.. metadataReferences],
                PreprocessorSymbolNames = [.. preprocessorSymbolNames],
                SourceFiles = [.. sourceFiles],
            };
        }
    }

    private static class RoslynParser
    {
        public static TCommand Enrich<TCommand>(TCommand command, CommandLineArguments arguments)
            where TCommand : CompilerCommand

            => command with
            {
                AnalyzerReferences = [.. arguments.AnalyzerReferences.Select(AsIOPath)],
                AnalyzerConfigPaths = [.. arguments.AnalyzerConfigPaths.Select(IOPath.Parse)],
                MetadataReferences = [.. arguments.MetadataReferences.Select(m => m.Reference)],
                Aliases = arguments.MetadataReferences.Where(m => !m.Properties.Aliases.IsEmpty).ToImmutableDictionary(m => m.Reference, m => m.Properties.Aliases),
                PreprocessorSymbolNames = [.. arguments.ParseOptions.PreprocessorSymbolNames],
                SourceFiles = [.. arguments.SourceFiles.Select(AsIOPath)],
                AdditionalFiles = [.. arguments.AdditionalFiles.Select(AsIOPath)],
                EmbeddedFiles = [.. arguments.EmbeddedFiles.Select(AsIOPath)],
            };
    }

    [Pure]
    internal static IOPath AsIOPath(CommandLineAnalyzerReference file) => IOPath.Parse(file.FilePath);

    [Pure]
    internal static IOPath AsIOPath(CommandLineSourceFile file) => IOPath.Parse(file.Path);
}
