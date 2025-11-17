using Microsoft.Build.Framework;

namespace Buildalyzer.Handling;

/// <summary>Handles the FSC (F#) build event.</summary>
public sealed class FSharpBuildCommandHandler : BuildEventHandlerBase<BuildMessageEventArgs>
{
    /// <inheritdoc />
    /// <remarks>
    /// The check on <see cref="FSharpCommandLineParser.SplitCommandLineIntoArguments(string?)"/>
    /// is performed to filter out messages similar to:
    /// Microsoft (R) F# Compiler version 13.9.300.0 for F# 9.0
    /// Which are communicated on TargetName = restore.
    /// </remarks>
    protected override bool CanHandle(BuildMessageEventArgs e, BuildEventHandlerContext context)
        => e.SenderName.IsMatch("FSC")
        && FSharpCommandLineParser.SplitCommandLineIntoArguments(e.Message) is { Length: > 0 };

    /// <inheritdoc />
    protected override void Apply(BuildMessageEventArgs e, BuildEventHandlerContext context)
        => context.Update(e, analysis =>
        {
            var command = Compiler.CommandLine.Parse(analysis.ProjectFile.File()?.Directory, e.Message!, CompilerLanguage.FSharp);

            return analysis with
            {
                Command = command,
                Events = analysis.Events.Add(e),
            };
        });
}
