using System.IO;
using System.Threading.Tasks;
using Buildalyzer.TestTools;
using Microsoft.CodeAnalysis;

namespace Buildalyzer.Workspaces.Tests;

[TestFixture]
[NonParallelizable]
public class ProjectAnalyzerExtensionsFixture
{
    [Test]
    public void Loads_Workspace()
    {
        using var ctx = Context.ForProject(@"SdkNetStandardProject\SdkNetStandardProject.csproj");

        using var workspace = ctx.Analyzer.GetWorkspace();

        var document = workspace.CurrentSolution.Projects.First().Documents.First();

        document.Should().BeEquivalentTo(new { Name = "Class1.cs" });
    }

    [Test]
    public void Loads_Workspace_for_a_file_based_app()
    {
        // This suite's own copy of the app: the generated project is materialized next to the entry point
        // and deleted after the build, and Buildalyzer.Tests builds its copy in a concurrently running host.
        using var ctx = Context.ForProject("FileBasedAppWorkspace/app.cs");

        using var workspace = ctx.Analyzer.GetWorkspace();

        var project = workspace.CurrentSolution.Projects.Should().ContainSingle().Subject;
        project.Documents.Should().Contain(d => d.Name == "app.cs");
        project.MetadataReferences.Should().Contain(r => r.Display!.EndsWith("NodaTime.dll", StringComparison.Ordinal));
    }

    [Test]
    public void LoadsSolution()
    {
        // Given
        string solutionPath = GetFullPath(@"projects\TestProjects.sln");
        SafeStringWriter log = new SafeStringWriter();
        AnalyzerManager manager = new AnalyzerManager(solutionPath, new AnalyzerManagerOptions { LogWriter = log });

        // When
        using var workspace = manager.GetWorkspace();

        // Then
        string logged = log.ToString();
        logged.Should().NotContain("Workspace failed");
        workspace.CurrentSolution.FilePath.Should().Be(solutionPath);
        workspace.CurrentSolution.Projects.Should().Contain(p => p.Name == "LegacyFrameworkProject");
        workspace.CurrentSolution.Projects.Should().Contain(p => p.Name == "SdkFrameworkProject");
    }

    [Test(Description = "Loading a workspace from a .slnx solution should not throw https://github.com/Buildalyzer/Buildalyzer/issues/350")]
    public void LoadsSlnxSolution()
    {
        // Given
        string solutionPath = GetFullPath(@"projects\SingleProject.slnx");
        SafeStringWriter log = new SafeStringWriter();
        AnalyzerManager manager = new AnalyzerManager(solutionPath, new AnalyzerManagerOptions { LogWriter = log });

        // When
        using var workspace = manager.GetWorkspace();

        // Then
        string logged = log.ToString();
        logged.Should().NotContain("Workspace failed");
        workspace.CurrentSolution.FilePath.Should().Be(solutionPath);
        workspace.CurrentSolution.Projects.Should().Contain(p => p.Name == "SdkNetStandardProject");
    }

    [Test]
    public async Task SupportsCompilation()
    {
        // Given
        SafeStringWriter log = new SafeStringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"projects\SdkNetStandardProject\SdkNetStandardProject.csproj", log);

        // When
        Workspace workspace = analyzer.GetWorkspace();
        Compilation compilation = await workspace.CurrentSolution.Projects.First().GetCompilationAsync();

        // Then
        string logged = log.ToString();
        logged.Should().NotContain("Workspace failed");
        compilation.GetSymbolsWithName(x => x == "Class1").Should().NotBeEmpty(log.ToString());
    }

    [Test]
    public void CreatesCompilationOptions()
    {
        // Given
        SafeStringWriter log = new SafeStringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"projects\SdkNetStandardProject\SdkNetStandardProject.csproj", log);

        // When
        Workspace workspace = analyzer.GetWorkspace();
        CompilationOptions compilationOptions = workspace.CurrentSolution.Projects.First().CompilationOptions;

        // Then
        string logged = log.ToString();
        logged.Should().NotContain("Workspace failed");
        compilationOptions.OutputKind.Should().Be(OutputKind.DynamicallyLinkedLibrary, log.ToString());
    }

    [Test]
    public void CompilationOptionsRespectEvaluatedProperties()
    {
        using var ctx = Context.ForProject(@"SdkCompilationOptionsProject\SdkCompilationOptionsProject.csproj");

        using var workspace = ctx.Analyzer.GetWorkspace();
        var compilationOptions = (Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions)workspace.CurrentSolution.Projects.First().CompilationOptions;

        compilationOptions.Should().BeEquivalentTo(new
        {
            AllowUnsafe = true,
            CheckOverflow = true,
            Deterministic = true,
            Platform = Platform.X64,
            WarningLevel = 7,
        });
    }

    [TestCase(false, 1)]
    [TestCase(true, 3)]
    public void AddsProjectReferences(bool addProjectReferences, int totalProjects)
    {
        // Given
        SafeStringWriter log = new SafeStringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"projects\LegacyFrameworkProjectWithReference\LegacyFrameworkProjectWithReference.csproj", log);

        // When
        Workspace workspace = analyzer.GetWorkspace(addProjectReferences);

        // Then
        string logged = log.ToString();
        logged.Should().NotContain("Workspace failed");
        workspace.CurrentSolution.Projects.Count().Should().Be(totalProjects, log.ToString());
    }

    [TestCase(false, 1)]
    [TestCase(true, 4)]
    public void AddsTransitiveProjectReferences(bool addProjectReferences, int totalProjects)
    {
        // Given
        SafeStringWriter log = new SafeStringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"projects\TransitiveProjectReference\TransitiveProjectReference.csproj", log);

        // When
        Workspace workspace = analyzer.GetWorkspace(addProjectReferences);

        // Then
        string logged = log.ToString();
        logged.Should().NotContain("Workspace failed");
        workspace.CurrentSolution.Projects.Count().Should().Be(totalProjects, log.ToString());
    }

    [Test]
    public async Task SupportsConstants()
    {
        // Given
        SafeStringWriter log = new SafeStringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"projects\SdkNetStandardProjectWithConstants\SdkNetStandardProjectWithConstants.csproj", log);

        // When
        Workspace workspace = analyzer.GetWorkspace();
        Compilation compilation = await workspace.CurrentSolution.Projects.First().GetCompilationAsync();

        // Then
        string logged = log.ToString();
        logged.Should().NotContain("Workspace failed");
        compilation.GetSymbolsWithName(x => x == "Class1").Should().BeEmpty(log.ToString());
        compilation.GetSymbolsWithName(x => x == "Class2").Should().NotBeEmpty(log.ToString());
    }

    [Test]
    public void SupportsAnalyzers()
    {
        // Given
        SafeStringWriter log = new SafeStringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"projects\SdkNetCore2ProjectWithAnalyzer\SdkNetCore2ProjectWithAnalyzer.csproj", log);

        // When
        Workspace workspace = analyzer.GetWorkspace();
        Project project = workspace.CurrentSolution.Projects.First();

        // Then
        string logged = log.ToString();
        logged.Should().NotContain("Workspace failed");
        project.AnalyzerReferences.Should().Contain(reference => reference.Display == "Microsoft.CodeQuality.Analyzers");
    }

    [Test]
    public void SupportsAdditionalFiles()
    {
        // Given
        SafeStringWriter log = new SafeStringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"projects\ProjectWithAdditionalFile\ProjectWithAdditionalFile.csproj", log);

        // When
        Workspace workspace = analyzer.GetWorkspace();
        Project project = workspace.CurrentSolution.Projects.First();

        // Then
        string logged = log.ToString();
        logged.Should().NotContain("Workspace failed");
        project.AdditionalDocuments.Select(d => d.Name).Should().BeEquivalentTo("message.txt");
    }

    [Test]
    public async Task SupportsNullabilityEnabled()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"projects\NullabilityEnabled\NullabilityEnabled.csproj", log);
        AdhocWorkspace workspace = analyzer.GetWorkspace();
        Project project = workspace.CurrentSolution.Projects.Single();

        // When
        Compilation compilation = await project.GetCompilationAsync();

        Diagnostic[] diagnostics = [.. compilation.GetDiagnostics().Where(d => d.Id == "CS8632")];

        diagnostics.Should().BeEmpty();
    }

    [Test(Description = "Test C#12 features https://github.com/phmonte/Buildalyzer/issues/281")]

    public async Task SupportsLangVersion12Features()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"projects\SdkNet8CS12FeaturesProject\SdkNet8CS12FeaturesProject.csproj", log);
        AdhocWorkspace workspace = analyzer.GetWorkspace();
        Project project = workspace.CurrentSolution.Projects.Single();

        // When
        Compilation compilation = await project.GetCompilationAsync();

        Diagnostic[] diagnostics = [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];

        diagnostics.Should().BeEmpty();
    }

    [Test(Description = "Test Reference Alias support")]

    public async Task SupportAssemblyAliases()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"projects\SdkNet8Alias\SdkNet8Alias.csproj", log);
        AdhocWorkspace workspace = analyzer.GetWorkspace();
        Project project = workspace.CurrentSolution.Projects.Single();

        // When
        Compilation compilation = await project.GetCompilationAsync();

        Diagnostic[] diagnostics = [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];

        diagnostics.Should().BeEmpty();
    }

    [Test(Description = "A consumer added before its dependency is still wired to it, alias included")]
    public async Task WiresProjectReferencesRegardlessOfOrder()
    {
        // Given - both results in hand, added directly (no addProjectReferences), consumer first.
        SafeStringWriter log = new SafeStringWriter();
        IProjectAnalyzer consumer = GetProjectAnalyzer(
            @"projects\AliasedProjectReference\AliasedConsumer\AliasedConsumer.csproj", log);
        IProjectAnalyzer library = GetProjectAnalyzer(
            @"projects\AliasedProjectReference\AliasedLibrary\AliasedLibrary.csproj", log, (AnalyzerManager)consumer.Manager);
        IAnalyzerResult consumerResult = consumer.Build().Single();
        IAnalyzerResult libraryResult = library.Build().Single();

        // When
        using AdhocWorkspace workspace = new AdhocWorkspace();
        Project consumerProject = consumerResult.AddToWorkspace(workspace)!;
        Project libraryProject = libraryResult.AddToWorkspace(workspace)!;

        // Then - the reference is wired from the consumer when the library arrives.
        consumerProject = workspace.CurrentSolution.GetProject(consumerProject.Id)!;
        ProjectReference reference = consumerProject.ProjectReferences.Should().ContainSingle().Subject;
        reference.ProjectId.Should().Be(libraryProject.Id);
        reference.Aliases.Should().BeEquivalentTo(["Lib"]);

        Compilation compilation = await consumerProject.GetCompilationAsync();
        compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty(log.ToString());
    }

    [Test(Description = "Static project references that are not real projects don't break closure discovery")]
    public void IgnoresProjectReferencesThatDoNotExist()
    {
        // Given - the project file carries a conditioned reference to an absent file and one through an
        // undefined property; MSBuild evaluates neither, but the XML scan that finds the closure sees both.
        SafeStringWriter log = new SafeStringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(
            @"projects\ConditionalProjectReference\ConditionalProjectReference.csproj", log);

        // When
        using AdhocWorkspace workspace = analyzer.GetWorkspace(addProjectReferences: true);

        // Then - the real reference is present, the phantom ones are skipped rather than thrown on.
        workspace.CurrentSolution.Projects.Select(p => p.Name)
            .Should().BeEquivalentTo(["ConditionalProjectReference", "SdkNetStandardProject"], log.ToString());
        workspace.CurrentSolution.Projects.Single(p => p.Name == "ConditionalProjectReference")
            .ProjectReferences.Should().ContainSingle();
    }

    [Test(Description = "A project reference resolved from an aliased assembly reference keeps its alias")]
    public async Task SupportsProjectReferenceAliases()
    {
        // Given
        SafeStringWriter log = new SafeStringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(
            @"projects\AliasedProjectReference\AliasedConsumer\AliasedConsumer.csproj", log);

        // When
        using var workspace = analyzer.GetWorkspace(addProjectReferences: true);
        Project project = workspace.CurrentSolution.Projects.Single(p => p.Name == "AliasedConsumer");

        // Then - the consumer reaches Widget only through "Lib::", so losing the alias when the assembly
        // reference is turned into a project reference surfaces as CS0430 on the extern alias.
        project.ProjectReferences.Single().Aliases.Should().BeEquivalentTo(["Lib"]);

        Compilation compilation = await project.GetCompilationAsync();
        compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty(log.ToString());
    }

#if Is_Windows
    [Test]
    public void HandlesWpfCustomControlLibrary()
    {
        // Given
        SafeStringWriter log = new SafeStringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"projects\WpfCustomControlLibrary1\WpfCustomControlLibrary1.csproj", log);

        // When
        using var workspace = analyzer.GetWorkspace();
        Project project = workspace.CurrentSolution.Projects.First();

        // Then
        string logged = log.ToString();
        logged.Should().NotContain("Workspace failed");
        project.Should().NotBeNull();
        project.Documents.Should().NotBeEmpty();
    }
#endif

    private static IProjectAnalyzer GetProjectAnalyzer(string projectFile, StringWriter log, AnalyzerManager manager = null)
    {
        // The path will get normalized inside the .GetProject() call below
        string projectPath = GetFullPath(projectFile);
        manager ??= new AnalyzerManager(new AnalyzerManagerOptions { LogWriter = log });
        return manager.GetProject(projectPath);
    }

    private static string GetFullPath(string partialPath)
    {
        return Path
            .GetFullPath(
                Path.Combine(
                    Path.GetDirectoryName(
                        typeof(ProjectAnalyzerExtensionsFixture).Assembly.Location),
#if Is_Windows
                    @"..\..\..\..\" + partialPath));
#else
                    "../../../../" + partialPath))
            .Replace(@"\", "/");
#endif
    }
}
