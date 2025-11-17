using Microsoft.Build.Framework;

namespace Buildalyzer.Handling;

public static class BuildEventHandlers
{
    public static readonly ImmutableArray<BuildEventHandler> Default =
    [
        new BuildErrorHandler(),
        new CSharpBuildCommandHandler(),
        new FSharpBuildCommandHandler(),
        new VisualBasicBuildCommandHandler(),
        new ProjectStartedHandler(),
        new ProjectFinshedHandler(),
        new TargetStartedHandler(),
    ];

    /// <summary>Handles events.</summary>
    /// <param name="handlers">
    /// The handlers to use.
    /// </param>
    /// <param name="events">
    /// The events to apply.
    /// </param>
    /// <param name="context">
    /// The handler context.
    /// </param>
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
