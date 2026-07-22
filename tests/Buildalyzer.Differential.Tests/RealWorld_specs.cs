using Microsoft.CodeAnalysis;

namespace Buildalyzer.Differential.Tests;

/// <summary>
/// The same differential comparison as <see cref="Differential_specs"/> — Buildalyzer versus
/// Roslyn's <see cref="Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace"/>, with MSBuildWorkspace
/// as the reference — but run against real open-source projects cloned from GitHub instead of
/// projects authored in a temp directory. The point is to see whether Buildalyzer agrees with
/// Roslyn on the messy, real-world project files people actually ship.
/// </summary>
/// <remarks>
/// These are <see cref="ExplicitAttribute">explicit</see>: they clone repositories over the
/// network and run two design-time builds each, so they are slow and offline-hostile. Run them
/// on demand, e.g. <c>dotnet test --filter "TestCategory=RealWorld"</c>, or a single one with
/// <c>--filter "FullyQualifiedName~Serilog"</c>. Each repository is pinned to a release tag so a
/// run is reproducible; the repo's own <c>global.json</c> is replaced with the SDK the test run
/// selected so both loaders build on identical MSBuild.
/// </remarks>
[TestFixture]
[NonParallelizable]
[Explicit("Clones OSS repositories over the network; slow. Run with --filter \"TestCategory=RealWorld\".")]
[Category("RealWorld")]
public class RealWorld_specs
{
    /// <summary>A real project to load: where to clone it from and which single framework to compare.</summary>
    /// <param name="Name">A short display name for the test case.</param>
    /// <param name="GitUrl">The clone URL.</param>
    /// <param name="Tag">The tag to pin the clone to, for reproducibility.</param>
    /// <param name="ProjectPath">The repo-relative path of the project to load.</param>
    /// <param name="TargetFramework">
    /// The single framework to compare for a multi-targeted project (both loaders produce one
    /// Roslyn project per framework). Chosen to be one that restores and builds on any OS under a
    /// current SDK — e.g. <c>net8.0</c> or <c>netstandard2.0</c>, never a Windows-only flavour.
    /// </param>
    public sealed record Repo(string Name, string GitUrl, string Tag, string ProjectPath, string TargetFramework)
    {
        public override string ToString() => Name;
    }

    private static readonly Repo[] Repositories =
    [
        new(
            "MediatR",
            "https://github.com/jbogard/MediatR.git",
            "v9.0.0",
            "src/MediatR/MediatR.csproj",
            "netstandard2.0"),
        new(
            "Serilog",
            "https://github.com/serilog/serilog.git",
            "v4.3.0",
            "src/Serilog/Serilog.csproj",
            "net8.0"),
        new(
            "FluentValidation",
            "https://github.com/FluentValidation/FluentValidation.git",
            "11.11.0",
            "src/FluentValidation/FluentValidation.csproj",
            "net8.0"),
        new(
            "Polly.Core",
            "https://github.com/App-vNext/Polly.git",
            "8.7.0",
            "src/Polly.Core/Polly.Core.csproj",
            "net8.0"),
    ];

    [TestCaseSource(nameof(Repositories))]
    public async Task Buildalyzer_matches_reference(Repo repo)
    {
        ArgumentNullException.ThrowIfNull(repo);

        using OssRepositoryFixture fixture = OssRepositoryFixture.Clone(repo.GitUrl, repo.Tag);
        string projectPath = fixture.ProjectPath(repo.ProjectPath);
        fixture.Restore(projectPath);

        using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath, repo.TargetFramework);

        // Dump both views first so a mismatch is actionable straight from the test output.
        await ReportAsync(repo, comparison);

        string log = comparison.BuildalyzerLog;
        comparison.Buildalyzer.SourceFileNames()
            .Should().BeEquivalentTo(comparison.MSBuild.SourceFileNames(), log);
        comparison.Buildalyzer.MetadataReferenceNames()
            .Should().BeEquivalentTo(comparison.MSBuild.MetadataReferenceNames(), log);
        comparison.Buildalyzer.AnalyzerReferenceNames()
            .Should().BeEquivalentTo(comparison.MSBuild.AnalyzerReferenceNames(), log);
        comparison.Buildalyzer.ProjectReferenceNames()
            .Should().BeEquivalentTo(comparison.MSBuild.ProjectReferenceNames(), log);
    }

    private static async Task ReportAsync(Repo repo, WorkspaceComparison comparison)
    {
        await TestContext.Out.WriteLineAsync($"# {repo.Name} @ {repo.Tag} ({repo.TargetFramework})");
        await WriteSetAsync("source files", comparison.Buildalyzer.SourceFileNames(), comparison.MSBuild.SourceFileNames());
        await WriteSetAsync("metadata references", comparison.Buildalyzer.MetadataReferenceNames(), comparison.MSBuild.MetadataReferenceNames());
        await WriteSetAsync("analyzer references", comparison.Buildalyzer.AnalyzerReferenceNames(), comparison.MSBuild.AnalyzerReferenceNames());
        await WriteSetAsync("project references", comparison.Buildalyzer.ProjectReferenceNames(), comparison.MSBuild.ProjectReferenceNames());

        if (comparison.MSBuildFailures.Count > 0)
        {
            await TestContext.Out.WriteLineAsync(
                $"  MSBuildWorkspace reported {comparison.MSBuildFailures.Count} failure(s) across the whole project "
                + "(may include other target frameworks):");
            foreach (string failure in comparison.MSBuildFailures)
            {
                await TestContext.Out.WriteLineAsync($"    - {failure}");
            }
        }
    }

    private static async Task WriteSetAsync(string label, string[] buildalyzer, string[] msbuild)
    {
        string[] onlyBuildalyzer = [.. buildalyzer.Except(msbuild, StringComparer.OrdinalIgnoreCase)];
        string[] onlyMsbuild = [.. msbuild.Except(buildalyzer, StringComparer.OrdinalIgnoreCase)];

        await TestContext.Out.WriteLineAsync(
            $"  {label}: Buildalyzer={buildalyzer.Length}, MSBuildWorkspace={msbuild.Length}"
            + (onlyBuildalyzer.Length == 0 && onlyMsbuild.Length == 0 ? " (match)" : string.Empty));
        if (onlyBuildalyzer.Length > 0)
        {
            await TestContext.Out.WriteLineAsync($"    only in Buildalyzer:      {string.Join(", ", onlyBuildalyzer)}");
        }

        if (onlyMsbuild.Length > 0)
        {
            await TestContext.Out.WriteLineAsync($"    only in MSBuildWorkspace:  {string.Join(", ", onlyMsbuild)}");
        }
    }
}
