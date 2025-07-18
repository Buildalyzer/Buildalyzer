using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.Construction;
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
        if (manager is null)
        {
            throw new ArgumentNullException(nameof(manager));
        }

        // Run builds in parallel
        List<IAnalyzerResult> results = manager.Projects.Values
            .AsParallel()
            .Select(p => p.Build().FirstOrDefault())
            .Where(x => x != null)
            .ToList();

        // Create a new workspace and add the solution (if there was one)
        AdhocWorkspace workspace = manager.CreateWorkspace();
        if (!string.IsNullOrEmpty(manager.SolutionFilePath))
        {
            Microsoft.CodeAnalysis.SolutionInfo solutionInfo = Microsoft.CodeAnalysis.SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Default, manager.SolutionFilePath);
            workspace.AddSolution(solutionInfo);

            // Sort the projects so the order that they're added to the workspace in the same order as the solution file
            List<ProjectInSolution> projectsInOrder = [.. manager.SolutionFile.ProjectsInOrder];
            results = [.. results.OrderBy(p => projectsInOrder.FindIndex(g => g.AbsolutePath == p.ProjectFilePath))];
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