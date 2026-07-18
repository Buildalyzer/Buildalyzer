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
        comparison.Buildalyzer.SourceFileNames().Should().BeEquivalentTo(comparison.MSBuild.SourceFileNames());
        comparison.Buildalyzer.MetadataReferenceNames().Should().BeEquivalentTo(comparison.MSBuild.MetadataReferenceNames());
        comparison.Buildalyzer.CompilationOptions!.OutputKind.Should().Be(comparison.MSBuild.CompilationOptions!.OutputKind);
        comparison.Buildalyzer.PreprocessorSymbols().Should().BeEquivalentTo(comparison.MSBuild.PreprocessorSymbols());
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
        comparison.Buildalyzer.AdditionalDocumentNames()
            .Should().BeEquivalentTo(comparison.MSBuild.AdditionalDocumentNames());
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
        comparison.Buildalyzer.MetadataReferenceNames()
            .Should().Contain("Newtonsoft.Json.dll")
            .And.BeEquivalentTo(comparison.MSBuild.MetadataReferenceNames());
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
        comparison.Buildalyzer.ProjectReferenceNames()
            .Should().Contain("Library.csproj")
            .And.BeEquivalentTo(comparison.MSBuild.ProjectReferenceNames());
    }

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
        comparison.Buildalyzer.AnalyzerReferenceNames()
            .Should().BeEquivalentTo(comparison.MSBuild.AnalyzerReferenceNames());
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
        comparison.Buildalyzer.AnalyzerReferenceNames()
            .Should().Contain("Riok.Mapperly.dll")
            .And.BeEquivalentTo(comparison.MSBuild.AnalyzerReferenceNames());

        // Every document collection should agree, including the analyzer-config documents that
        // carry the build_property.* values a source generator reads at run time.
        comparison.Buildalyzer.SourceFileNames().Should().BeEquivalentTo(comparison.MSBuild.SourceFileNames());
        comparison.Buildalyzer.AdditionalDocumentNames().Should().BeEquivalentTo(comparison.MSBuild.AdditionalDocumentNames());
        comparison.Buildalyzer.AnalyzerConfigDocumentNames()
            .Should().Contain("GeneratorProject.GeneratedMSBuildEditorConfig.editorconfig")
            .And.BeEquivalentTo(comparison.MSBuild.AnalyzerConfigDocumentNames());
    }

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
        comparison.Buildalyzer.SourceFileNames().Should().BeEquivalentTo(comparison.MSBuild.SourceFileNames());
        comparison.Buildalyzer.MetadataReferenceNames().Should().BeEquivalentTo(comparison.MSBuild.MetadataReferenceNames());
        comparison.Buildalyzer.CompilationOptions!.OutputKind.Should().Be(comparison.MSBuild.CompilationOptions!.OutputKind);
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
        comparison.Buildalyzer.SourceFileNames()
            .Should().BeEquivalentTo(comparison.MSBuild.SourceFileNames());
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
        comparison.Buildalyzer.SolutionProjectNames()
            .Should().BeEquivalentTo(comparison.MSBuild.SolutionProjectNames());
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
        comparison.Buildalyzer.SourceFileNames()
            .Should().BeEquivalentTo(comparison.MSBuild.SourceFileNames());
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
        comparison.Buildalyzer.SourceFileNames().Should().BeEquivalentTo(comparison.MSBuild.SourceFileNames());
        comparison.Buildalyzer.MetadataReferenceNames().Should().BeEquivalentTo(comparison.MSBuild.MetadataReferenceNames());
        comparison.Buildalyzer.PreprocessorSymbols().Should().BeEquivalentTo(comparison.MSBuild.PreprocessorSymbols());
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
        comparison.Buildalyzer.AnalyzerConfigDocumentNames()
            .Should().BeEquivalentTo(comparison.MSBuild.AnalyzerConfigDocumentNames());
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

    private static void AssertLoadedCleanly(WorkspaceComparison comparison)
    {
        comparison.MSBuildFailures.Should().BeEmpty();
        comparison.BuildalyzerLog.Should().NotContain("Workspace failed");
    }

    private static Dictionary<string, string> Source(string name, string content) => new() { [name] = content };
}
