using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Buildalyzer.Workspaces;

public static class ProjectAnalyzerExtensions
{
    /// <summary>
    /// Gets a Roslyn workspace for the analyzed project. Note that this will rebuild the project. Use an <see cref="AnalyzerResult"/> instead if you already have one available.
    /// </summary>
    /// <param name="analyzer">The Buildalyzer project analyzer.</param>
    /// <param name="addProjectReferences">
    /// <c>true</c> to add projects to the workspace for project references that exist in the same <see cref="AnalyzerManager"/>.
    /// If <c>true</c> this will trigger (re)building all referenced projects. Directly add <see cref="AnalyzerResult"/> instances instead if you already have them available.
    /// </param>
    /// <returns>A Roslyn workspace.</returns>
    public static AdhocWorkspace GetWorkspace(this IProjectAnalyzer analyzer, bool addProjectReferences = false)
    {
        Guard.NotNull(analyzer);
        AdhocWorkspace workspace = analyzer.Manager.CreateWorkspace();
        AddToWorkspace(analyzer, workspace, addProjectReferences);
        return workspace;
    }

    /// <summary>
    /// Adds a project to an existing Roslyn workspace. Note that this will rebuild the project. Use an <see cref="AnalyzerResult"/> instead if you already have one available.
    /// </summary>
    /// <param name="analyzer">The Buildalyzer project analyzer.</param>
    /// <param name="workspace">A Roslyn workspace.</param>
    /// <param name="addProjectReferences">
    /// <c>true</c> to add projects to the workspace for project references that exist in the same <see cref="AnalyzerManager"/>.
    /// If <c>true</c> this will trigger (re)building all referenced projects. Directly add <see cref="AnalyzerResult"/> instances instead if you already have them available.
    /// </param>
    /// <returns>
    /// The newly added Roslyn project. A multi-targeted project is added as one project per target
    /// framework (named <c>Name(tfm)</c>, matching MSBuildWorkspace); the first target framework's
    /// project is returned, and every framework is present in <c>workspace.CurrentSolution</c>.
    /// </returns>
    public static Project AddToWorkspace(this IProjectAnalyzer analyzer, Workspace workspace, bool addProjectReferences = false)
    {
        Guard.NotNull(analyzer);
        Guard.NotNull(workspace);

        HashSet<string> visited = new(System.StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<ProjectId> ids = AnalyzerResultExtensions.AddAnalyzer(analyzer, workspace, addProjectReferences, visited);
        return ids.Count > 0 ? workspace.CurrentSolution.GetProject(ids[0]) : null;
    }
}
