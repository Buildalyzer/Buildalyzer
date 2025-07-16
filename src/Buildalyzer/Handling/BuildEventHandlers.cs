using Microsoft.Build.Framework;

namespace Buildalyzer.Handling;

public static class BuildEventHandlers
{
    public static readonly IReadOnlyCollection<BuildEventHandler> Default =
    [
        new BuildErrorHandler(),
        new CSharpBuildCommandHandler(),
        new FSharpBuildCommandHandler(),
        new VisualBasicBuildCommandHandler(),
        new ProjectStartedHandler(),
        new ProjectFinshedHandler(),
    ];

    [Pure]
    public static ImmutableArray<ProjectAnalysis> Handle(
        this IReadOnlyCollection<BuildEventHandler> handlers,
        IEnumerable<BuildEventArgs> events,
        BuildEventHandlerContext? context = null)
    {
        Guard.NotNull(handlers);
        Guard.NotNull(events);

        context ??= new BuildEventHandlerContext();

        foreach (var e in events)
        {
            // Apply the first match only.
            if (handlers.FirstOrDefault(h => h.Handle(e, context)) is null)
            {
                context.Skipped.Add(e);
            }
        }
        return [.. context.Projects.Values];
    }
}
