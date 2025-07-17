using Microsoft.Build.Framework;

namespace Buildalyzer.Handling;

/// <summary>Handles the <see cref="TargetStartedEventArgs"/> build event.</summary>
public sealed class TargetStartedHandler : BuildEventHandlerBase<TargetStartedEventArgs>
{
    /// <inheritdoc />
    protected override void Apply(TargetStartedEventArgs e, BuildEventHandlerContext context)
        => context.Update(e, analysis => analysis with { TargetName = e.TargetName });
}
