using Microsoft.Build.Framework;

namespace Buildalyzer.Handling;

/// <summary>Handles the <see cref="ProjectFinishedEventArgs"/> build event.</summary>
public sealed class ProjectFinshedHandler : BuildEventHandlerBase<ProjectFinishedEventArgs>
{
    protected override void Apply(ProjectFinishedEventArgs e, BuildEventHandlerContext context)
        => context.Update(e, analysis => analysis with
        {
            Succeeded = e.Succeeded && analysis.Errors.Length is 0,
            Finished = e.Timestamp,
        });
}
