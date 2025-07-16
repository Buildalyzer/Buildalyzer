using Microsoft.Build.Framework;

namespace Buildalyzer.Handling;

/// <summary>Handles the FSC (F#) build event.</summary>
public sealed class FSharpBuildCommandHandler : BuildEventHandlerBase<BuildMessageEventArgs>
{
    /// <inheritdoc />
    protected override bool CanHandle(BuildMessageEventArgs e, BuildEventHandlerContext context)
        => e.Message is { Length: > 0 }
        && e.SenderName.IsMatch("Fsc");

    /// <inheritdoc />
    protected override void Apply(BuildMessageEventArgs e, BuildEventHandlerContext context)
        => context.Update(e, analysis =>
        {
            var command = Compiler.CommandLine.Parse(analysis.ProjectFile.File()?.Directory, e.Message!, CompilerLanguage.FSharp);

            return analysis with
            {
                Command = command,
                SourceFiles = command.SourceFiles,
                AdditionalFiles = command.AdditionalFiles,
                Events = analysis.Events.Add(e),
            };
        });
    }
