using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Buildalyzer.Differential.Tests;

/// <summary>
/// Loads the same project both with Buildalyzer and with Roslyn's own
/// <see cref="MSBuildWorkspace"/> and exposes the two resulting Roslyn <see cref="Project"/>
/// instances side by side so tests can diff them. MSBuildWorkspace is treated as the
/// reference implementation.
/// </summary>
public sealed class WorkspaceComparison : IDisposable
{
    private readonly IDisposable _buildalyzerWorkspace;
    private readonly MSBuildWorkspace _msbuildWorkspace;

    private WorkspaceComparison(
        Project buildalyzer,
        IDisposable buildalyzerWorkspace,
        string buildalyzerLog,
        Project msbuild,
        MSBuildWorkspace msbuildWorkspace,
        IReadOnlyList<string> msbuildFailures)
    {
        Buildalyzer = buildalyzer;
        BuildalyzerLog = buildalyzerLog;
        MSBuild = msbuild;
        MSBuildFailures = msbuildFailures;
        _buildalyzerWorkspace = buildalyzerWorkspace;
        _msbuildWorkspace = msbuildWorkspace;
    }

    /// <summary>The primary project as loaded by Buildalyzer.</summary>
    public Project Buildalyzer { get; }

    /// <summary>The primary project as loaded by MSBuildWorkspace (the reference).</summary>
    public Project MSBuild { get; }

    /// <summary>The verbose log written by Buildalyzer while building.</summary>
    public string BuildalyzerLog { get; }

    /// <summary>Any diagnostics raised by MSBuildWorkspace's <c>WorkspaceFailed</c> event.</summary>
    public IReadOnlyList<string> MSBuildFailures { get; }

    /// <param name="projectPath">The project to load.</param>
    /// <param name="targetFramework">
    /// For a multi-targeted project, the single target framework to compare (e.g. <c>net8.0</c>).
    /// Both loaders produce one Roslyn project per target framework, so a specific one must be
    /// chosen to compare like for like. Leave <c>null</c> for single-targeted projects.
    /// </param>
    public static async Task<WorkspaceComparison> LoadAsync(string projectPath, string? targetFramework = null)
    {
        System.Diagnostics.Stopwatch msbuildTimer = System.Diagnostics.Stopwatch.StartNew();
        (Project msbuild, MSBuildWorkspace msbuildWorkspace, IReadOnlyList<string> msbuildFailures) =
            await LoadWithMSBuildWorkspaceAsync(projectPath, targetFramework);
        msbuildTimer.Stop();

        System.Diagnostics.Stopwatch buildalyzerTimer = System.Diagnostics.Stopwatch.StartNew();
        (Project buildalyzer, IDisposable buildalyzerWorkspace, string buildalyzerLog) =
            LoadWithBuildalyzer(projectPath, targetFramework);
        buildalyzerTimer.Stop();

        LoadTimings.Record(TestContext.CurrentContext.Test.Name, buildalyzerTimer.Elapsed, msbuildTimer.Elapsed);

        return new WorkspaceComparison(
            buildalyzer,
            buildalyzerWorkspace,
            buildalyzerLog,
            msbuild,
            msbuildWorkspace,
            msbuildFailures);
    }

    private static (Project Project, IDisposable Workspace, string Log) LoadWithBuildalyzer(string projectPath, string? targetFramework)
    {
        SafeStringWriter log = new();
        AnalyzerManager manager = new(new AnalyzerManagerOptions { LogWriter = log });
        IProjectAnalyzer analyzer = manager.GetProject(projectPath)
            ?? throw new InvalidOperationException($"Buildalyzer could not load the project '{projectPath}'.");

        // For a single target the default GetWorkspace() picks Build().First(); for a multi-targeted
        // project select the requested target's result and build a workspace from just that one.
        AdhocWorkspace workspace = targetFramework is null
            ? analyzer.GetWorkspace(addProjectReferences: true)
            : analyzer.Build()[targetFramework].GetWorkspace(addProjectReferences: true);

        Project project = workspace.CurrentSolution.Projects.FirstOrDefault(p => PathsEqual(p.FilePath, projectPath))
            ?? throw new InvalidOperationException(
                $"Buildalyzer did not add the project '{projectPath}' to the workspace.{System.Environment.NewLine}{log}");

        return (project, workspace, log.ToString());
    }

    private static async Task<(Project Project, MSBuildWorkspace Workspace, IReadOnlyList<string> Failures)> LoadWithMSBuildWorkspaceAsync(string projectPath, string? targetFramework)
    {
        List<string> failures = [];
        MSBuildWorkspace workspace = MSBuildWorkspace.Create();
        using (workspace.RegisterWorkspaceFailedHandler(e => failures.Add(e.Diagnostic.Message)))
        {
            Project project = await workspace.OpenProjectAsync(projectPath);

            // A multi-targeted project loads as one Roslyn project per framework, each named
            // "<Project>(<tfm>)"; pick the requested one so the comparison is like for like.
            if (targetFramework is not null)
            {
                project = project.Solution.Projects.FirstOrDefault(
                        p => PathsEqual(p.FilePath, projectPath)
                        && p.Name.EndsWith($"({targetFramework})", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"MSBuildWorkspace did not load target framework '{targetFramework}' for '{projectPath}'.");
            }

            return (project, workspace, failures);
        }
    }

    private static bool PathsEqual(string? left, string? right) =>
        left is not null
        && right is not null
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _buildalyzerWorkspace.Dispose();
        _msbuildWorkspace.Dispose();
    }
}
