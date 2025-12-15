using Microsoft.Build.Framework;

namespace Buildalyzer.Handling;

/// <summary>Handles the CSC (C#) build event.</summary>
public sealed class CSharpBuildCommandHandler : BuildEventHandlerBase<TaskCommandLineEventArgs>
{
    /// <inheritdoc />
    protected override bool CanHandle(TaskCommandLineEventArgs e, BuildEventHandlerContext context)
        => e.CommandLine is { Length: > 0 }
        && e.TaskName.IsMatch("CSC");

    /// <inheritdoc />
    protected override void Apply(TaskCommandLineEventArgs e, BuildEventHandlerContext context)
        => context.Update(e, analysis =>
        {
            var command = Compiler.CommandLine.Parse(analysis.ProjectFile.File()?.Directory, e.CommandLine, CompilerLanguage.CSharp);

            return analysis with
            {
                Command = command,
                Events = analysis.Events.Add(e),
            };
        });
    }
