using System.Collections.Generic;
using System.Linq;
using Buildalyzer.IO;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
namespace Buildalyzer.Workspaces;

public static class AnalyzerManagerExtensions
{
    /// <summary>
    /// Instantiates an empty AdhocWorkspace with logging event handlers.
    /// </summary>
    internal static AdhocWorkspace CreateWorkspace(this IAnalyzerManager manager)
    {
        ILogger logger = manager.LoggerFactory?.CreateLogger<AdhocWorkspace>();
        AdhocWorkspace workspace = new AdhocWorkspace();
        workspace.RegisterWorkspaceChangedHandler(args => logger?.LogDebug("Workspace changed: {Kind}{NewLine}", args.Kind, System.Environment.NewLine));
        workspace.RegisterWorkspaceFailedHandler(args => logger?.LogError("Workspace failed: {Diagnostic}{NewLine}", args.Diagnostic, System.Environment.NewLine));
        return workspace;
    }

    public static AdhocWorkspace GetWorkspace(this IAnalyzerManager manager)
    {
        Guard.NotNull(manager);

        // Create a new workspace and add the solution (if there was one)
        AdhocWorkspace workspace = manager.CreateWorkspace();

        // Add the projects in solution order when we have a solution, otherwise in discovery order.
        IEnumerable<IProjectAnalyzer> analyzers = manager.Projects.Values;
        if (manager.Solution is { } solution)
        {
            string solutionPath = solution.Path;
            Microsoft.CodeAnalysis.SolutionInfo solutionInfo = Microsoft.CodeAnalysis.SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Default, solutionPath);
            workspace.AddSolution(solutionInfo);

            // Sort the projects so the order that they're added to the workspace is the same order as the solution.
            // IOPath's equality/hash honour the file system's case sensitivity, so the lookup is robust across
            // platforms; projects not found in the solution sort last.
            Dictionary<IOPath, int> order = [];
            for (int i = 0; i < solution.Projects.Length; i++)
            {
                order[solution.Projects[i].Location] = i;
            }

            analyzers = manager.Projects.Values
                .OrderBy(p => order.TryGetValue(IOPath.Parse(p.ProjectFile.Path), out int index) ? index : int.MaxValue);
        }

        // Build every project up front in parallel - Build() is safe to run concurrently across
        // projects - then populate the workspace sequentially below (the workspace itself is not
        // thread-safe). AddAnalyzer reuses these results instead of rebuilding.
        IReadOnlyDictionary<string, IAnalyzerResult[]> prebuilt = manager.Projects.Values
            .AsParallel()
            .Select(p => (Path: AnalyzerResultExtensions.NormalizePath(p.ProjectFile.Path), Results: p.Build().Where(r => r.Succeeded).ToArray()))
            .ToList()
            .ToDictionary(x => x.Path, x => x.Results, System.StringComparer.OrdinalIgnoreCase);

        // Add each project - one Roslyn project per target framework - wiring project references by
        // output-assembly path. The shared visited set means each project (and its references) is
        // added exactly once even when several solution projects reference it.
        HashSet<string> visited = new(System.StringComparer.OrdinalIgnoreCase);
        foreach (IProjectAnalyzer analyzer in analyzers)
        {
            AnalyzerResultExtensions.AddAnalyzer(analyzer, workspace, addProjectReferences: true, visited, prebuilt);
        }

        return workspace;
    }
}
