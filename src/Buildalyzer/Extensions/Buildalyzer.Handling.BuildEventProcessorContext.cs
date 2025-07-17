using Microsoft.Build.Framework;

namespace Buildalyzer.Handling;

internal static class BuildEventProcessorContextExtensions
{
    internal static ProjectAnalysis Get(this BuildEventHandlerContext context, BuildEventArgs args)
    {
        var projectId = args.BuildEventContext!.ProjectInstanceId;
        return Guard.NotNull(context).Projects.TryGetValue(projectId, out var existing)
            ? existing : new ProjectAnalysis() { ProjectId = projectId };
    }

    internal static void Set(this BuildEventHandlerContext context, ProjectAnalysis analysis)
    {
        Guard.NotNull(context).Projects[analysis.ProjectId] = analysis;
    }

    internal static void Update(this BuildEventHandlerContext context, BuildEventArgs args, Func<ProjectAnalysis, ProjectAnalysis> transform)
    {
        var project = Guard.NotNull(context).Get(args);
        context.Projects[project.ProjectId] = transform(project);
    }
}
