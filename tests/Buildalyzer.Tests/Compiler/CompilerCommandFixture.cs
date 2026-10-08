using System.Collections.Generic;
using System.IO;
using System.Linq;
using Buildalyzer.IO;

namespace Buildalyzer.Tests.Compiler;

[TestFixture]
public class CompilerCommandFixture
{
    // The compiler command is now built from the compiler task's resolved input parameters (structured
    // items) plus the raw command line - Buildalyzer no longer performs semantic command-line parsing.

    [Test]
    public void Tokenizes_csharp_command_line_and_locates_compiler()
    {
        const string commandLine =
            "/usr/local/share/dotnet/dotnet exec \"/usr/local/share/dotnet/sdk/8.0.101/Roslyn/bincore/csc.dll\" "
            + "/noconfig /nostdlib+ Program.cs Startup.cs";

        string[]? tokens = Buildalyzer.Compiler.CommandLine.SplitCommandLineIntoArguments(commandLine, CompilerLanguage.CSharp);

        tokens.Should().NotBeNull();
        // Quotes are stripped and tokenization starts at the compiler executable.
        Path.GetFileName(tokens![0]).Should().Be("csc.dll");
        tokens.Should().Contain("/noconfig");
        tokens.Should().Contain("Program.cs");
    }

    [Test(Description = "The vendored tokenizer keeps Roslyn's quote handling")]
    public void Tokenizes_quotes_like_roslyn()
    {
        // An argument wrapped in one pair of quotes loses them; quotes inside an argument stay for the
        // compiler's own parser to interpret; an empty quoted argument is dropped.
        const string commandLine =
            "csc.exe \"C:\\a b\\Program.cs\" /define:CONFIG=\"Debug\",DEBUG=-1 \"\" /out:\"C:\\a b\\x.dll\"";

        string[] tokens = [.. RoslynCommandLineParser.SplitCommandLineIntoArguments(commandLine, removeHashComments: true)];

        tokens.Should().Equal(
            "csc.exe",
            "C:\\a b\\Program.cs",
            "/define:CONFIG=\"Debug\",DEBUG=-1",
            "/out:\"C:\\a b\\x.dll\"");
    }

    [Test(Description = "The vendored tokenizer drops the characters Roslyn treats as illegal in an argument")]
    public void Drops_illegal_characters_like_roslyn()
    {
        // A control character (here a tab, kept inside the argument by the quotes) and '|' are dropped
        // rather than kept, as Roslyn's tokenizer does.
        const string commandLine = "csc.exe \"a\tb.cs\" /out:x|y.dll";

        string[] tokens = [.. RoslynCommandLineParser.SplitCommandLineIntoArguments(commandLine, removeHashComments: true)];

        tokens.Should().Equal("csc.exe", "ab.cs", "/out:xy.dll");
    }

    [Test]
    public void Builds_csharp_command_from_task_parameters()
    {
        // Source lists come from the task parameters; preprocessor symbols come from the command line's
        // /define switch (MSBuild does not forward scalar task inputs at normal verbosity).
        const string commandLine =
            "/usr/local/share/dotnet/dotnet exec \"/usr/local/share/dotnet/sdk/8.0.101/Roslyn/bincore/csc.dll\" "
            + "/noconfig /define:TRACE;DEBUG;NET8_0";

        var taskInputs = Inputs(
            ("Sources", Items("Program.cs", "Startup.cs")),
            ("References", Items("/refs/mscorlib.dll", "/refs/System.dll")),
            ("Analyzers", Items("/analyzers/Some.Analyzer.dll")),
            ("AnalyzerConfigFiles", Items(".editorconfig")));

        CompilerCommand? command = CompilerCommandBuilder.Build(CompilerLanguage.CSharp, "/proj", commandLine, taskInputs);

        command.Should().NotBeNull();
        command!.Language.Should().Be(CompilerLanguage.CSharp);
        command.Text.Should().Be(commandLine);
        command.CompilerLocation!.Name.Should().Be("csc.dll");
        command.Arguments.Should().Contain("/noconfig");

        FileNames(command.SourceFiles).Should().BeEquivalentTo("Program.cs", "Startup.cs");
        command.MetadataReferences.Select(Path.GetFileName).Should().BeEquivalentTo("mscorlib.dll", "System.dll");
        FileNames(command.AnalyzerReferences).Should().ContainSingle().Which.Should().Be("Some.Analyzer.dll");
        FileNames(command.AnalyzerConfigPaths).Should().ContainSingle().Which.Should().Be(".editorconfig");
        command.PreprocessorSymbolNames.Should().BeEquivalentTo("TRACE", "DEBUG", "NET8_0");
    }

    [Test]
    public void Embeds_all_sources_when_requested()
    {
        const string commandLine =
            "/usr/local/share/dotnet/dotnet exec \"/usr/local/share/dotnet/sdk/8.0.101/Roslyn/bincore/csc.dll\" /embed";

        var taskInputs = Inputs(
            ("Sources", Items("Program.cs")),
            ("EmbeddedFiles", Items("extra.cs")));

        CompilerCommand? command = CompilerCommandBuilder.Build(CompilerLanguage.CSharp, "/proj", commandLine, taskInputs);

        FileNames(command!.EmbeddedFiles).Should().BeEquivalentTo("extra.cs", "Program.cs");
    }

    [Test]
    public void Builds_reference_aliases_from_metadata()
    {
        var references = new List<CompilerInputItem>
        {
            new("/refs/Aliased.dll", [("Aliases", "global,Foo,Bar")]),
            new("/refs/GlobalOnly.dll", [("Aliases", "global")]),
            new("/refs/Plain.dll", CompilerInputItem.NoMetadata),
        };
        var taskInputs = Inputs(("References", references));

        CompilerCommand? command = CompilerCommandBuilder.Build(CompilerLanguage.CSharp, "/proj", null, taskInputs);

        // Reference paths are reported canonicalized (see Canonicalizes_input_paths), so the alias map is
        // keyed by the same canonical path that MetadataReferences reports.
        string aliased = Path.GetFullPath("/refs/Aliased.dll");
        command!.MetadataReferences.Should().Contain(aliased);
        command.Aliases.Should().ContainKey(aliased);

        // 'global' next to another alias keeps the assembly in the global namespace too, so it is kept.
        command.Aliases[aliased].Should().BeEquivalentTo("global", "Foo", "Bar");

        // On its own it is the ordinary unaliased reference.
        command.Aliases.Should().NotContainKey(Path.GetFullPath("/refs/GlobalOnly.dll"));
        command.Aliases.Should().NotContainKey(Path.GetFullPath("/refs/Plain.dll"));
    }

    [Test]
    public void Canonicalizes_input_paths()
    {
        // Items pulled in by package build logic routinely carry un-normalized segments, e.g. a
        // "<pkg>/build/../stylecop.json" AdditionalFiles include; the compiler (and MSBuildWorkspace) report
        // such inputs rooted and collapsed, and consumers key documents and references by path.
        var taskInputs = Inputs(
            ("Sources", Items("../Shared/Linked.cs", "./Program.cs")),
            ("References", Items("/refs/../lib/System.dll")),
            ("Analyzers", Items("/analyzers/./Some.Analyzer.dll")),
            ("AdditionalFiles", Items("/pkg/build/../stylecop.json")),
            ("AnalyzerConfigFiles", Items("cfg/../.editorconfig")),
            ("EmbeddedFiles", Items("res/../extra.cs")));

        CompilerCommand? command = CompilerCommandBuilder.Build(CompilerLanguage.CSharp, "/proj", null, taskInputs);

        command!.SourceFiles.Should().BeEquivalentTo(Path.GetFullPath("/Shared/Linked.cs"), Path.GetFullPath("/proj/Program.cs"));
        command.MetadataReferences.Should().BeEquivalentTo(Path.GetFullPath("/lib/System.dll"));
        command.AnalyzerReferences.Should().BeEquivalentTo(Path.GetFullPath("/analyzers/Some.Analyzer.dll"));
        command.AdditionalFiles.Should().BeEquivalentTo(Path.GetFullPath("/pkg/stylecop.json"));
        command.AnalyzerConfigPaths.Should().BeEquivalentTo(Path.GetFullPath("/proj/.editorconfig"));
        command.EmbeddedFiles.Should().BeEquivalentTo(Path.GetFullPath("/proj/extra.cs"));
    }

    [Test]
    public void Builds_visual_basic_symbols_from_define_switch_with_synthesized_symbols()
    {
        const string commandLine =
            "/usr/local/share/dotnet/dotnet exec \"/usr/local/share/dotnet/sdk/8.0.200/Roslyn/bincore/vbc.dll\" "
            + "/noconfig /define:\"CONFIG=\\\"Debug\\\",DEBUG=-1,TRACE=-1,NET6_0=-1,_MyType=\\\"Empty\\\"\"";

        CompilerCommand? command = CompilerCommandBuilder.Build(CompilerLanguage.VisualBasic, "/proj", commandLine, Inputs());

        command!.Language.Should().Be(CompilerLanguage.VisualBasic);
        command.PreprocessorSymbolNames.Should().BeEquivalentTo(
            "CONFIG", "DEBUG", "TRACE", "NET6_0", "_MyType", "VBC_VER", "TARGET");
    }

    [Test]
    public void Builds_fsharp_symbols_from_individual_define_switches()
    {
        const string commandLine = """
            /usr/local/share/dotnet/dotnet "/usr/local/share/dotnet/sdk/8.0.200/FSharp/fsc.dll"
            --define:TRACE
            --define:DEBUG
            --define:NETCOREAPP
            """;

        var taskInputs = Inputs(
            ("Sources", Items("Program.fs")),
            ("References", Items("/refs/FSharp.Core.dll")));

        CompilerCommand? command = CompilerCommandBuilder.Build(CompilerLanguage.FSharp, "/proj", commandLine, taskInputs);

        command!.Language.Should().Be(CompilerLanguage.FSharp);
        FileNames(command.SourceFiles).Should().ContainSingle().Which.Should().Be("Program.fs");
        command.MetadataReferences.Select(Path.GetFileName).Should().ContainSingle().Which.Should().Be("FSharp.Core.dll");
        command.PreprocessorSymbolNames.Should().BeEquivalentTo("TRACE", "DEBUG", "NETCOREAPP");
    }

    private static IEnumerable<string> FileNames(IEnumerable<string> paths)
        => paths.Select(Path.GetFileName);

    private static List<CompilerInputItem> Items(params string[] specs)
        => [.. specs.Select(s => new CompilerInputItem(s, CompilerInputItem.NoMetadata))];

    private static Dictionary<string, List<CompilerInputItem>> Inputs(params (string Key, List<CompilerInputItem> Items)[] groups)
        => groups.ToDictionary(g => g.Key, g => g.Items, StringComparer.OrdinalIgnoreCase);
}
