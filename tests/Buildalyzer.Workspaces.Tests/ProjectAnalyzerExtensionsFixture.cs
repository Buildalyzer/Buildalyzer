using System.IO;
using System.Threading.Tasks;
using Buildalyzer.TestTools;
using Microsoft.CodeAnalysis;
using Shouldly;

namespace Buildalyzer.Workspaces.Tests;

[TestFixture]
[NonParallelizable]
public class ProjectAnalyzerExtensionsFixture
{
    [Test]
    public void Loads_Workspace()
    {
        using var ctx = Context.ForProject(@"SdkNetStandardProject\SdkNetStandardProject.csproj");

        var workspace = ctx.Analyzer.GetWorkspace();

        ctx.Log.ToString().Should().NotContain("Workspace failed");
        workspace.CurrentSolution.Projects.First().Documents.First().Should().BeEquivalentTo(new { Name = "Class1.cs" });
    }

    [Test]
    public void LoadsSolution()
    {
        // Given
        string solutionPath = GetFullPath(@"projects\TestProjects.sln");
        SafeStringWriter log = new SafeStringWriter();
        AnalyzerManager manager = new AnalyzerManager(solutionPath, new AnalyzerManagerOptions { LogWriter = log });

        // When
        Workspace workspace = manager.GetWorkspace();

        // Then
        string logged = log.ToString();
        logged.ShouldNotContain("Workspace failed");
        workspace.CurrentSolution.FilePath.ShouldBe(solutionPath);
        workspace.CurrentSolution.Projects.ShouldContain(p => p.Name == "LegacyFrameworkProject");
        workspace.CurrentSolution.Projects.ShouldContain(p => p.Name == "SdkFrameworkProject");
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
        logged.ShouldNotContain("Workspace failed");
        compilation.GetSymbolsWithName(x => x == "Class1").ShouldNotBeEmpty(log.ToString());
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
        logged.ShouldNotContain("Workspace failed");
        compilationOptions.OutputKind.ShouldBe(OutputKind.DynamicallyLinkedLibrary, log.ToString());
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
        logged.ShouldNotContain("Workspace failed");
        workspace.CurrentSolution.Projects.Count().ShouldBe(totalProjects, log.ToString());
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
        logged.ShouldNotContain("Workspace failed");
        workspace.CurrentSolution.Projects.Count().ShouldBe(totalProjects, log.ToString());
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
        logged.ShouldNotContain("Workspace failed");
        compilation.GetSymbolsWithName(x => x == "Class1").ShouldBeEmpty(log.ToString());
        compilation.GetSymbolsWithName(x => x == "Class2").ShouldNotBeEmpty(log.ToString());
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
        logged.ShouldNotContain("Workspace failed");
        project.AnalyzerReferences.ShouldContain(reference => reference.Display == "Microsoft.CodeQuality.Analyzers");
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
        logged.ShouldNotContain("Workspace failed");
        project.AdditionalDocuments.Select(d => d.Name).ShouldBe(["message.txt"]);
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

        diagnostics.ShouldBeEmpty();
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

        diagnostics.ShouldBeEmpty();
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

        diagnostics.ShouldBeEmpty();
    }

#if Is_Windows
    [Test]
    public void HandlesWpfCustomControlLibrary()
    {
        // Given
        SafeStringWriter log = new SafeStringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"projects\WpfCustomControlLibrary1\WpfCustomControlLibrary1.csproj", log);

        // When
        AdhocWorkspace workspace = analyzer.GetWorkspace();
        Project project = workspace.CurrentSolution.Projects.First();

        // Then
        string logged = log.ToString();
        logged.ShouldNotContain("Workspace failed");
        project.ShouldNotBeNull();
        project.Documents.ShouldNotBeEmpty();
    }
#endif

    private IProjectAnalyzer GetProjectAnalyzer(string projectFile, StringWriter log, AnalyzerManager manager = null)
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