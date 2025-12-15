using Buildalyzer.Construction;
using Buildalyzer.IO;
using Microsoft.Build.Framework;

namespace Buildalyzer.Handling;

/// <summary>Handles the <see cref="ProjectStartedEventArgs"/> build event.</summary>
public sealed class ProjectStartedHandler : BuildEventHandlerBase<ProjectStartedEventArgs>
{
    /// <inheritdoc />
    protected override void Apply(ProjectStartedEventArgs e, BuildEventHandlerContext context)
        => context.Update(e, analysis =>
        {
            var projectFile = IOPath.Parse(e.ProjectFile) is { HasValue: true } pf ? pf : analysis.ProjectFile;

            var properties = analysis.Properties.Count > 0
                ? analysis.Properties
                : CompilerProperties.FromDictionaryEntries(e.Properties);

            var items = analysis.Items.Count > 0
                ? analysis.Items
                : CompilerItemsCollection.FromDictionaryEntries(e.Items);

            var tfm = properties.TryGet(ProjectFileNames.TargetFramework)
                ?? properties.TryGet(ProjectFileNames.TargetFrameworkIdentifier)
                ?? properties.TryGet(ProjectFileNames.TargetFrameworkVersion);

            // Restore is not communicated via TargetStarted, but is important to know.
            var targetName = analysis.TargetName is null && e.TargetNames is "Restore"
                ? e.TargetNames
                : analysis.TargetName;

            return analysis with
            {
                ProjectFile = projectFile,
                Properties = properties,
                Items = items,
                TargetFramework = tfm is { } prop ? prop.StringValue : null,
                TargetName = targetName,
                Started = e.Timestamp,
                Events = analysis.Events.Add(e),
            };
        });
}
