using System;
using System.Linq;
using Microsoft.Build.Framework;
using XenoAtom.MsBuildPipeLogger;

namespace Buildalyzer.Logger;

public class BuildalyzerLogger : PipeLogger
{
    private string _pipeHandleAsString = string.Empty;
    private bool _logEverything;

    public override void Initialize(IEventSource eventSource)
    {
        // Parse the parameters
        string[] parameters = [.. (Parameters?.Split(';').Select(x => x.Trim())).OfType<string>()];
        if (parameters.Length != 2)
        {
            throw new LoggerException("Unexpected number of parameters");
        }
        _pipeHandleAsString = parameters[0];
        if (!bool.TryParse(parameters[1], out _logEverything))
        {
            throw new LoggerException("Second parameter (log everything) should be a bool");
        }

        base.Initialize(eventSource);
    }

    protected override void InitializeEnvironmentVariables()
    {
        // Only register the extra logging environment variables if logging everything
        if (_logEverything)
        {
            base.InitializeEnvironmentVariables();
        }
    }

    protected override IPipeWriter InitializePipeWriter() => new AnonymousPipeWriter(_pipeHandleAsString);

    protected override void InitializeEvents(IEventSource eventSource)
    {
        if (eventSource is null)
        {
            throw new ArgumentNullException(nameof(eventSource));
        }

        if (_logEverything)
        {
            base.InitializeEvents(eventSource);
            return;
        }

        // Ask MSBuild to log task input parameters, so the compiler task's resolved Sources/References/etc.
        // are available as structured TaskParameter events. This is the same opt-in the binary logger uses;
        // it does not require diagnostic verbosity, and we only forward the compiler task's parameters below.
        if (eventSource is IEventSource4 eventSource4)
        {
            eventSource4.IncludeTaskInputs();
        }

        // Only log what we need for Buildalyzer
        eventSource.ProjectStarted += (_, e) => Pipe!.Write(e);
        eventSource.ProjectFinished += (_, e) => Pipe!.Write(e);
        eventSource.BuildFinished += (_, e) => Pipe!.Write(e);
        eventSource.ErrorRaised += (_, e) => Pipe!.Write(e);
        eventSource.TargetStarted += TargetStarted;
        eventSource.TargetFinished += TargetFinished;
        eventSource.MessageRaised += MessageRaised;

        // TaskParameterEventArgs are dispatched via AnyEventRaised (TaskStarted isn't delivered at normal
        // verbosity). Scope forwarding to the CoreCompile target - where the compiler task runs - so we get
        // the compiler's resolved inputs and not, say, the References fed to other tasks. Keeps the pipe lean.
        eventSource.AnyEventRaised += (_, e) =>
        {
            if (e is not TaskParameterEventArgs parameter)
            {
                return;
            }

            // Forward the compiler task's resolved inputs (captured inside CoreCompile) and the references
            // that ResolveAssemblyReference resolved before it - the latter so the workspace can still be
            // reconstructed when the build fails before CoreCompile runs (issue #341).
            bool compilerInput = _inCoreCompile
                && parameter.Kind == TaskParameterMessageKind.TaskInput
                && IsCompilerInput(parameter.ItemType);
            bool resolvedReferences = parameter.Kind == TaskParameterMessageKind.TaskOutput
                && string.Equals(parameter.ItemType, "ReferencePath", StringComparison.OrdinalIgnoreCase);

            if (compilerInput || resolvedReferences)
            {
                Pipe!.Write(e);
            }
        };
    }

    private bool _inCoreCompile;

    // MSBuild only forwards ITaskItem[] task inputs at normal verbosity (not scalar string parameters such
    // as DefineConstants), so only the compiler's item-group inputs are listed here. Preprocessor symbols are
    // recovered from the compiler command line instead.
    private static bool IsCompilerInput(string? itemType) => itemType is
        "Sources" or "References" or "Analyzers" or "AdditionalFiles" or "AnalyzerConfigFiles" or "EmbeddedFiles";

    private void TargetStarted(object sender, TargetStartedEventArgs e)
    {
        // Only send the CoreCompile target
        if (e.TargetName == "CoreCompile")
        {
            _inCoreCompile = true;
            Pipe!.Write(e);
        }
    }

    private void TargetFinished(object sender, TargetFinishedEventArgs e)
    {
        // Only send the CoreCompile target
        if (e.TargetName == "CoreCompile")
        {
            _inCoreCompile = false;
            Pipe!.Write(e);
        }
    }

    private void MessageRaised(object sender, BuildMessageEventArgs e)
    {
        // Only send if in the Csc Vbc, or the Fsc task
        if ((e is TaskCommandLineEventArgs cmd &&
            string.Equals(cmd.TaskName, "Csc", StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(e.SenderName, "Fsc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.SenderName, "Vbc", StringComparison.OrdinalIgnoreCase))
        {
            Pipe!.Write(e);
        }
    }
}
