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
    public void Builds_reference_aliases_from_metadata_and_filters_global()
    {
        var references = new List<CompilerInputItem>
        {
            new("/refs/Aliased.dll", [("Aliases", "global,Foo,Bar")]),
            new("/refs/Plain.dll", CompilerInputItem.NoMetadata),
        };
        var taskInputs = Inputs(("References", references));

        CompilerCommand? command = CompilerCommandBuilder.Build(CompilerLanguage.CSharp, "/proj", null, taskInputs);

        command!.Aliases.Should().ContainKey("/refs/Aliased.dll");
        command.Aliases["/refs/Aliased.dll"].Should().BeEquivalentTo("Foo", "Bar");
        command.Aliases.Should().NotContainKey("/refs/Plain.dll");
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
