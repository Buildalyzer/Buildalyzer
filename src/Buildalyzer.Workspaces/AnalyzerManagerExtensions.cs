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
        workspace.WorkspaceChanged += (sender, args) => logger?.LogDebug("Workspace changed: {Kind}{NewLine}", args.Kind, System.Environment.NewLine);
        workspace.WorkspaceFailed += (sender, args) => logger?.LogError("Workspace failed: {Diagnostic}{NewLine}", args.Diagnostic, System.Environment.NewLine);
        return workspace;
    }

    public static AdhocWorkspace GetWorkspace(this IAnalyzerManager manager)
    {
        // Run builds in parallel
        var results = Guard.NotNull(manager).Projects.Values
            .AsParallel()
            .Select(p => p.Build().FirstOrDefault())
            .OfType<IAnalyzerResult>()
            .ToList();

        // Create a new workspace and add the solution (if there was one)
        AdhocWorkspace workspace = manager.CreateWorkspace();
        if (manager.Solution is { } solution)
        {
            string solutionPath = solution.Path.ToString();
            Microsoft.CodeAnalysis.SolutionInfo solutionInfo = Microsoft.CodeAnalysis.SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Default, solutionPath);
            workspace.AddSolution(solutionInfo);

            // Sort the projects so the order that they're added to the workspace is the same order as the solution.
            // IOPath's equality/hash honour the file system's case sensitivity, so the lookup is robust across
            // platforms; projects not found in the solution sort last.
            Dictionary<IOPath, int> order = [];
            for (int i = 0; i < solution.Projects.Length; i++)
            {
                order[solution.Projects[i].Path] = i;
            }

            results = [.. results.OrderBy(p => order.TryGetValue(IOPath.Parse(p.ProjectFilePath), out int index) ? index : int.MaxValue)];
        }

        // Add each result to the new workspace (sorted in solution order above, if we have a solution)
        foreach (IAnalyzerResult result in results)
        {
            // Check for duplicate project files and don't add them
            if (workspace.CurrentSolution.Projects.All(p => p.FilePath != result.ProjectFilePath))
            {
                result.AddToWorkspace(workspace, true);
            }
        }
        return workspace;
    }
}
