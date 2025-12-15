using Microsoft.Build.Framework;

namespace Buildalyzer.Handling;

/// <summary>Handles the VBC (VB.NET) build event.</summary>
public sealed class VisualBasicBuildCommandHandler : BuildEventHandlerBase<TaskCommandLineEventArgs>
{
    /// <inheritdoc />
    protected override bool CanHandle(TaskCommandLineEventArgs e, BuildEventHandlerContext context)
        => e.CommandLine is { Length: > 0 }
        && e.TaskName.IsMatch("VBC");

    /// <inheritdoc />
    protected override void Apply(TaskCommandLineEventArgs e, BuildEventHandlerContext context)
        => context.Update(e, analysis =>
        {
            var command = Compiler.CommandLine.Parse(analysis.ProjectFile.File()?.Directory, e.CommandLine, CompilerLanguage.VisualBasic);

            return analysis with
            {
                Command = command,
                Events = analysis.Events.Add(e),
            };
        });
}
