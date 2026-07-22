using System.IO;
using Buildalyzer.IO;

namespace Buildalyzer;

/// <summary>
/// Builds a <see cref="CompilerCommand"/> from the compiler task's resolved input parameters (collected as
/// <see cref="CompilerInputItem"/>s over the pipe) plus the raw compiler command line. Buildalyzer no longer
/// performs semantic command-line parsing (no Roslyn).
/// <para>
/// The compiler's <em>item</em> inputs (Sources, References, Analyzers, AdditionalFiles, AnalyzerConfigFiles,
/// EmbeddedFiles) come straight from the task parameters. The command line supplies
/// <see cref="CompilerCommand.Text"/>, <see cref="CompilerCommand.CompilerLocation"/> and
/// <see cref="CompilerCommand.Arguments"/>, and - because MSBuild only forwards <em>item</em> task inputs at
/// normal verbosity (not scalar string parameters such as <c>DefineConstants</c>) - the preprocessor symbols
/// are read from the command line's <c>define</c> switch.
/// </para>
/// </summary>
internal static class CompilerCommandBuilder
{
    private static readonly char[] CSharpDefineSplitters = [';', ','];

    [Pure]
    public static CompilerCommand? Build(
        CompilerLanguage language,
        string projectDirectory,
        string? commandLineText,
        IReadOnlyDictionary<string, List<CompilerInputItem>> taskInputs)
    {
        // Nothing was captured for this project - there is no compiler command to build.
        if (string.IsNullOrWhiteSpace(commandLineText) && taskInputs.Count == 0)
        {
            return null;
        }

        FileInfo? location = null;
        ImmutableArray<string> arguments = [];

        if (!string.IsNullOrWhiteSpace(commandLineText)
            && Compiler.CommandLine.SplitCommandLineIntoArguments(commandLineText, language) is { Length: > 0 } tokens)
        {
            location = new FileInfo(tokens[0]);
            arguments = [.. tokens[1..]];
        }

        List<CompilerInputItem> sources = Items(taskInputs, "Sources");
        List<CompilerInputItem> references = Items(taskInputs, "References");

        IEnumerable<string> embedded = Items(taskInputs, "EmbeddedFiles").Select(i => i.Spec);
        if (EmbedsAllSources(arguments))
        {
            embedded = embedded.Concat(sources.Select(i => i.Spec));
        }

        CompilerCommand command = language switch
        {
            CompilerLanguage.CSharp => new CSharpCompilerCommand(),
            CompilerLanguage.VisualBasic => new VisualBasicCompilerCommand(),
            CompilerLanguage.FSharp => new FSharpCompilerCommand(),
            _ => throw new NotSupportedException($"The {language} language is not supported."),
        };

        return command with
        {
            Text = commandLineText ?? string.Empty,
            CompilerLocation = location,
            Arguments = arguments,
            SourceFiles = [.. sources.Select(i => Resolve(projectDirectory, i.Spec).ToString())],
            MetadataReferences = [.. references.Select(i => i.Spec)],
            AnalyzerReferences = [.. Items(taskInputs, "Analyzers").Select(i => Resolve(projectDirectory, i.Spec).ToString())],
            AdditionalFiles = [.. Items(taskInputs, "AdditionalFiles").Select(i => Resolve(projectDirectory, i.Spec).ToString())],
            AnalyzerConfigPaths = [.. Items(taskInputs, "AnalyzerConfigFiles").Select(i => Resolve(projectDirectory, i.Spec).ToString())],
            EmbeddedFiles = [.. embedded.Select(spec => Resolve(projectDirectory, spec).ToString())],
            PreprocessorSymbolNames = [.. PreprocessorSymbols(language, arguments)],
            Aliases = BuildAliases(references),
            EmbedInteropTypes = BuildEmbedInteropTypes(references),
        };
    }

    [Pure]
    private static ImmutableHashSet<string> BuildEmbedInteropTypes(List<CompilerInputItem> references) =>
    [
        .. references
            .Where(r => r.Metadata.Any(m => m.Name.IsMatch("EmbedInteropTypes")
                && string.Equals((m.Value ?? string.Empty).Trim(), "true", StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.Spec)
    ];

    [Pure]
    private static List<CompilerInputItem> Items(IReadOnlyDictionary<string, List<CompilerInputItem>> taskInputs, string key)
        => taskInputs.TryGetValue(key, out var items) ? items : [];

    [Pure]
    private static IOPath Resolve(string projectDirectory, string spec)
        => IOPath.Parse(Path.IsPathRooted(spec) || projectDirectory.Length == 0 ? spec : Path.Combine(projectDirectory, spec));

    // Csc/Vbc embed all source files when a bare "/embed" (or "-embed") switch is present.
    [Pure]
    private static bool EmbedsAllSources(ImmutableArray<string> arguments)
        => arguments.Any(a => a is "/embed" or "-embed" or "/embed+" or "-embed+");

    [Pure]
    private static IEnumerable<string> PreprocessorSymbols(CompilerLanguage language, ImmutableArray<string> arguments)
    {
        switch (language)
        {
            case CompilerLanguage.CSharp:
                // e.g. /define:TRACE;DEBUG;NET8_0 (may also be comma separated).
                return DefineValues(arguments)
                    .SelectMany(v => v.Split(CSharpDefineSplitters, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Distinct(StringComparer.Ordinal);

            case CompilerLanguage.VisualBasic:
                // e.g. /define:CONFIG="Debug",DEBUG=-1,TRACE=-1 - the symbol names are before the '='.
                // VBC_VER and TARGET are synthesized by the VB compiler and are not on the command line.
                return DefineValues(arguments)
                    .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Select(NameBeforeEquals)
                    .Where(name => name.Length > 0)
                    .Concat(["VBC_VER", "TARGET"])
                    .Distinct(StringComparer.Ordinal);

            case CompilerLanguage.FSharp:
                // F# uses one --define:SYMBOL switch per symbol.
                return arguments
                    .Where(a => a.IsMatchStart("--define:"))
                    .Select(a => a[9..].Trim())
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.Ordinal);

            default:
                return [];
        }
    }

    // Extracts the value of a C#/VB "define" (or "d") switch, e.g. "/define:VALUE" or "-d:VALUE".
    [Pure]
    private static IEnumerable<string> DefineValues(ImmutableArray<string> arguments)
    {
        foreach (string arg in arguments)
        {
            if (arg.Length < 3 || (arg[0] != '/' && arg[0] != '-'))
            {
                continue;
            }

            int colon = arg.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            string option = arg[1..colon];
            if (option.IsMatch("define") || option.IsMatch("d"))
            {
                yield return arg[(colon + 1)..];
            }
        }
    }

    [Pure]
    private static string NameBeforeEquals(string pair)
    {
        int equals = pair.IndexOf('=');
        return (equals >= 0 ? pair[..equals] : pair).Trim();
    }

    [Pure]
    private static ImmutableDictionary<string, ImmutableArray<string>> BuildAliases(List<CompilerInputItem> references)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>();

        foreach (var group in references.GroupBy(r => r.Spec, StringComparer.Ordinal))
        {
            ImmutableArray<string> aliases =
            [
                .. group
                    .SelectMany(AliasNames)
                    // 'global' is never reported by Roslyn as an alias, so filter it out.
                    .Where(a => a.Length > 0 && !a.IsMatch("global"))
                    .Distinct(StringComparer.Ordinal)
            ];

            if (!aliases.IsEmpty)
            {
                builder[group.Key] = aliases;
            }
        }

        return builder.ToImmutable();

        static IEnumerable<string> AliasNames(CompilerInputItem item)
            => item.Metadata
                .Where(m => m.Name.IsMatch("Aliases"))
                .SelectMany(m => (m.Value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
