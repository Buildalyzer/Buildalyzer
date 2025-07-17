using Microsoft.Build.Framework;

namespace Buildalyzer.Handling;

/// <summary>Handles the <see cref="BuildErrorEventArgs"/> build event.</summary>
public sealed class BuildErrorHandler : BuildEventHandlerBase<BuildErrorEventArgs>
{
    /// <inheritdoc />
    protected override void Apply(BuildErrorEventArgs e, BuildEventHandlerContext context)
        => context.Update(e, analysis => analysis with
        {
            Errors = analysis.Errors.Add(e),
            Events = analysis.Events.Add(e),
        });
}
