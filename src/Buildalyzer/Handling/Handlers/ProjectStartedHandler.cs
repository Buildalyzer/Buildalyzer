using Buildalyzer.IO;
using Microsoft.Build.Framework;

namespace Buildalyzer.Handling;

public sealed class ProjectStartedHandler : BuildEventHandlerBase<ProjectStartedEventArgs>
{
    protected override void Apply(ProjectStartedEventArgs e, BuildEventHandlerContext context)
        => context.Update(e, analysis =>
        {
            var projectFile = IOPath.Parse(e.ProjectFile) is { HasValue: true } pf ? pf : analysis.ProjectFile;

            return analysis with
            {
                ProjectFile = projectFile,
                Events = analysis.Events.Add(e),
                Started = e.Timestamp,
            };
        });
}
