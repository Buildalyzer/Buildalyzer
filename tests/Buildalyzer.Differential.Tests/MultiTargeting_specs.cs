using Microsoft.Build.Utilities.ProjectCreation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Buildalyzer.Differential.Tests;

/// <summary>
/// Differential specs for multi-targeting. These pin the two hard problems that the current
/// XML-scan discovery and file-path project-reference wiring get wrong, using MSBuildWorkspace
/// as the reference:
///
/// <list type="number">
///   <item><b>Discovery</b> — the set of target frameworks a project actually builds must come
///   from MSBuild's <em>evaluated</em> <c>TargetFrameworks</c> (honouring conditions such as the
///   Windows-only <c>net472</c> append), never from reading the raw project XML (which yields a
///   phantom <c>$(TargetFrameworks)</c> and off-platform frameworks).</item>
///   <item><b>Nearest-TFM wiring</b> — when a consumer references a multi-targeted library, it
///   must be wired to the exact framework flavour MSBuild resolved (by output-assembly path),
///   e.g. a <c>net10.0</c> app referencing a <c>netstandard2.0;net472</c> library must bind the
///   <c>netstandard2.0</c> output, not <c>net472</c>.</item>
/// </list>
///
/// </summary>
[TestFixture]
[NonParallelizable]
public class MultiTargeting_specs
{
    /// <summary>
    /// A Windows-only <c>net472</c> is appended by composing <c>$(TargetFrameworks)</c> inside an
    /// OS-conditioned property. Buildalyzer must discover exactly the frameworks MSBuild evaluates:
    /// off Windows that is <c>{netstandard2.0, net10.0}</c>; on Windows it also includes
    /// <c>net472</c>. Crucially it must never surface the literal <c>$(TargetFrameworks)</c>.
    /// </summary>
    [Test]
    public async Task Discovered_frameworks_match_reference_and_honour_os_condition()
    {
        using ProjectFixture fixture = new();
        string projectPath = fixture.AddProject(
            "ConditionalTfm",
            p => p
                .Property("TargetFrameworks", "netstandard2.0;net10.0")
                .Property("TargetFrameworks", "$(TargetFrameworks);net472", condition: "'$(OS)' == 'Windows_NT'"),
            Source("Class1.cs", "namespace ConditionalTfm;\npublic class Class1 { }\n"));
        fixture.Restore(projectPath);

        // Reference: the frameworks MSBuildWorkspace evaluates (reads $(TargetFrameworks), honours the condition).
        using MSBuildWorkspace msbuild = MSBuildWorkspace.Create();
        await msbuild.OpenProjectAsync(projectPath);
        string[] reference = TargetFrameworksOf(msbuild.CurrentSolution, projectPath);

        // Buildalyzer's discovered, buildable frameworks.
        SafeStringWriter log = new();
        AnalyzerManager manager = new(new AnalyzerManagerOptions { LogWriter = log });
        string[] actual =
        [
            .. manager.GetProject(projectPath).Build()
                .Where(r => r.Succeeded)
                .Select(r => r.TargetFramework)
                .Where(tfm => !string.IsNullOrWhiteSpace(tfm))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        ];

        // Never surface an unexpanded MSBuild expression as a target framework.
        actual.Should().NotContain(x => x.Contains("$(", StringComparison.Ordinal), log.ToString());

        // Match Roslyn exactly. On non-Windows this asserts net472 is absent; on Windows it asserts it is present.
        reference.Should().Contain("netstandard2.0").And.Contain("net10.0");
        actual.Should().BeEquivalentTo(reference, log.ToString());
    }

    /// <summary>
    /// A <c>net10.0</c> app references a <c>netstandard2.0;net8.0</c> library. There is no exact match,
    /// so MSBuild resolves the nearest compatible flavour - <c>net8.0</c>, not <c>netstandard2.0</c> -
    /// and Buildalyzer must wire the same one (by resolved output-assembly path, not project-file path).
    /// </summary>
    /// <remarks>
    /// The library deliberately targets <c>net8.0</c> rather than <c>net472</c> for the non-netstandard
    /// leg: netstandard2.0 emits no reference assembly, so a design-time build where the nearest resolved
    /// flavour is netstandard2.0 is degenerate even for MSBuildWorkspace (it falls back to its
    /// "no matching metadata reference" path). net8.0 emits a ref assembly, keeping the oracle clean.
    /// The Windows-only net472 dimension is covered by the discovery spec above.
    /// </remarks>
    [Test]
    public async Task Consumer_binds_nearest_framework_of_multi_targeted_reference()
    {
        using ProjectFixture fixture = new();
        string libraryPath = fixture.AddProject(
            "Library",
            p => p.Property("TargetFrameworks", "netstandard2.0;net8.0"),
            Source("Widget.cs", "namespace Library;\npublic class Widget { public int Value => 42; }\n"));
        string appPath = fixture.AddProject(
            "App",
            p => p.Property("TargetFramework", "net10.0"),
            Source("Program.cs", "namespace App;\npublic class Program { public Library.Widget W = new(); }\n"));
        ProjectFixture.AddProjectReference(appPath, libraryPath);
        fixture.Restore(appPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(appPath);
        AssertLoadedCleanly(comparison);

        // The reference resolves to exactly the net8.0 flavour of the library (nearest to net10.0).
        ReferencedFrameworks(comparison.MSBuild)
            .Should().ContainSingle().Which.Should().Be("net8.0");
        ReferencedFrameworks(comparison.Buildalyzer)
            .Should().BeEquivalentTo(ReferencedFrameworks(comparison.MSBuild), comparison.BuildalyzerLog);
    }

    /// <summary>The evaluated target frameworks of a project, read from the per-TFM project names in a solution.</summary>
    private static string[] TargetFrameworksOf(Solution solution, string projectPath) =>
    [
        .. solution.Projects
            .Where(p => PathsEqual(p.FilePath, projectPath))
            .Select(p => ExtractFramework(p.Name))
            .Where(tfm => tfm is not null)
            .Select(tfm => tfm!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>The framework flavours of the projects a project references (from their per-TFM names).</summary>
    private static string[] ReferencedFrameworks(Project project) =>
    [
        .. project.ProjectReferences
            .Select(r => project.Solution.GetProject(r.ProjectId)?.Name)
            .Select(ExtractFramework)
            .Where(tfm => tfm is not null)
            .Select(tfm => tfm!)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>Pulls the "(tfm)" discriminator out of a Roslyn project name, e.g. "Library(net472)" -> "net472".</summary>
    private static string? ExtractFramework(string? projectName)
    {
        if (projectName is null)
        {
            return null;
        }

        int open = projectName.LastIndexOf('(');
        int close = projectName.LastIndexOf(')');
        return open >= 0 && close > open ? projectName[(open + 1)..close] : null;
    }

    private static bool PathsEqual(string? left, string? right) =>
        left is not null
        && right is not null
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void AssertLoadedCleanly(WorkspaceComparison comparison)
    {
        comparison.MSBuildFailures.Should().BeEmpty();
        comparison.BuildalyzerLog.Should().NotContain("Workspace failed");
    }

    private static Dictionary<string, string> Source(string name, string content) => new() { [name] = content };
}
