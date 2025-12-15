using Microsoft.Build.Framework;

namespace Buildalyzer.Handling;

/// <summary>Handles the <see cref="ProjectEvaluationFinishedEventArgs"/> build event.</summary>
public sealed class ProjectEvaluationFinishedHandler : BuildEventHandlerBase<ProjectEvaluationFinishedEventArgs>
{
    /// <inheritdoc />
    /// <remarks>
    /// In binlog 14 we need to gather properties and items during evaluation
    /// and "glue" them with the project event args But can never remove
    /// <see cref="ProjectStartedEventArgs"/>:
    /// "even v14 will log them on ProjectStarted if any legacy loggers are present (for compat)"
    /// See https://twitter.com/KirillOsenkov/status/1427686459713019904.
    /// </remarks>
    protected override void Apply(ProjectEvaluationFinishedEventArgs e, BuildEventHandlerContext context)
        => context.Update(e, analysis => analysis with
        {
            Properties = CompilerProperties.FromDictionaryEntries(e.Properties),
            Items = CompilerItemsCollection.FromDictionaryEntries(e.Items),
            Events = analysis.Events.Add(e),
        });
}
