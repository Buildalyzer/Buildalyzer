using System.Collections.Immutable;
using Microsoft.Build.Utilities.ProjectCreation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Buildalyzer.Differential.Tests;

/// <summary>
/// Differential tests: author a project with <c>MSBuild.ProjectCreation</c>, then load it
/// with both Buildalyzer and Roslyn's <see cref="Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace"/>
/// and assert the two Roslyn projects agree. MSBuildWorkspace is the reference.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Differential_specs
{
    private const string TargetFramework = "net10.0";

    [Test]
    public async Task Class_library_matches_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "ClassLibrary",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Class1.cs", "namespace ClassLibrary;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);
        comparison.Buildalyzer.Language.Should().Be(comparison.MSBuild.Language);
        comparison.Buildalyzer.SourceFilePaths().Should().BeEquivalentTo(comparison.MSBuild.SourceFilePaths());
        comparison.Buildalyzer.MetadataReferencePaths().Should().BeEquivalentTo(comparison.MSBuild.MetadataReferencePaths());
        comparison.Buildalyzer.CompilationOptions!.OutputKind.Should().Be(comparison.MSBuild.CompilationOptions!.OutputKind);
        comparison.Buildalyzer.PreprocessorSymbols().Should().BeEquivalentTo(comparison.MSBuild.PreprocessorSymbols());

        // And everything else in one go: identity, output paths, every document and reference
        // with its metadata, and every compilation/parse option.
        comparison.Buildalyzer.Shape().Should().BeEquivalentTo(comparison.MSBuild.Shape());
    }

    [Test]
    public async Task Console_application_output_kind_matches_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "ConsoleApp",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("OutputType", "Exe"),
            Source("Program.cs", "System.Console.WriteLine(\"hi\");\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);
        comparison.MSBuild.CompilationOptions!.OutputKind.Should().Be(OutputKind.ConsoleApplication);
        comparison.Buildalyzer.CompilationOptions!.OutputKind.Should().Be(comparison.MSBuild.CompilationOptions!.OutputKind);
    }

    [Test]
    public async Task Compilation_options_match_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "OptionsProject",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("AllowUnsafeBlocks", "true")
                .Property("CheckForOverflowUnderflow", "true")
                .Property("Nullable", "enable")
                .Property("PlatformTarget", "x64"),
            Source("Class1.cs", "namespace OptionsProject;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);
        CSharpCompilationOptions actual = comparison.Buildalyzer.CSharpOptions();
        CSharpCompilationOptions reference = comparison.MSBuild.CSharpOptions();

        actual.Should().BeEquivalentTo(new
        {
            reference.AllowUnsafe,
            reference.CheckOverflow,
            reference.NullableContextOptions,
            reference.Platform,
        });

        // Every other scalar option too (module/main type, signing, determinism, concurrency,
        // metadata import, diagnostic configuration, ...), and the parse options alongside.
        comparison.Buildalyzer.Shape().CompilationOptions.Should().BeEquivalentTo(comparison.MSBuild.Shape().CompilationOptions);
        comparison.Buildalyzer.Shape().ParseOptions.Should().BeEquivalentTo(comparison.MSBuild.Shape().ParseOptions);
    }

    [Test]
    public async Task Language_version_and_defines_match_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "LangProject",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("LangVersion", "13.0")
                .Property("DefineConstants", "FOO;BAR"),
            Source("Class1.cs", "namespace LangProject;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);
        comparison.Buildalyzer.CSharpParse().LanguageVersion
            .Should().Be(comparison.MSBuild.CSharpParse().LanguageVersion);
        comparison.Buildalyzer.PreprocessorSymbols()
            .Should().BeEquivalentTo(comparison.MSBuild.PreprocessorSymbols());
    }

    [Test]
    public async Task Additional_files_match_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "AdditionalFilesProject",
            p => p.Property("TargetFramework", TargetFramework),
            new Dictionary<string, string>
            {
                ["Class1.cs"] = "namespace AdditionalFilesProject;\npublic class Class1 { }\n",
                ["message.txt"] = "hello\n",
            });
        ProjectFixture.AddItem(projectPath, "AdditionalFiles", "message.txt");
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);
        comparison.Buildalyzer.AdditionalDocumentPaths()
            .Should().BeEquivalentTo(comparison.MSBuild.AdditionalDocumentPaths());
    }

    [Test]
    public async Task Package_reference_metadata_matches_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "PackageProject",
            p => p
                .Property("TargetFramework", TargetFramework)
                .ItemPackageReference("Newtonsoft.Json", "13.0.3"),
            Source("Class1.cs", "public class Class1 { public Newtonsoft.Json.Linq.JObject? O; }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);
        comparison.Buildalyzer.MetadataReferenceNames().Should().Contain("Newtonsoft.Json.dll");
        comparison.Buildalyzer.MetadataReferencePaths()
            .Should().BeEquivalentTo(comparison.MSBuild.MetadataReferencePaths());
    }

    [Test]
    public async Task Project_reference_matches_reference()
    {
        using ProjectFixture fixture = new();
        string libraryPath = fixture.AddProject(
            "Library",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Widget.cs", "namespace Library;\npublic class Widget { }\n"));
        string appPath = fixture.AddProject(
            "App",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Program.cs", "_ = new Library.Widget();\n"));
        ProjectFixture.AddProjectReference(appPath, libraryPath);
        fixture.Restore(appPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(appPath);

        AssertLoadedCleanly(comparison);
        comparison.Buildalyzer.ProjectReferenceNames().Should().Contain("Library.csproj");
        comparison.Buildalyzer.ProjectReferencePaths()
            .Should().BeEquivalentTo(comparison.MSBuild.ProjectReferencePaths());
    }

    [Test(Description = "The project-reference graph does not depend on the order projects enter the workspace")]
    public async Task Project_reference_graph_matches_reference_regardless_of_load_order()
    {
        using ProjectFixture fixture = new();
        string libraryPath = fixture.AddProject(
            "Library",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Widget.cs", "namespace Library;\npublic class Widget { }\n"));
        string appPath = fixture.AddProject(
            "App",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Consumer.cs", "namespace App;\npublic class Consumer { public Library.Widget? Widget; }\n"));
        ProjectFixture.AddProjectReference(appPath, libraryPath);
        fixture.Restore(appPath);

        // Reference: MSBuildWorkspace wires a consumer opened later to the dependency already open, so
        // the dependency-first graph is the one to match. It has no consumer-first operation at all:
        // opening the consumer pulls the dependency in with it, and opening the dependency afterwards
        // is refused. Both are pinned here so a change in either shows up.
        (string Project, ProjectReferenceShape[] References)[] expected = await OpenWithMSBuildWorkspaceAsync(libraryPath, appPath);
        expected.Should().ContainSingle(p => p.Project == "App").Which.References
            .Should().ContainSingle().Which.TargetName.Should().Be("Library");
        await FluentActions.Awaiting(() => OpenWithMSBuildWorkspaceAsync(appPath, libraryPath))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*already part of the workspace*");

        // Buildalyzer: results already in hand, added directly - the documented alternative to
        // addProjectReferences - in both orders. Its API allows the consumer-first order MSBuildWorkspace
        // refuses, so the outcome is held to the same graph: a consumer added before its dependency is
        // wired to it when the dependency arrives.
        SafeStringWriter log = new();
        AnalyzerManager manager = new(new AnalyzerManagerOptions { LogWriter = log });
        IAnalyzerResult library = manager.GetProject(libraryPath)!.Build().First();
        IAnalyzerResult app = manager.GetProject(appPath)!.Build().First();

        using AdhocWorkspace dependencyFirst = new();
        library.AddToWorkspace(dependencyFirst);
        app.AddToWorkspace(dependencyFirst);
        Graph(dependencyFirst.CurrentSolution).Should().BeEquivalentTo(expected, log.ToString());

        using AdhocWorkspace consumerFirst = new();
        app.AddToWorkspace(consumerFirst);
        library.AddToWorkspace(consumerFirst);
        Graph(consumerFirst.CurrentSolution).Should().BeEquivalentTo(expected, log.ToString());

        Compilation compilation = (await consumerFirst.CurrentSolution.Projects.Single(p => p.Name == "App").GetCompilationAsync())!;
        compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty(log.ToString());
    }

    // Opens the projects one after another in a single MSBuildWorkspace and returns the resulting
    // project-reference graph.
    private static async Task<(string Project, ProjectReferenceShape[] References)[]> OpenWithMSBuildWorkspaceAsync(params string[] projectPaths)
    {
        using Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace workspace = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create();
        List<string> failures = [];
        using (workspace.RegisterWorkspaceFailedHandler(e => failures.Add(e.Diagnostic.Message)))
        {
            foreach (string projectPath in projectPaths)
            {
                await workspace.OpenProjectAsync(projectPath);
            }
        }

        failures.Should().BeEmpty();
        return Graph(workspace.CurrentSolution);
    }

    private static (string Project, ProjectReferenceShape[] References)[] Graph(Solution solution) =>
        [.. solution.Shape().Select(p => (p.Name, p.ProjectReferences))];

    [Test]
    public async Task Implicit_sdk_analyzers_match_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "AnalyzerProject",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Class1.cs", "namespace AnalyzerProject;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);

        // The .NET SDK injects a fixed set of analyzers and source generators; both loaders
        // should surface exactly the same set.
        comparison.MSBuild.AnalyzerReferenceNames().Should().NotBeEmpty();
        comparison.Buildalyzer.AnalyzerReferencePaths()
            .Should().BeEquivalentTo(comparison.MSBuild.AnalyzerReferencePaths());
    }

    [Test]
    public async Task Effective_language_version_and_warning_settings_match_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "EffectiveSettingsProject",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("LangVersion", "latest")
                .Property("Deterministic", "true"),
            Source("Class1.cs", "namespace EffectiveSettingsProject;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);

        // "latest" must resolve to the same concrete language version the compiler used.
        comparison.Buildalyzer.CSharpParse().LanguageVersion
            .Should().Be(comparison.MSBuild.CSharpParse().LanguageVersion);
        comparison.Buildalyzer.CSharpOptions().WarningLevel
            .Should().Be(comparison.MSBuild.CSharpOptions().WarningLevel);
        comparison.Buildalyzer.CSharpOptions().Deterministic
            .Should().Be(comparison.MSBuild.CSharpOptions().Deterministic);
    }

    [Test]
    public async Task Package_delivered_source_generator_matches_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "GeneratorProject",
            p => p
                .Property("TargetFramework", TargetFramework)
                .ItemPackageReference("Riok.Mapperly", "4.3.1"),
            Source("Class1.cs", "namespace GeneratorProject;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);

        // Analyzers/generators delivered by a NuGet package are resolved through a different
        // MSBuild path (ResolvePackageAssets) than the implicit SDK analyzers.
        comparison.Buildalyzer.AnalyzerReferenceNames().Should().Contain("Riok.Mapperly.dll");
        comparison.Buildalyzer.AnalyzerReferencePaths()
            .Should().BeEquivalentTo(comparison.MSBuild.AnalyzerReferencePaths());

        // Every document collection should agree, including the analyzer-config documents that
        // carry the build_property.* values a source generator reads at run time.
        comparison.Buildalyzer.SourceFilePaths().Should().BeEquivalentTo(comparison.MSBuild.SourceFilePaths());
        comparison.Buildalyzer.AdditionalDocumentPaths().Should().BeEquivalentTo(comparison.MSBuild.AdditionalDocumentPaths());
        comparison.Buildalyzer.AnalyzerConfigDocumentNames().Should().Contain("GeneratorProject.GeneratedMSBuildEditorConfig.editorconfig");
        comparison.Buildalyzer.AnalyzerConfigDocumentPaths()
            .Should().BeEquivalentTo(comparison.MSBuild.AnalyzerConfigDocumentPaths());
    }

    [Test]
    public async Task Embed_interop_types_reference_matches_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "EmbedProject",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Class1.cs", "namespace EmbedProject;\npublic class Class1 { }\n"));

        // Copy a real assembly next to the project and reference it with EmbedInteropTypes=true. The
        // design-time build carries the metadata through without the compiler running (which is where a
        // non-PIA would actually be rejected), so this exercises the plumbing cross-platform.
        string source = typeof(Microsoft.Build.Locator.MSBuildLocator).Assembly.Location;
        File.Copy(source, Path.Combine(Path.GetDirectoryName(projectPath)!, "Embedded.dll"));
        ProjectFixture.AddItem(projectPath, "Reference", "Embedded", new Dictionary<string, string>
        {
            ["HintPath"] = "Embedded.dll",
            ["EmbedInteropTypes"] = "true",
            ["Private"] = "false",
        });
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        string[] ms = EmbedInteropReferences(comparison.MSBuild);
        string[] ba = EmbedInteropReferences(comparison.Buildalyzer);
        ms.Should().Contain("Embedded.dll", "the reference is marked EmbedInteropTypes");
        ba.Should().BeEquivalentTo(ms);
    }

    private static string[] EmbedInteropReferences(Project project) =>
    [
        .. project.MetadataReferences.OfType<PortableExecutableReference>()
            .Where(r => r.Properties.EmbedInteropTypes && r.FilePath is not null)
            .Select(r => Path.GetFileName(r.FilePath!))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
    ];

    [Test]
    public async Task Visual_basic_class_library_matches_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "VbLibrary",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Widget.vb", "Namespace VbLibrary\n    Public Class Widget\n    End Class\nEnd Namespace\n"),
            extension: ".vbproj");
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);
        comparison.Buildalyzer.Language.Should().Be(LanguageNames.VisualBasic);
        comparison.Buildalyzer.Language.Should().Be(comparison.MSBuild.Language);
        comparison.Buildalyzer.SourceFilePaths().Should().BeEquivalentTo(comparison.MSBuild.SourceFilePaths());
        comparison.Buildalyzer.MetadataReferencePaths().Should().BeEquivalentTo(comparison.MSBuild.MetadataReferencePaths());
        comparison.Buildalyzer.CompilationOptions!.OutputKind.Should().Be(comparison.MSBuild.CompilationOptions!.OutputKind);
        WithoutVbPreprocessorSymbols(comparison.Buildalyzer.Shape())
            .Should().BeEquivalentTo(WithoutVbPreprocessorSymbols(comparison.MSBuild.Shape()));
    }

    [Test]
    public async Task Visual_basic_options_match_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "VbOptions",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("RootNamespace", "Custom.Vb")
                .Property("OptionStrict", "On")
                .Property("OptionExplicit", "Off")
                .Property("OptionInfer", "On")
                .Property("OptionCompare", "Text")
                .Property("DefineConstants", "MY_FLAG=True,MY_NUMBER=42")
                .Property("GenerateDocumentationFile", "true"),
            Source("Widget.vb", "Public Class Widget\nEnd Class\n"),
            extension: ".vbproj");
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        // VB-only options (Option Strict/Explicit/Infer/Compare, root namespace, the SDK's global
        // imports) and VB's valued preprocessor symbols travel via vbc's command line; both loaders
        // must agree on every one of them.
        SortedDictionary<string, string?> ms = comparison.MSBuild.Shape().CompilationOptions;
        ms.Should().Contain("OptionStrict", "On").And.Contain("OptionExplicit", "False").And.Contain("OptionCompareText", "True");
        ms["GlobalImports"].Should().Contain("Microsoft.VisualBasic");
        comparison.Buildalyzer.Shape().CompilationOptions.Should().BeEquivalentTo(ms);

        // The defines come from vbc's real command line on the Buildalyzer side (see
        // WithoutVbPreprocessorSymbols for why the reference cannot be used for them).
        ProjectShape ba = comparison.Buildalyzer.Shape();
        ba.ParseOptions["PreprocessorSymbolValues"].Should().Contain("MY_FLAG=True").And.Contain("MY_NUMBER=42").And.Contain("NET10_0=-1");
        WithoutVbPreprocessorSymbols(ba).ParseOptions
            .Should().BeEquivalentTo(WithoutVbPreprocessorSymbols(comparison.MSBuild.Shape()).ParseOptions);
    }

    /// <summary>
    /// MSBuildWorkspace loses VB preprocessor symbols: for a VB project it reports only the compiler's own
    /// implicit symbols (<c>TARGET</c>, <c>VBC_VER</c>), none of the <c>/define</c> values vbc actually
    /// receives (<c>CONFIG</c>, <c>DEBUG</c>, the <c>NETx_y</c> family, the project's own
    /// <c>DefineConstants</c>). Its property-based fallback reads <c>FinalDefineConstants</c>, which the SDK's
    /// design-time build leaves unset. Buildalyzer reads the real command line and has them all, so the
    /// reference cannot be the oracle here: VB shape comparisons drop the symbols and assert Buildalyzer's
    /// directly instead.
    /// </summary>
    private static ProjectShape WithoutVbPreprocessorSymbols(ProjectShape shape)
    {
        if (shape.Language != LanguageNames.VisualBasic)
        {
            return shape;
        }

        SortedDictionary<string, string?> parse = new(shape.ParseOptions, StringComparer.Ordinal);
        parse.Remove("PreprocessorSymbols");
        parse.Remove("PreprocessorSymbolValues");
        return shape with { ParseOptions = parse };
    }

    [Test]
    public async Task Implicit_usings_generated_file_matches_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "ImplicitUsingsProject",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("ImplicitUsings", "enable"),
            Source("Class1.cs", "namespace ImplicitUsingsProject;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);

        // ImplicitUsings makes the SDK emit an <Assembly>.GlobalUsings.g.cs Compile item.
        comparison.MSBuild.SourceFileNames()
            .Should().Contain(x => x.EndsWith("GlobalUsings.g.cs", StringComparison.Ordinal));
        comparison.Buildalyzer.SourceFilePaths()
            .Should().BeEquivalentTo(comparison.MSBuild.SourceFilePaths());
    }

    [Test]
    public async Task Transitive_project_references_match_reference()
    {
        using ProjectFixture fixture = new();
        string leaf = fixture.AddProject(
            "Leaf",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Leaf.cs", "namespace Leaf;\npublic class Thing { }\n"));
        string middle = fixture.AddProject(
            "Middle",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Middle.cs", "namespace Middle;\npublic class Thing { public Leaf.Thing? Ref; }\n"));
        string top = fixture.AddProject(
            "Top",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Top.cs", "namespace Top;\npublic class Thing { public Middle.Thing? Ref; }\n"));
        ProjectFixture.AddProjectReference(middle, leaf);
        ProjectFixture.AddProjectReference(top, middle);
        fixture.Restore(top);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(top);

        AssertLoadedCleanly(comparison);
        comparison.Buildalyzer.SolutionProjectPaths()
            .Should().BeEquivalentTo(comparison.MSBuild.SolutionProjectPaths());
        comparison.Buildalyzer.SolutionProjectNames()
            .Should().BeEquivalentTo("Top.csproj", "Middle.csproj", "Leaf.csproj");
    }

    [Test]
    public async Task Linked_source_file_outside_project_matches_reference()
    {
        using ProjectFixture fixture = new();

        // A source file that physically lives outside the project directory, pulled in through
        // an explicit (linked) Compile item rather than the SDK's implicit globbing.
        File.WriteAllText(Path.Combine(fixture.Root.FullName, "Shared.cs"), "namespace Shared;\npublic class Shared { }\n");

        string projectPath = fixture.AddProject(
            "LinkedFileProject",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Class1.cs", "namespace LinkedFileProject;\npublic class Class1 { }\n"));
        ProjectFixture.AddItem(projectPath, "Compile", @"..\Shared.cs");
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);
        comparison.MSBuild.SourceFileNames().Should().Contain("Shared.cs");
        comparison.Buildalyzer.SourceFilePaths()
            .Should().BeEquivalentTo(comparison.MSBuild.SourceFilePaths());
    }

    [TestCase("net8.0")]
    [TestCase("net10.0")]
    public async Task Multi_targeted_project_matches_reference_per_framework(string targetFramework)
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "MultiTarget",
            p => p.Property("TargetFrameworks", "net8.0;net10.0"),
            Source("Class1.cs", "namespace MultiTarget;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath, targetFramework);

        AssertLoadedCleanly(comparison);

        // Sources are shared, but references and preprocessor symbols are framework-specific
        // (e.g. NET8_0 vs NET10_0), so this checks Buildalyzer builds the right target.
        comparison.Buildalyzer.SourceFilePaths().Should().BeEquivalentTo(comparison.MSBuild.SourceFilePaths());
        comparison.Buildalyzer.MetadataReferencePaths().Should().BeEquivalentTo(comparison.MSBuild.MetadataReferencePaths());
        comparison.Buildalyzer.PreprocessorSymbols().Should().BeEquivalentTo(comparison.MSBuild.PreprocessorSymbols());

        // Roslyn names each flavour "<Project>(<tfm>)" (no space); everything else must match too.
        comparison.MSBuild.Name.Should().Be($"MultiTarget({targetFramework})");
        comparison.Buildalyzer.Shape().Should().BeEquivalentTo(comparison.MSBuild.Shape());
    }

    [Test]
    public async Task Editorconfig_is_surfaced_as_analyzer_config_document()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "EditorConfigProject",
            p => p.Property("TargetFramework", TargetFramework),
            new Dictionary<string, string>
            {
                ["Class1.cs"] = "namespace EditorConfigProject;\npublic class Class1 { }\n",
                [".editorconfig"] = "root = true\n\n[*.cs]\ndotnet_diagnostic.CA1822.severity = warning\n",
            });
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);
        comparison.MSBuild.AnalyzerConfigDocumentNames().Should().Contain(".editorconfig");
        comparison.Buildalyzer.AnalyzerConfigDocumentPaths()
            .Should().BeEquivalentTo(comparison.MSBuild.AnalyzerConfigDocumentPaths());
    }

    [Test]
    public async Task Assembly_identity_and_compilation_flags_match_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "IdentityProject",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("AssemblyName", "CustomAssembly")
                .Property("RootNamespace", "Custom.Root")
                .Property("Optimize", "true")
                .Property("TreatWarningsAsErrors", "true")
                .Property("GenerateDocumentationFile", "true"),
            Source("Class1.cs", "namespace Custom.Root;\n/// <summary>A.</summary>\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);
        comparison.Buildalyzer.AssemblyName.Should().Be(comparison.MSBuild.AssemblyName).And.Be("CustomAssembly");
        comparison.Buildalyzer.DefaultNamespace.Should().Be(comparison.MSBuild.DefaultNamespace).And.Be("Custom.Root");
        comparison.Buildalyzer.CSharpOptions().OptimizationLevel
            .Should().Be(comparison.MSBuild.CSharpOptions().OptimizationLevel);
        comparison.Buildalyzer.CSharpOptions().GeneralDiagnosticOption
            .Should().Be(comparison.MSBuild.CSharpOptions().GeneralDiagnosticOption);
        comparison.Buildalyzer.CSharpParse().DocumentationMode
            .Should().Be(comparison.MSBuild.CSharpParse().DocumentationMode);
    }

    [Test]
    public async Task Document_contents_match_reference()
    {
        const string source = "namespace Contents;\npublic class Class1 { public string S = \"café ☕\"; }\n";

        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "ContentsProject",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Class1.cs", source));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);

        // Reading the document text (not just the file name) catches encoding/loader mistakes.
        string buildalyzerText = await DocumentText(comparison.Buildalyzer, "Class1.cs");
        string msbuildText = await DocumentText(comparison.MSBuild, "Class1.cs");
        buildalyzerText.Should().Be(msbuildText).And.Contain("café ☕");
    }

    [Test]
    public async Task Specific_diagnostic_options_match_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "DiagnosticOptionsProject",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("NoWarn", "CA1822;CS0219"),
            Source("Class1.cs", "namespace DiagnosticOptionsProject;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);
        comparison.Buildalyzer.CSharpOptions().SpecificDiagnosticOptions
            .Should().BeEquivalentTo(comparison.MSBuild.CSharpOptions().SpecificDiagnosticOptions);
    }

    [Test]
    public async Task Source_checksum_algorithm_matches_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "ChecksumProject",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Class1.cs", "namespace ChecksumProject;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        Microsoft.CodeAnalysis.Text.SourceText ba = await DocumentSource(comparison.Buildalyzer, "Class1.cs");
        Microsoft.CodeAnalysis.Text.SourceText ms = await DocumentSource(comparison.MSBuild, "Class1.cs");
        ba.ChecksumAlgorithm.Should().Be(ms.ChecksumAlgorithm);
    }

    [Test]
    public async Task Source_encoding_matches_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "EncodingProject",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Class1.cs", "namespace EncodingProject;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        string? ba = (await DocumentSource(comparison.Buildalyzer, "Class1.cs")).Encoding?.WebName;
        string? ms = (await DocumentSource(comparison.MSBuild, "Class1.cs")).Encoding?.WebName;
        ba.Should().Be(ms);
    }

    private static async Task<Microsoft.CodeAnalysis.Text.SourceText> DocumentSource(Project project, string fileName)
    {
        Document document = project.Documents.Single(
            d => string.Equals(Path.GetFileName(d.FilePath), fileName, StringComparison.OrdinalIgnoreCase));
        return await document.GetTextAsync();
    }

    private static async Task<string> DocumentText(Project project, string fileName)
    {
        Document document = project.Documents.Single(
            d => string.Equals(Path.GetFileName(d.FilePath), fileName, StringComparison.OrdinalIgnoreCase));
        return (await document.GetTextAsync()).ToString();
    }

    [Test]
    public async Task Compilation_services_match_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "ServicesProject",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Class1.cs", "namespace ServicesProject;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        CompilationOptions ba = comparison.Buildalyzer.CompilationOptions!;
        CompilationOptions ms = comparison.MSBuild.CompilationOptions!;

        // MSBuildWorkspace attaches these to every project; the command-line parser leaves them null.
        // (The metadata reference resolver it uses is internal to Roslyn and only affects #r, so we
        // deliberately do not match that one.)
        ba.XmlReferenceResolver!.GetType().Should().Be(ms.XmlReferenceResolver!.GetType());
        ba.SourceReferenceResolver!.GetType().Should().Be(ms.SourceReferenceResolver!.GetType());
        ba.StrongNameProvider!.GetType().Should().Be(ms.StrongNameProvider!.GetType());
        ba.AssemblyIdentityComparer.GetType().Should().Be(ms.AssemblyIdentityComparer.GetType());
    }

    [Test]
    public async Task Signed_assembly_emits_from_workspace()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "SignedProject",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("SignAssembly", "true")
                .Property("AssemblyOriginatorKeyFile", "key.snk"),
            Source("Class1.cs", "namespace SignedProject;\npublic class Class1 { }\n"));
        StrongNameKey.Write(Path.Combine(Path.GetDirectoryName(projectPath)!, "key.snk"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        // Emitting a strong-named assembly needs a StrongNameProvider; without it Emit fails. Both
        // workspaces should produce a signed assembly.
        (await Emits(comparison.MSBuild)).Should().BeTrue("the reference workspace should emit a signed assembly");
        (await Emits(comparison.Buildalyzer)).Should().BeTrue("Buildalyzer's workspace should emit a signed assembly");
    }

    private static async Task<bool> Emits(Project project)
    {
        Compilation compilation = await project.GetCompilationAsync();
        using MemoryStream stream = new();
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(stream);
        return result.Success
            && compilation.Assembly.Identity.PublicKey.Length > 0;
    }

    [Test]
    public async Task Editorconfig_severity_flows_into_compiler_diagnostics()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "SeverityProject",
            p => p.Property("TargetFramework", TargetFramework),
            new Dictionary<string, string>
            {
                // 'unused' triggers CS0219 (assigned but never used), normally a warning.
                ["Class1.cs"] = "namespace SeverityProject;\npublic class Class1 { public int M() { int unused = 1; return 2; } }\n",
                [".editorconfig"] = "root = true\n\n[*.cs]\ndotnet_diagnostic.CS0219.severity = error\n",
            });
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        (string Id, DiagnosticSeverity Severity)[] ms = await CompilerDiagnostics(comparison.MSBuild);
        (string Id, DiagnosticSeverity Severity)[] ba = await CompilerDiagnostics(comparison.Buildalyzer);

        // The .editorconfig must promote CS0219 to an error, and Buildalyzer's compilation must
        // produce the exact same set of compiler diagnostics as the reference.
        ms.Should().Contain(("CS0219", DiagnosticSeverity.Error), "the reference applies the editorconfig severity");
        ba.Should().BeEquivalentTo(ms);
    }

    [Test]
    public async Task Visual_basic_option_strict_diagnostics_match_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "VbStrict",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("OptionStrict", "On"),
            Source("Widget.vb", "Namespace VbStrict\n    Public Class Widget\n        Public Sub M()\n            Dim x As Integer = 1.5\n        End Sub\n    End Class\nEnd Namespace\n"),
            extension: ".vbproj");
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        // Option Strict On makes the narrowing conversion an error (BC30512); both must agree.
        (string Id, DiagnosticSeverity Severity)[] ms = await CompilerDiagnostics(comparison.MSBuild);
        (string Id, DiagnosticSeverity Severity)[] ba = await CompilerDiagnostics(comparison.Buildalyzer);

        // Both workspaces must apply Option Strict (BC30512 on the narrowing conversion).
        ms.Should().Contain(("BC30512", DiagnosticSeverity.Error), "Option Strict On disallows the implicit narrowing");
        ba.Should().Contain(("BC30512", DiagnosticSeverity.Error));

        // NB: we do not require the full diagnostic sets to match here. For a .NET VB class library,
        // MSBuildWorkspace injects the VB "My" template and emits spurious BC30002 errors for
        // Microsoft.VisualBasic.ApplicationServices/Devices types that are not referenced - errors the
        // real compiler and Buildalyzer do not produce. Buildalyzer is the more faithful one in this case.
        ba.Should().NotContain(("BC30002", DiagnosticSeverity.Error));
    }

    private static async Task<(string Id, DiagnosticSeverity Severity)[]> CompilerDiagnostics(Project project)
    {
        Compilation compilation = await project.GetCompilationAsync();
        return [.. compilation.GetDiagnostics()
            .Where(d => d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
            .Select(d => (d.Id, d.Severity))
            .Distinct()
            .OrderBy(x => x.Id, StringComparer.Ordinal)];
    }

    [Test]
    public async Task Analyzer_diagnostics_match_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "AnalyzerDiagnosticsProject",
            p => p.Property("TargetFramework", TargetFramework),
            new Dictionary<string, string>
            {
                // M() uses no instance state, so the SDK analyzer CA1822 (mark as static) fires
                // once it is turned on via the editorconfig below.
                ["Class1.cs"] = "namespace AnalyzerDiagnosticsProject;\npublic class Class1 { public int M() => 2; }\n",
                [".editorconfig"] = "root = true\n\n[*.cs]\ndotnet_diagnostic.CA1822.severity = warning\n",
            });
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        (string Id, DiagnosticSeverity Severity)[] ms = await AnalyzerDiagnostics(comparison.MSBuild, "CA1822");
        (string Id, DiagnosticSeverity Severity)[] ba = await AnalyzerDiagnostics(comparison.Buildalyzer, "CA1822");

        // The analyzer must run and honour the editorconfig severity, identically on both sides.
        ms.Should().Contain(("CA1822", DiagnosticSeverity.Warning), "the reference runs the SDK analyzer");
        ba.Should().BeEquivalentTo(ms);
    }

    [Test]
    public async Task Missing_source_file_still_becomes_a_document()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "MissingSourceProject",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Class1.cs", "namespace MissingSourceProject;\npublic class Class1 { }\n"));

        // An explicit Compile item whose file does not exist on disk - the shape of an uninitialised
        // git submodule (issue #345). The design-time build skips the compiler, so the path flows
        // through to the workspace without failing the build.
        ProjectFixture.AddItem(projectPath, "Compile", "Missing.cs");
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        // The missing path is a document on both sides, not silently dropped.
        comparison.MSBuild.SourceFileNames().Should().Contain("Missing.cs", "the reference keeps missing paths as documents");
        comparison.Buildalyzer.SourceFilePaths().Should().BeEquivalentTo(comparison.MSBuild.SourceFilePaths());

        // The read fails lazily and identically on both sides: an empty document, not an exception.
        string ba = await DocumentText(comparison.Buildalyzer, "Missing.cs");
        string ms = await DocumentText(comparison.MSBuild, "Missing.cs");
        ba.Should().Be(ms);
        ba.Should().BeEmpty();
    }

    [Test]
    public async Task Missing_analyzer_reference_is_surfaced_unresolved()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "MissingAnalyzerProject",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Class1.cs", "namespace MissingAnalyzerProject;\npublic class Class1 { }\n"));

        // An analyzer path that does not exist on disk - the shape of a project-private analyzer
        // whose producing project has not been built yet (issue #345).
        ProjectFixture.AddItem(projectPath, "Analyzer", Path.Combine("analyzers", "NotBuilt.dll"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        // Both loaders surface the missing analyzer as an unresolved reference instead of dropping it.
        comparison.MSBuild.AnalyzerReferences.OfType<UnresolvedAnalyzerReference>()
            .Should().NotBeEmpty("the reference surfaces missing analyzers as unresolved");
        comparison.Buildalyzer.AnalyzerReferences.OfType<UnresolvedAnalyzerReference>()
            .Should().ContainSingle().Which.Display.Should().Contain("NotBuilt");

        // The resolved analyzers must match by full path. The unresolved one can only be matched by name:
        // MSBuildWorkspace keeps the raw command-line string for an analyzer it could not load (here the
        // relative "analyzers/NotBuilt.dll"), whereas Buildalyzer keeps the rooted path it was given.
        ResolvedAnalyzerPaths(comparison.Buildalyzer).Should().BeEquivalentTo(ResolvedAnalyzerPaths(comparison.MSBuild));
        comparison.Buildalyzer.AnalyzerReferenceNames()
            .Should().BeEquivalentTo(comparison.MSBuild.AnalyzerReferenceNames());
    }

    private static string[] ResolvedAnalyzerPaths(Project project) =>
    [
        .. project.AnalyzerReferences
            .OfType<AnalyzerFileReference>()
            .Select(r => Path.GetFullPath(r.FullPath))
            .OrderBy(x => x, StringComparer.Ordinal)
    ];

    [Test]
    public void Recovers_workspace_when_build_fails_before_compile()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "FailBeforeCompile",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("DefineConstants", "$(DefineConstants);CUSTOM_CONSTANT")
                .Target(name: "FailBeforeCompile", beforeTargets: "CoreCompile")
                .TaskError(text: "Simulated failure before Csc (#341)"),
            new Dictionary<string, string>
            {
                ["Class1.cs"] = "namespace FailBeforeCompile;\npublic class Class1 { }\n",
                [".editorconfig"] = "root = true\n\n[*.cs]\ndotnet_diagnostic.CA1822.severity = warning\n",
            });
        fixture.Restore(projectPath);

        SafeStringWriter log = new();
        AnalyzerManager manager = new(new AnalyzerManagerOptions { LogWriter = log });
        IProjectAnalyzer analyzer = manager.GetProject(projectPath);
        IAnalyzerResult result = analyzer.Build().First();

        // The build failed before the compiler ran, so CompilerCommand was never captured.
        result.Succeeded.Should().BeFalse(log.ToString());
        result.SourceFiles.Should().BeEmpty(log.ToString());
        result.AnalyzerConfigFiles.Should().BeEmpty(log.ToString());

        using AdhocWorkspace workspace = result.GetWorkspace();
        Project project = workspace.CurrentSolution.Projects.Single();

        // The workspace is reconstructed from evaluation-time items and the references resolved before
        // the failure, so documents, references and preprocessor symbols are all recovered (issue #341).
        project.SourceFileNames().Should().Contain("Class1.cs", log.ToString());
        project.MetadataReferenceNames().Should().Contain("System.Runtime.dll", log.ToString());
        project.PreprocessorSymbols().Should().Contain("CUSTOM_CONSTANT", log.ToString());

        // So are the analyzers the SDK resolved before the failure - the targeting pack's generators are
        // added by ResolveTargetingPackAssets, not at evaluation - and not only the evaluated ones.
        project.AnalyzerReferenceNames().Should().Contain("System.Text.Json.SourceGeneration.dll", log.ToString());

        // And the analyzer configuration: the SDK gathers the .editorconfig files above the Compile items at
        // evaluation (as the EditorConfigFiles items the compiler would have been handed), so the severities
        // and generator options they carry survive the failure too. The build-generated
        // GeneratedMSBuildEditorConfig.editorconfig, like the generated sources, does not.
        project.AnalyzerConfigDocumentNames().Should().Contain(".editorconfig", log.ToString());

        // The same recovery has to be reachable from the analyzer- and manager-level entry points, which
        // build the project themselves rather than being handed a result.
        using AdhocWorkspace fromAnalyzer = analyzer.GetWorkspace();
        Project analyzerProject = fromAnalyzer.CurrentSolution.Projects.Single();
        analyzerProject.SourceFileNames().Should().Contain("Class1.cs", log.ToString());

        using AdhocWorkspace fromManager = manager.GetWorkspace();
        Project managerProject = fromManager.CurrentSolution.Projects.Single();
        managerProject.SourceFileNames().Should().Contain("Class1.cs", log.ToString());
    }

    [Test]
    public async Task Analyzers_resolved_before_a_pre_compile_failure_match_reference()
    {
        // When the build stops before the compiler, MSBuildWorkspace reads the Analyzer items as they stood at
        // the failure, so the targeting-pack and package analyzers the SDK resolved by then are all present.
        // Buildalyzer must recover the same set from the resolution tasks' outputs, not just the evaluated
        // items (which hold only the SDK's own analyzers).
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "AnalyzersBeforeFailure",
            p => p
                .Property("TargetFramework", TargetFramework)
                .ItemPackageReference("Riok.Mapperly", "4.3.1")
                .Target(name: "FailBeforeCompile", beforeTargets: "CoreCompile")
                .TaskError(text: "Simulated failure before Csc (#341)"),
            Source("Class1.cs", "namespace AnalyzersBeforeFailure;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        comparison.MSBuildFailures.Should().ContainSingle(f => f.Contains("Simulated failure before Csc"));
        comparison.BuildalyzerLog.Should().Contain("No compiler invocation was captured");
        comparison.Buildalyzer.AnalyzerReferenceNames().Should().Contain(["Riok.Mapperly.dll", "System.Text.Json.SourceGeneration.dll"]);
        comparison.Buildalyzer.AnalyzerReferencePaths()
            .Should().BeEquivalentTo(comparison.MSBuild.AnalyzerReferencePaths(), comparison.BuildalyzerLog);
    }

    [Test]
    public async Task Single_framework_in_TargetFrameworks_is_named_without_discriminator()
    {
        // MSBuildWorkspace loads one project per entry of TargetFrameworks and appends "(tfm)" to the name only
        // when that yields more than one. A TargetFrameworks that lists a single framework therefore keeps the
        // bare name, exactly like a TargetFramework would.
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "SingleFrameworkList",
            p => p.Property("TargetFrameworks", TargetFramework),
            Source("Class1.cs", "namespace SingleFrameworkList;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);
        comparison.MSBuild.Name.Should().Be("SingleFrameworkList");
        comparison.Buildalyzer.Name.Should().Be(comparison.MSBuild.Name);
        comparison.Buildalyzer.Shape().Should().BeEquivalentTo(comparison.MSBuild.Shape(), comparison.BuildalyzerLog);
    }

    [Test]
    public async Task Project_overriding_inherited_TargetFrameworks_is_named_without_discriminator()
    {
        // A Directory.Build.props that multi-targets the tree, overridden by a project that sets its own
        // TargetFramework: the project builds once, so MSBuildWorkspace gives it the bare name even though
        // its evaluated TargetFrameworks still lists two frameworks.
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "OverridesInherited",
            p => p.Property("TargetFramework", TargetFramework),
            new Dictionary<string, string>
            {
                ["Class1.cs"] = "namespace OverridesInherited;\npublic class Class1 { }\n",
                ["Directory.Build.props"] = $"""
                    <Project>
                      <PropertyGroup>
                        <TargetFrameworks>{TargetFramework};net8.0</TargetFrameworks>
                      </PropertyGroup>
                    </Project>
                    """,
            });
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        AssertLoadedCleanly(comparison);
        comparison.MSBuild.Name.Should().Be("OverridesInherited");
        comparison.Buildalyzer.Solution.Projects.Should().ContainSingle();
        comparison.Buildalyzer.Name.Should().Be(comparison.MSBuild.Name);
    }

    [Test]
    public async Task Solution_projects_match_reference()
    {
        using ProjectFixture fixture = new();
        string libraryPath = fixture.AddProject(
            "Lib",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Widget.cs", "namespace Lib;\npublic class Widget { }\n"));
        string appPath = fixture.AddProject(
            "App",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Program.cs", "namespace App;\npublic class Program { Lib.Widget W = new(); }\n"));
        ProjectFixture.AddProjectReference(appPath, libraryPath);

        string solutionPath = Path.Combine(fixture.Root.FullName, "Solution.slnx");
        File.WriteAllText(solutionPath, "<Solution>\n  <Project Path=\"Lib/Lib.csproj\" />\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        fixture.Restore(appPath);

        // Buildalyzer loads the whole solution (parsed via SolutionPersistence).
        SafeStringWriter log = new();
        AnalyzerManager manager = new(solutionPath, new AnalyzerManagerOptions { LogWriter = log });
        using AdhocWorkspace buildalyzer = manager.GetWorkspace();
        string[] ba = SolutionProjectFileNames(buildalyzer.CurrentSolution);

        // MSBuildWorkspace loads the same solution as the reference.
        using Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace msbuild = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create();
        Solution msSolution = await msbuild.OpenSolutionAsync(solutionPath);
        string[] ms = SolutionProjectFileNames(msSolution);

        log.ToString().Should().NotContain("Workspace failed");
        ms.Should().BeEquivalentTo("App.csproj", "Lib.csproj");
        ba.Should().BeEquivalentTo(ms);
    }

    private static string[] SolutionProjectFileNames(Solution solution) =>
    [
        .. solution.Projects
            .Where(p => p.FilePath is not null)
            .Select(p => Path.GetFileName(p.FilePath!))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
    ];

    [Test]
    public async Task Project_reference_resolves_cross_project_symbols()
    {
        using ProjectFixture fixture = new();
        string libraryPath = fixture.AddProject(
            "Library",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Widget.cs", "namespace Library;\npublic class Widget { public int Value => 42; }\n"));
        string appPath = fixture.AddProject(
            "App",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Program.cs", "namespace App;\npublic class Program { public Library.Widget W = new(); }\n"));
        ProjectFixture.AddProjectReference(appPath, libraryPath);
        fixture.Restore(appPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(appPath);
        AssertLoadedCleanly(comparison);

        // A design-time build never produces Library.dll, so App can only bind Library.Widget if the
        // project reference is wired into the workspace as a compilation reference. The app should
        // compile without errors on both sides.
        (await CompilationErrors(comparison.MSBuild)).Should().BeEmpty("the reference resolves Library.Widget");
        (await CompilationErrors(comparison.Buildalyzer))
            .Should().BeEquivalentTo(await CompilationErrors(comparison.MSBuild));
    }

    [Test]
    public async Task Xml_documentation_include_resolves_from_workspace()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "XmlIncludeProject",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("GenerateDocumentationFile", "true"),
            new Dictionary<string, string>
            {
                ["Class1.cs"] =
                    "namespace XmlIncludeProject;\n"
                    + "public class Class1\n{\n"
                    + "    /// <include file='docs.xml' path='docs/member[@name=\"M\"]/*'/>\n"
                    + "    public void M() { }\n}\n",
                ["docs.xml"] = "<docs><member name=\"M\"><summary>Documented.</summary></member></docs>\n",
            });
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        // Resolving the <include> needs an XmlReferenceResolver; without one the compiler reports
        // CS1589. Both workspaces should resolve it and report the same (empty) diagnostics.
        (string Id, DiagnosticSeverity Severity)[] ms = await CompilerDiagnostics(comparison.MSBuild);
        (string Id, DiagnosticSeverity Severity)[] ba = await CompilerDiagnostics(comparison.Buildalyzer);

        ms.Should().NotContain(("CS1589", DiagnosticSeverity.Warning));
        ba.Should().BeEquivalentTo(ms);
    }

    [Test]
    public async Task Document_folders_match_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "FoldersProject",
            p => p.Property("TargetFramework", TargetFramework),
            new Dictionary<string, string>
            {
                ["Class1.cs"] = "namespace FoldersProject;\npublic class Class1 { }\n",
                ["Models/Thing.cs"] = "namespace FoldersProject.Models;\npublic class Thing { }\n",
                ["Models/Nested/Deep.cs"] = "namespace FoldersProject.Models.Nested;\npublic class Deep { }\n",

                // Inside the project, despite the leading dots. The SDK's globs skip dot-prefixed folders,
                // so it is included explicitly below.
                ["..generated/Gen.cs"] = "namespace FoldersProject.Generated;\npublic class Gen { }\n",
            });
        ProjectFixture.AddItem(projectPath, "Compile", "..generated/Gen.cs");
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        // MSBuildWorkspace records each document's logical folder path (e.g. Models/Nested).
        DocumentFolders(comparison.MSBuild)["Gen.cs"].Should().Equal("..generated");
        DocumentFolders(comparison.Buildalyzer).Should().BeEquivalentTo(DocumentFolders(comparison.MSBuild));
    }

    private static Dictionary<string, string[]> DocumentFolders(Project project) => project.Documents
        .Where(d => d.FilePath is not null)
        .ToDictionary(d => Path.GetFileName(d.FilePath!), d => d.Folders.ToArray(), StringComparer.OrdinalIgnoreCase);

    [Test]
    public async Task Linked_file_folders_match_reference()
    {
        using ProjectFixture fixture = new();
        File.WriteAllText(Path.Combine(fixture.Root.FullName, "Shared.cs"), "namespace Shared;\npublic class Shared { }\n");
        string projectPath = fixture.AddProject(
            "LinkedFolderProject",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Class1.cs", "namespace LinkedFolderProject;\npublic class Class1 { }\n"));
        ProjectFixture.AddItem(projectPath, "Compile", @"..\Shared.cs");
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        // A file above the project directory is the edge case of the relative-folder computation.
        DocumentFolders(comparison.Buildalyzer)["Shared.cs"]
            .Should().BeEquivalentTo(DocumentFolders(comparison.MSBuild)["Shared.cs"]);
    }

    [Test]
    public async Task Linked_file_link_metadata_matches_reference()
    {
        using ProjectFixture fixture = new();
        File.WriteAllText(Path.Combine(fixture.Root.FullName, "Shared.cs"), "namespace Shared;\npublic class Shared { }\n");
        string projectPath = fixture.AddProject(
            "ExplicitLinkProject",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Class1.cs", "namespace ExplicitLinkProject;\npublic class Class1 { }\n"));
        ProjectFixture.AddItem(projectPath, "Compile", @"..\Shared.cs", new Dictionary<string, string>
        {
            // A forward slash is a directory separator on every platform, and both Buildalyzer and
            // MSBuildWorkspace split a logical path on the platform's separators only.
            ["Link"] = "Virtual/Renamed.cs",
        });
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        // Link is the logical path the project files the document under: MSBuildWorkspace takes both the
        // document's name and its folders from it, rather than from where the file physically lives.
        DocumentIdentity(comparison.Buildalyzer, "Shared.cs")
            .Should().Be(("Renamed.cs", "Virtual"))
            .And.Be(DocumentIdentity(comparison.MSBuild, "Shared.cs"));
    }

    private static (string Name, string Folders) DocumentIdentity(Project project, string fileName)
    {
        Document document = project.Documents.Single(
            d => string.Equals(Path.GetFileName(d.FilePath), fileName, StringComparison.OrdinalIgnoreCase));
        return (document.Name, string.Join('/', document.Folders));
    }

    [Test]
    public async Task Additional_file_folders_match_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "AdditionalFolderProject",
            p => p.Property("TargetFramework", TargetFramework),
            new Dictionary<string, string>
            {
                ["Class1.cs"] = "namespace AdditionalFolderProject;\npublic class Class1 { }\n",
                ["Docs/notes.txt"] = "notes\n",
            });
        ProjectFixture.AddItem(projectPath, "AdditionalFiles", @"Docs\notes.txt");
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        Dictionary<string, string[]> ba = comparison.Buildalyzer.AdditionalDocuments
            .ToDictionary(d => Path.GetFileName(d.FilePath!), d => d.Folders.ToArray(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string[]> ms = comparison.MSBuild.AdditionalDocuments
            .ToDictionary(d => Path.GetFileName(d.FilePath!), d => d.Folders.ToArray(), StringComparer.OrdinalIgnoreCase);
        ba.Should().BeEquivalentTo(ms);
    }

    [Test]
    public async Task Output_ref_assembly_path_matches_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "RefAssemblyProject",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Class1.cs", "namespace RefAssemblyProject;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        comparison.MSBuild.OutputRefFilePath.Should().NotBeNull();
        Path.GetFileName(comparison.Buildalyzer.OutputRefFilePath)
            .Should().Be(Path.GetFileName(comparison.MSBuild.OutputRefFilePath));
    }

    [Test]
    public async Task Nullable_diagnostics_match_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "NullableProject",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("Nullable", "enable"),
            Source("Class1.cs", "namespace NullableProject;\npublic class Class1 { public int M(string? s) => s.Length; }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        (string Id, DiagnosticSeverity Severity)[] ms = await CompilerDiagnostics(comparison.MSBuild);
        (string Id, DiagnosticSeverity Severity)[] ba = await CompilerDiagnostics(comparison.Buildalyzer);

        ms.Should().Contain(("CS8602", DiagnosticSeverity.Warning), "nullable is enabled, so dereferencing s warns");
        ba.Should().BeEquivalentTo(ms);
    }

    [Test]
    public async Task Unsafe_code_compiles_the_same()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "UnsafeProject",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("AllowUnsafeBlocks", "true"),
            Source("Class1.cs", "namespace UnsafeProject;\npublic unsafe class Class1 { public int* P; }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        // Unsafe code compiles only when AllowUnsafe flowed through; otherwise CS0227.
        (await CompilationErrors(comparison.MSBuild)).Should().BeEmpty();
        (await CompilationErrors(comparison.Buildalyzer)).Should().BeEquivalentTo(await CompilationErrors(comparison.MSBuild));
    }

    [Test]
    public async Task Checked_overflow_diagnostics_match_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "CheckedProject",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("CheckForOverflowUnderflow", "true"),
            Source("Class1.cs", "namespace CheckedProject;\npublic class Class1 { public int V = int.MaxValue + 1; }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        // In checked mode the constant overflow is a compile error (CS0220); both must agree.
        (await CompilationErrors(comparison.MSBuild)).Should().Contain("CS0220");
        (await CompilationErrors(comparison.Buildalyzer)).Should().BeEquivalentTo(await CompilationErrors(comparison.MSBuild));
    }

    [Test]
    public async Task Internals_visible_to_resolves_across_projects()
    {
        using ProjectFixture fixture = new();
        string libraryPath = fixture.AddProject(
            "Library",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Secret.cs", "namespace Library;\ninternal class Secret { public int Value => 1; }\n"));
        ProjectFixture.AddItem(libraryPath, "InternalsVisibleTo", "App");

        string appPath = fixture.AddProject(
            "App",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Program.cs", "namespace App;\npublic class Program { object U = new Library.Secret(); }\n"));
        ProjectFixture.AddProjectReference(appPath, libraryPath);
        fixture.Restore(appPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(appPath);
        AssertLoadedCleanly(comparison);

        // App can touch Library's internal type only if Library's generated InternalsVisibleTo
        // attribute is part of its compilation. Otherwise CS0122 (inaccessible).
        (await CompilationErrors(comparison.MSBuild)).Should().BeEmpty();
        (await CompilationErrors(comparison.Buildalyzer)).Should().BeEquivalentTo(await CompilationErrors(comparison.MSBuild));
    }

    private static async Task<string[]> CompilationErrors(Project project)
    {
        Compilation compilation = await project.GetCompilationAsync();
        return [.. compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Id)
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)];
    }

    private static async Task<(string Id, DiagnosticSeverity Severity)[]> AnalyzerDiagnostics(Project project, string id)
    {
        Compilation compilation = await project.GetCompilationAsync();
        ImmutableArray<DiagnosticAnalyzer> analyzers =
            [.. project.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(project.Language))];
        if (analyzers.IsEmpty)
        {
            return [];
        }

        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers(analyzers, project.AnalyzerOptions)
            .GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics
            .Where(d => d.Id == id)
            .Select(d => (d.Id, d.Severity))
            .OrderBy(x => x.Id, StringComparer.Ordinal)];
    }

    [Test]
    [Explicit("Diagnostic: dumps project-level facts for triage.")]
    public async Task Exploratory_project_facts_diff()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "Facts",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("AssemblyName", "CustomAssembly")
                .Property("RootNamespace", "Custom.Root")
                .Property("TreatWarningsAsErrors", "true")
                .Property("Optimize", "true")
                .Property("NoWarn", "CA1822;CS0219")
                .Property("GenerateDocumentationFile", "true"),
            Source("Class1.cs", "namespace Facts;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions ba = comparison.Buildalyzer.CSharpOptions();
        Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions ms = comparison.MSBuild.CSharpOptions();

        await TestContext.Out.WriteLineAsync($"AssemblyName     BA={comparison.Buildalyzer.AssemblyName}  MS={comparison.MSBuild.AssemblyName}");
        await TestContext.Out.WriteLineAsync($"DefaultNamespace BA={comparison.Buildalyzer.DefaultNamespace}  MS={comparison.MSBuild.DefaultNamespace}");
        await TestContext.Out.WriteLineAsync($"OutputFilePath   BA={Path.GetFileName(comparison.Buildalyzer.OutputFilePath)}  MS={Path.GetFileName(comparison.MSBuild.OutputFilePath)}");
        await TestContext.Out.WriteLineAsync($"OptimizationLvl  BA={ba.OptimizationLevel}  MS={ms.OptimizationLevel}");
        await TestContext.Out.WriteLineAsync($"GeneralDiag      BA={ba.GeneralDiagnosticOption}  MS={ms.GeneralDiagnosticOption}");
        await TestContext.Out.WriteLineAsync($"SpecificDiag     BA=[{string.Join(",", ba.SpecificDiagnosticOptions.Select(x => $"{x.Key}={x.Value}"))}]  MS=[{string.Join(",", ms.SpecificDiagnosticOptions.Select(x => $"{x.Key}={x.Value}"))}]");
        await TestContext.Out.WriteLineAsync($"DocumentationMd  BA={comparison.Buildalyzer.CSharpParse().DocumentationMode}  MS={comparison.MSBuild.CSharpParse().DocumentationMode}");
    }

    [Test]
    [Explicit("Diagnostic: dumps every document collection for triage.")]
    public async Task Exploratory_document_diff()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "DocDiff",
            p => p
                .Property("TargetFramework", TargetFramework)
                .ItemPackageReference("Riok.Mapperly", "4.3.1"),
            Source("Class1.cs", "namespace DocDiff;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        await TestContext.Out.WriteLineAsync($"documents        BA=[{string.Join(", ", comparison.Buildalyzer.SourceFileNames())}]");
        await TestContext.Out.WriteLineAsync($"documents        MS=[{string.Join(", ", comparison.MSBuild.SourceFileNames())}]");
        await TestContext.Out.WriteLineAsync($"additional       BA=[{string.Join(", ", comparison.Buildalyzer.AdditionalDocumentNames())}]");
        await TestContext.Out.WriteLineAsync($"additional       MS=[{string.Join(", ", comparison.MSBuild.AdditionalDocumentNames())}]");
        await TestContext.Out.WriteLineAsync($"analyzerconfig   BA=[{string.Join(", ", comparison.Buildalyzer.AnalyzerConfigDocumentNames())}]");
        await TestContext.Out.WriteLineAsync($"analyzerconfig   MS=[{string.Join(", ", comparison.MSBuild.AnalyzerConfigDocumentNames())}]");
    }

    [Test]
    public async Task Reference_aliases_match_reference()
    {
        using ProjectFixture fixture = new();
        string libraryPath = fixture.AddProject(
            "AliasedLibrary",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Widget.cs", "namespace AliasedLibrary;\npublic class Widget { }\n"));
        string appPath = fixture.AddProject(
            "AliasApp",
            p => p.Property("TargetFramework", TargetFramework),
            Source(
                "Program.cs",
                "extern alias Json;\nextern alias Lib;\n"
                + "public class Program { Json::Newtonsoft.Json.Linq.JObject? O; Lib::AliasedLibrary.Widget? W; }\n"));
        ProjectFixture.AddItem(appPath, "PackageReference", "Newtonsoft.Json", new Dictionary<string, string>
        {
            ["Version"] = "13.0.3",
            ["Aliases"] = "Json",
        });
        ProjectFixture.AddItem(
            appPath,
            "ProjectReference",
            Path.GetRelativePath(Path.GetDirectoryName(appPath)!, libraryPath).Replace('/', '\\'),
            new Dictionary<string, string> { ["Aliases"] = "Lib" });
        fixture.Restore(appPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(appPath);
        AssertLoadedCleanly(comparison);

        // Aliases ride on the compiler's /reference:Alias=path switches; both loaders must carry them
        // onto the metadata reference (package) and the project reference (project) alike.
        ProjectShape ms = comparison.MSBuild.Shape();
        ms.MetadataReferences.Should().Contain(r => r.Aliases == "Json" && r.FilePath!.EndsWith("Newtonsoft.Json.dll", StringComparison.Ordinal));
        ms.ProjectReferences.Should().ContainSingle().Which.Aliases.Should().Be("Lib");

        ProjectShape ba = comparison.Buildalyzer.Shape();
        ba.MetadataReferences.Should().BeEquivalentTo(ms.MetadataReferences);
        ba.ProjectReferences.Should().BeEquivalentTo(ms.ProjectReferences);

        // And the aliases actually resolve: the extern alias directives compile on both sides.
        (await CompilationErrors(comparison.MSBuild)).Should().BeEmpty();
        (await CompilationErrors(comparison.Buildalyzer)).Should().BeEquivalentTo(await CompilationErrors(comparison.MSBuild));
    }

    [Test]
    public async Task Global_reference_aliases_match_reference()
    {
        using ProjectFixture fixture = new();
        string libraryPath = fixture.AddProject(
            "AliasedLibrary",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Widget.cs", "namespace AliasedLibrary;\npublic class Widget { }\n"));

        // 'global' alongside an alias keeps the assembly in the global namespace as well, so the same
        // types are reachable both unqualified and through the extern alias.
        string appPath = fixture.AddProject(
            "AliasApp",
            p => p.Property("TargetFramework", TargetFramework),
            Source(
                "Program.cs",
                "extern alias Json;\nextern alias Lib;\n"
                + "public class Program\n{\n"
                + "    Json::Newtonsoft.Json.Linq.JObject? O; Newtonsoft.Json.Linq.JObject? GO;\n"
                + "    Lib::AliasedLibrary.Widget? W; AliasedLibrary.Widget? GW;\n"
                + "}\n"));
        ProjectFixture.AddItem(appPath, "PackageReference", "Newtonsoft.Json", new Dictionary<string, string>
        {
            ["Version"] = "13.0.3",
            ["Aliases"] = "global,Json",
        });
        ProjectFixture.AddItem(
            appPath,
            "ProjectReference",
            Path.GetRelativePath(Path.GetDirectoryName(appPath)!, libraryPath).Replace('/', '\\'),
            new Dictionary<string, string> { ["Aliases"] = "global,Lib" });
        fixture.Restore(appPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(appPath);
        AssertLoadedCleanly(comparison);

        ProjectShape ms = comparison.MSBuild.Shape();
        ProjectShape ba = comparison.Buildalyzer.Shape();
        ba.MetadataReferences.Should().BeEquivalentTo(ms.MetadataReferences);
        ba.ProjectReferences.Should().BeEquivalentTo(ms.ProjectReferences);

        (await CompilationErrors(comparison.MSBuild)).Should().BeEmpty();
        (await CompilationErrors(comparison.Buildalyzer)).Should().BeEquivalentTo(await CompilationErrors(comparison.MSBuild));
    }

    [Test]
    public async Task Reference_aliases_reconstructed_without_a_compiler_invocation_match_reference()
    {
        // When the build never reaches CoreCompile (issue #341) the references come from the ReferencePath
        // items ResolveAssemblyReference produced, whose Aliases and EmbedInteropTypes metadata is what the
        // compiler task would have read. The reconstructed project has to carry them the same way, or an
        // `extern alias` in the sources fails to bind (CS0430) where the real build compiles.
        using ProjectFixture fixture = new();
        string libraryPath = fixture.AddProject(
            "AliasedLibrary",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Widget.cs", "namespace AliasedLibrary;\npublic class Widget { }\n"));
        string appPath = fixture.AddProject(
            "AliasApp",
            p => p.Property("TargetFramework", TargetFramework),
            Source(
                "Program.cs",
                "extern alias Json;\nextern alias Lib;\n"
                + "public class Program\n{\n"
                + "    Json::Newtonsoft.Json.Linq.JObject? O; Newtonsoft.Json.Linq.JObject? GO;\n"
                + "    Lib::AliasedLibrary.Widget? W;\n"
                + "}\n"));
        ProjectFixture.AddItem(appPath, "PackageReference", "Newtonsoft.Json", new Dictionary<string, string>
        {
            ["Version"] = "13.0.3",
            ["Aliases"] = "global,Json",
        });
        ProjectFixture.AddItem(
            appPath,
            "ProjectReference",
            Path.GetRelativePath(Path.GetDirectoryName(appPath)!, libraryPath).Replace('/', '\\'),
            new Dictionary<string, string> { ["Aliases"] = "Lib" });
        fixture.Restore(appPath);

        // ResolveAssemblyReferences produces ReferencePath but stops well short of CoreCompile.
        Buildalyzer.Environment.EnvironmentOptions options = new();
        options.TargetsToBuild.Clear();
        options.TargetsToBuild.Add("ResolveAssemblyReferences");

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(appPath, options: options);

        comparison.BuildalyzerLog.Should().Contain("No compiler invocation was captured");
        ProjectShape ms = comparison.MSBuild.Shape();
        ProjectShape ba = comparison.Buildalyzer.Shape();
        ba.MetadataReferences.Should().BeEquivalentTo(ms.MetadataReferences, comparison.BuildalyzerLog);
        ba.ProjectReferences.Should().BeEquivalentTo(ms.ProjectReferences, comparison.BuildalyzerLog);

        // And the aliases actually bind on the reconstructed side too.
        (await CompilationErrors(comparison.Buildalyzer)).Should().BeEmpty(comparison.BuildalyzerLog);
    }

    [Test]
    public async Task Signing_options_match_reference()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "PublicSignProject",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("SignAssembly", "true")
                .Property("PublicSign", "true")
                .Property("AssemblyOriginatorKeyFile", "key.snk"),
            Source("Class1.cs", "namespace PublicSignProject;\npublic class Class1 { }\n"));
        StrongNameKey.Write(Path.Combine(Path.GetDirectoryName(projectPath)!, "key.snk"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        SortedDictionary<string, string?> ms = comparison.MSBuild.Shape().CompilationOptions;
        ms["CryptoKeyFile"].Should().EndWith("key.snk");
        ms.Should().Contain("PublicSign", "True");
        comparison.Buildalyzer.Shape().CompilationOptions.Should().BeEquivalentTo(ms);
    }

    [Test]
    public async Task Project_shape_matches_reference()
    {
        using ProjectFixture fixture = new();
        string libraryPath = fixture.AddProject(
            "ShapeLibrary",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Widget.cs", "namespace ShapeLibrary;\npublic class Widget { }\n"));
        string projectPath = fixture.AddProject(
            "ShapeProject",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("RootNamespace", "Shape.Root")
                .Property("AssemblyName", "Shape.Assembly")
                .Property("GenerateDocumentationFile", "true")
                .Property("NoWarn", "CS1591")
                .ItemPackageReference("Newtonsoft.Json", "13.0.3"),
            new Dictionary<string, string>
            {
                ["Class1.cs"] = "namespace ShapeProject;\npublic class Class1 { }\n",
                ["Models/Thing.cs"] = "namespace ShapeProject.Models;\npublic class Thing { }\n",
                ["Docs/notes.txt"] = "notes\n",
                [".editorconfig"] = "root = true\n",
            });
        ProjectFixture.AddItem(projectPath, "AdditionalFiles", @"Docs\notes.txt");
        ProjectFixture.AddProjectReference(projectPath, libraryPath);
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        // One representative project exercising every facet the shape captures: identity and output
        // paths, documents of all three kinds with folders, package/project/analyzer references, and
        // the full option set. Any divergence is reported field by field.
        ProjectShape ms = comparison.MSBuild.Shape();
        ms.AssemblyName.Should().Be("Shape.Assembly");
        ms.DefaultNamespace.Should().Be("Shape.Root");
        ms.OutputFilePath.Should().EndWith("Shape.Assembly.dll");
        ms.OutputRefFilePath.Should().NotBeNull();
        ms.Documents.Should().Contain(d => d.Name == "Thing.cs" && d.Folders == "Models");
        ms.AdditionalDocuments.Should().Contain(d => d.Name == "notes.txt" && d.Folders == "Docs");
        ms.AnalyzerConfigDocuments.Should().Contain(d => d.Name == ".editorconfig");
        ms.ProjectReferences.Should().ContainSingle().Which.TargetName.Should().Be("ShapeLibrary");
        ms.AnalyzerReferences.Should().NotBeEmpty();

        comparison.Buildalyzer.Shape().Should().BeEquivalentTo(ms);
    }

    [Test]
    public async Task Solution_shape_matches_reference()
    {
        using ProjectFixture fixture = new();
        string leaf = fixture.AddProject(
            "Leaf",
            p => p.Property("TargetFrameworks", "netstandard2.0;" + TargetFramework),
            Source("Leaf.cs", "namespace Leaf;\npublic class L { }\n"));
        string middle = fixture.AddProject(
            "Middle",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Middle.cs", "namespace Middle;\npublic class M { Leaf.L L = new(); }\n"));
        string top = fixture.AddProject(
            "Top",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Top.cs", "namespace Top;\npublic class T { Middle.M M = new(); }\n"));
        ProjectFixture.AddProjectReference(middle, leaf);
        ProjectFixture.AddProjectReference(top, middle);
        fixture.Restore(top);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(top);
        AssertLoadedCleanly(comparison);

        // The whole graph, not just the primary: every project MSBuildWorkspace loaded (including both
        // flavours of the multi-targeted leaf) must exist on the Buildalyzer side with the same shape,
        // and the project references must point at the same flavours.
        ProjectShape[] ms = comparison.MSBuild.Solution.Shape();
        ms.Select(p => p.Name).Should().BeEquivalentTo("Top", "Middle", "Leaf(netstandard2.0)", $"Leaf({TargetFramework})");
        ms.Single(p => p.Name == "Middle").ProjectReferences.Should().ContainSingle().Which.TargetName.Should().Be($"Leaf({TargetFramework})");

        comparison.Buildalyzer.Solution.Shape().Should().BeEquivalentTo(ms);
    }

    [Test]
    public async Task Paths_with_parent_segments_are_canonical()
    {
        // Items pulled in by package build logic routinely carry un-normalized segments - a
        // "<pkg>/build/../stylecop.json" AdditionalFiles include, a "../Shared/*.cs" link, a HintPath
        // through "..". MSBuildWorkspace reports every input as a canonical full path (Roslyn's parser
        // resolves and collapses them); the same file must not surface under a different spelling here,
        // since consumers key documents and references by path.
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "DottedPaths",
            p => p.Property("TargetFramework", TargetFramework),
            Source("Class1.cs", "namespace DottedPaths;\npublic class Class1 { }\n"));
        string projectDirectory = Path.GetDirectoryName(projectPath)!;

        Directory.CreateDirectory(Path.Combine(fixture.Root.FullName, "Shared"));
        File.WriteAllText(Path.Combine(fixture.Root.FullName, "Shared", "Linked.cs"), "namespace DottedPaths;\npublic class Linked { }\n");
        File.WriteAllText(Path.Combine(projectDirectory, "BannedSymbols.txt"), "T:System.Object\n");
        File.WriteAllText(Path.Combine(projectDirectory, "custom.globalconfig"), "is_global = true\ndotnet_diagnostic.CA1822.severity = none\n");

        string hintPath = typeof(TestAttribute).Assembly.Location;
        string hintDirectory = Path.GetDirectoryName(hintPath)!;
        string dottedHintPath = Path.Combine(hintDirectory, "..", Path.GetFileName(hintDirectory), Path.GetFileName(hintPath));

        ProjectFixture.AddItem(projectPath, "Compile", "../Shared/Linked.cs");
        ProjectFixture.AddItem(projectPath, "AdditionalFiles", "$(MSBuildProjectDirectory)/cfg/../BannedSymbols.txt");
        ProjectFixture.AddItem(projectPath, "GlobalAnalyzerConfigFiles", "$(MSBuildProjectDirectory)/cfg/../custom.globalconfig");
        ProjectFixture.AddItem(projectPath, "Reference", "nunit.framework", new Dictionary<string, string> { ["HintPath"] = dottedHintPath });
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
        AssertLoadedCleanly(comparison);

        string[] buildalyzerPaths =
        [
            .. comparison.Buildalyzer.SourceFilePaths(),
            .. comparison.Buildalyzer.AdditionalDocumentPaths(),
            .. comparison.Buildalyzer.AnalyzerConfigDocumentPaths(),
            .. comparison.Buildalyzer.MetadataReferencePaths(),
        ];
        buildalyzerPaths.Should().NotContain(path => path.Contains("..", StringComparison.Ordinal) || path.Contains("/./", StringComparison.Ordinal));
        buildalyzerPaths.Should().Contain(path => path.EndsWith("Linked.cs", StringComparison.Ordinal))
            .And.Contain(path => path.EndsWith("BannedSymbols.txt", StringComparison.Ordinal))
            .And.Contain(path => path.EndsWith("custom.globalconfig", StringComparison.Ordinal))
            .And.Contain(path => path.EndsWith("nunit.framework.dll", StringComparison.Ordinal));

        comparison.Buildalyzer.SourceFilePaths().Should().BeEquivalentTo(comparison.MSBuild.SourceFilePaths());
        comparison.Buildalyzer.AdditionalDocumentPaths().Should().BeEquivalentTo(comparison.MSBuild.AdditionalDocumentPaths());
        comparison.Buildalyzer.AnalyzerConfigDocumentPaths().Should().BeEquivalentTo(comparison.MSBuild.AnalyzerConfigDocumentPaths());
        comparison.Buildalyzer.MetadataReferencePaths().Should().BeEquivalentTo(comparison.MSBuild.MetadataReferencePaths());
        comparison.Buildalyzer.Shape().Should().BeEquivalentTo(comparison.MSBuild.Shape(), comparison.BuildalyzerLog);
    }

    [Test]
    public async Task Options_reconstructed_without_a_compiler_invocation_match_reference()
    {
        // When the build never reaches CoreCompile (issue #341) there is no compiler command line to
        // parse, so the compilation and parse options are reconstructed from the evaluated properties by
        // rebuilding the switches the Csc task would have emitted and handing them to Roslyn's parser.
        // Driving Buildalyzer to a target short of CoreCompile exercises exactly that path, while
        // MSBuildWorkspace performs its full design-time build: the options must still agree, and the
        // preprocessor symbols must include the SDK's implicit framework defines that only a target adds.
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "NoCompilerOptions",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("OutputType", "Exe")
                .Property("StartupObject", "NoCompilerOptions.Program")
                .Property("Nullable", "enable")
                .Property("LangVersion", "preview")
                .Property("AllowUnsafeBlocks", "true")
                .Property("CheckForOverflowUnderflow", "true")
                .Property("Optimize", "true")
                .Property("PlatformTarget", "x64")
                .Property("WarningLevel", "7")
                .Property("TreatWarningsAsErrors", "true")
                .Property("WarningsAsErrors", "CS0168;1591")
                .Property("WarningsNotAsErrors", "NU1605")
                .Property("NoWarn", "CA1822;CS0219;1701")
                .Property("DefineConstants", "$(DefineConstants);CUSTOM_SYMBOL")
                .Property("Features", "strict;debug-determinism")
                .Property("GenerateDocumentationFile", "true"),
            new Dictionary<string, string>
            {
                ["Program.cs"] = "namespace NoCompilerOptions;\npublic static class Program { public static void Main() { } }\n",
                [".editorconfig"] = "root = true\n\n[*.cs]\ndotnet_diagnostic.CA1822.severity = warning\n",
            });
        fixture.Restore(projectPath);

        // ResolveAssemblyReferences runs the SDK's reference resolution (so ReferencePath is captured for
        // the fallback) but stops well short of CoreCompile.
        Buildalyzer.Environment.EnvironmentOptions options = new();
        options.TargetsToBuild.Clear();
        options.TargetsToBuild.Add("ResolveAssemblyReferences");

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath, options: options);

        comparison.BuildalyzerLog.Should().Contain("No compiler invocation was captured");
        ProjectShape buildalyzer = comparison.Buildalyzer.Shape();
        ProjectShape msbuild = comparison.MSBuild.Shape();
        buildalyzer.CompilationOptions.Should().BeEquivalentTo(msbuild.CompilationOptions, comparison.BuildalyzerLog);
        buildalyzer.ParseOptions.Should().BeEquivalentTo(msbuild.ParseOptions, comparison.BuildalyzerLog);
        comparison.Buildalyzer.PreprocessorSymbols().Should().Contain(["NET", "NET10_0", "NETCOREAPP", "NET5_0_OR_GREATER", "NET10_0_OR_GREATER", "NETCOREAPP3_1_OR_GREATER", "CUSTOM_SYMBOL"]);

        // The evaluated Compile items lack the build-generated sources (AssemblyInfo, GlobalUsings, ...),
        // so the documents are a subset; the resolved references, though, are complete - including the
        // targeting pack's facades (netstandard.dll, mscorlib.dll, System.dll, ...).
        comparison.Buildalyzer.SourceFilePaths().Should().BeSubsetOf(comparison.MSBuild.SourceFilePaths())
            .And.Contain(path => path.EndsWith("Program.cs", StringComparison.Ordinal));
        comparison.Buildalyzer.MetadataReferencePaths().Should().BeEquivalentTo(comparison.MSBuild.MetadataReferencePaths(), comparison.BuildalyzerLog);
        comparison.Buildalyzer.MetadataReferenceNames().Should().Contain(["netstandard.dll", "mscorlib.dll", "System.dll", "System.Xml.dll"]);

        // Likewise the analyzer configuration: the .editorconfig the SDK discovers at evaluation is there, the
        // GeneratedMSBuildEditorConfig.editorconfig that only the build produces is not.
        comparison.Buildalyzer.AnalyzerConfigDocumentPaths().Should().BeSubsetOf(comparison.MSBuild.AnalyzerConfigDocumentPaths(), comparison.BuildalyzerLog);
        comparison.Buildalyzer.AnalyzerConfigDocumentNames().Should().Contain(".editorconfig", comparison.BuildalyzerLog);
        comparison.MSBuild.AnalyzerConfigDocumentNames().Should().Contain("NoCompilerOptions.GeneratedMSBuildEditorConfig.editorconfig");
    }

    [Test]
    public async Task Visual_basic_options_reconstructed_without_a_compiler_invocation_match_reference()
    {
        // The VB counterpart of Options_reconstructed_without_a_compiler_invocation_match_reference: the
        // Option Strict/Explicit/Infer/Compare settings, root namespace, global imports and the CoreCompile-
        // appended NoWarn ids must come out of the reconstructed vbc command line the way vbc reports them.
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "VbNoCompilerOptions",
            p => p
                .Property("TargetFramework", TargetFramework)
                .Property("RootNamespace", "Custom.Vb")
                .Property("OptionStrict", "On")
                .Property("OptionExplicit", "Off")
                .Property("OptionInfer", "On")
                .Property("OptionCompare", "Text")
                .Property("NoWarn", "BC40008;42024")
                .Property("WarningsAsErrors", "BC42025")
                .Property("DefineConstants", "MY_FLAG=True,MY_NUMBER=42")
                .Property("GenerateDocumentationFile", "true"),
            Source("Widget.vb", "Public Class Widget\nEnd Class\n"),
            extension: ".vbproj");
        fixture.Restore(projectPath);

        Buildalyzer.Environment.EnvironmentOptions options = new();
        options.TargetsToBuild.Clear();
        options.TargetsToBuild.Add("ResolveAssemblyReferences");

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath, options: options);

        comparison.BuildalyzerLog.Should().Contain("No compiler invocation was captured");
        ProjectShape buildalyzer = comparison.Buildalyzer.Shape();
        ProjectShape msbuild = comparison.MSBuild.Shape();
        buildalyzer.CompilationOptions.Should().BeEquivalentTo(msbuild.CompilationOptions, comparison.BuildalyzerLog);
        WithoutVbPreprocessorSymbols(buildalyzer).ParseOptions.Should().BeEquivalentTo(WithoutVbPreprocessorSymbols(msbuild).ParseOptions, comparison.BuildalyzerLog);

        // FinalDefineConstants is composed at evaluation time, and the implicit framework defines are
        // appended as "<SYMBOL>=-1" (see WithoutVbPreprocessorSymbols for why the reference cannot be
        // the oracle for these).
        buildalyzer.ParseOptions["PreprocessorSymbolValues"].Should().Contain("MY_FLAG=True").And.Contain("MY_NUMBER=42").And.Contain("NET10_0=-1").And.Contain("CONFIG=Debug");
        comparison.Buildalyzer.MetadataReferencePaths().Should().BeEquivalentTo(comparison.MSBuild.MetadataReferencePaths(), comparison.BuildalyzerLog);
    }

    [Test]
    public async Task Failing_pre_compile_task_still_reaches_the_compiler()
    {
        // A Compile item that duplicates one the SDK's default globs already include fails
        // CheckForDuplicateItems (NETSDK1022), which runs before CoreCompile. Both loaders pass
        // ContinueOnError=ErrorAndContinue, so the error is logged and the build carries on to
        // CoreCompile: the compiler's real inputs and command line are captured instead of the
        // pre-compile fallback, and the SDK-generated sources are present on both sides.
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "DuplicateCompile",
            p => p.Property("TargetFramework", TargetFramework).ItemCompile("Class1.cs"),
            Source("Class1.cs", "namespace DuplicateCompile;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        // MSBuildWorkspace reports the message without its code; Buildalyzer's log carries the code.
        comparison.MSBuildFailures.Should().Contain(f => f.Contains("Duplicate 'Compile' items"));
        comparison.BuildalyzerLog.Should().Contain("NETSDK1022");
        comparison.BuildalyzerLog.Should().NotContain("No compiler invocation was captured");
        comparison.Buildalyzer.SourceFileNames().Should().Contain("DuplicateCompile.AssemblyInfo.cs");
        comparison.Buildalyzer.SourceFilePaths().Should().BeEquivalentTo(comparison.MSBuild.SourceFilePaths(), comparison.BuildalyzerLog);
        comparison.Buildalyzer.Shape().Should().BeEquivalentTo(comparison.MSBuild.Shape(), comparison.BuildalyzerLog);
    }

    [Test]
    public async Task Failing_restore_still_reaches_the_compiler()
    {
        // Central Package Management with transitive pinning, where a transitive dependency is pinned
        // below the version its parent requires, fails restore with NU1109. NuGet writes the assets file
        // regardless, and MSBuildWorkspace - which never restores - builds against it: ResolvePackageAssets
        // replays the error, ContinueOnError=ErrorAndContinue carries on, and CoreCompile runs. Buildalyzer's
        // own -restore fails first, which stops MSBuild before any build target runs, so it has to build
        // again without the restore: otherwise the project is reconstructed from the evaluation alone, which
        // has no references, analyzers or generated sources at all.
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "RestoreFailure",
            p => p
                .Property("TargetFramework", TargetFramework)
                .ItemPackageReference("Microsoft.Extensions.Logging"),
            new Dictionary<string, string>
            {
                ["Class1.cs"] = "namespace RestoreFailure;\npublic class Class1 { }\n",
                ["Directory.Packages.props"] = """
                    <Project>
                      <PropertyGroup>
                        <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                        <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
                      </PropertyGroup>
                      <ItemGroup>
                        <PackageVersion Include="Microsoft.Extensions.Logging" Version="8.0.0" />
                        <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="6.0.0" />
                      </ItemGroup>
                    </Project>
                    """,
            });
        fixture.Restore(projectPath, allowFailure: true);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);

        // MSBuildWorkspace reports the message without its code; Buildalyzer's log carries the code.
        comparison.MSBuildFailures.Should().Contain(f => f.Contains("Detected package downgrade"));
        comparison.BuildalyzerLog.Should().Contain("NU1109");
        comparison.BuildalyzerLog.Should().NotContain("No compiler invocation was captured");
        comparison.Buildalyzer.SourceFileNames().Should().Contain("RestoreFailure.AssemblyInfo.cs");
        comparison.Buildalyzer.MetadataReferenceNames().Should().Contain(["Microsoft.Extensions.Logging.dll", "Microsoft.Extensions.Logging.Abstractions.dll"]);
        comparison.Buildalyzer.Shape().Should().BeEquivalentTo(comparison.MSBuild.Shape(), comparison.BuildalyzerLog);
    }

    private static void AssertLoadedCleanly(WorkspaceComparison comparison)
    {
        comparison.MSBuildFailures.Should().BeEmpty();
        comparison.BuildalyzerLog.Should().NotContain("Workspace failed");
    }

    private static Dictionary<string, string> Source(string name, string content) => new() { [name] = content };
}
