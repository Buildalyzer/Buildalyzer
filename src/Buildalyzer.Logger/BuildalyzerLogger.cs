using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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

        // Only log what we need for Buildalyzer
        eventSource.ProjectStarted += (_, e) => Pipe!.Write(e);
        eventSource.ProjectFinished += (_, e) => Pipe!.Write(e);
        eventSource.BuildFinished += (_, e) => Pipe!.Write(e);
        eventSource.ErrorRaised += (_, e) => Pipe!.Write(e);
        eventSource.TargetStarted += TargetStarted;
        eventSource.TargetFinished += TargetFinished;
        eventSource.MessageRaised += MessageRaised;

        // Ask MSBuild to log task input parameters, so the compiler task's resolved Sources/References/etc.
        // are available as structured TaskParameter events. This is the same opt-in the binary logger uses;
        // it does not require diagnostic verbosity, and we only forward the compiler task's parameters below.
        // IEventSource4 is deliberately a version sentinel, not the minimal interface: IncludeTaskInputs is
        // declared on IEventSource3 (MSBuild 15.3), but on 15.3-16.3 it only logs task inputs as plain text
        // messages - the structured TaskParameterEventArgs consumed below shipped in 16.4, the same release
        // as IEventSource4. Gating on IEventSource4 both avoids a useless opt-in on 15.3-16.3 and keeps the
        // AnyEventRaised handler (whose body would fail to JIT where TaskParameterEventArgs is missing) off
        // older MSBuilds. Without it Buildalyzer falls back to the evaluation-time items.
        TryIncludeTaskInputs(eventSource);
    }

    // The IEventSource4 test lives in its own method, called through this one: the JIT resolves a type when
    // it compiles the method that names it, not when the test runs, so on an MSBuild whose Framework
    // assembly predates IEventSource4 the method naming it fails to compile with a TypeLoadException. That
    // is caught here, in a method that names none of the newer types, so the logger still initializes and
    // Buildalyzer falls back to the evaluation-time items.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TryIncludeTaskInputs(IEventSource eventSource)
    {
        try
        {
            IncludeTaskInputs(eventSource);
        }
        catch (TypeLoadException)
        {
            // IEventSource4 (MSBuild 16.4+) is not available.
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void IncludeTaskInputs(IEventSource eventSource)
    {
        if (eventSource is IEventSource4 eventSource4)
        {
            eventSource4.IncludeTaskInputs();

            // TaskParameterEventArgs are dispatched via AnyEventRaised (TaskStarted isn't delivered at normal
            // verbosity). Scope forwarding to the CoreCompile target - where the compiler task runs - so we
            // get the compiler's resolved inputs and not, say, the References fed to other tasks. Keeps the
            // pipe lean.
            eventSource.AnyEventRaised += OnAnyEventRaised;
        }
    }

    private void OnAnyEventRaised(object sender, BuildEventArgs e)
    {
        if (e is not TaskParameterEventArgs parameter)
        {
            return;
        }

        // Forward the compiler task's resolved inputs (captured inside CoreCompile) and the item groups the
        // SDK resolves before it - ResolveAssemblyReference's ReferencePath, and the analyzers that
        // ResolveTargetingPackAssets (into Analyzer) and ResolvePackageAssets (into ResolvedAnalyzers) produce -
        // so the workspace can still be reconstructed when the build fails before CoreCompile runs (issue #341).
        bool compilerInput = _coreCompileContexts.Contains(ProjectContextId(parameter))
            && parameter.Kind == TaskParameterMessageKind.TaskInput
            && IsCompilerInput(parameter.ItemType);
        bool resolvedBeforeCompile = parameter.Kind == TaskParameterMessageKind.TaskOutput
            && IsResolvedBeforeCompile(parameter.ItemType);

        if (compilerInput || resolvedBeforeCompile)
        {
            Pipe!.Write(e);
        }
    }

    // The project contexts currently inside CoreCompile. This logger is a central logger, so under /m it sees
    // every project's events interleaved on one thread: a single "in CoreCompile" flag would be cleared by
    // whichever project's CoreCompile finished first, silently dropping the task inputs of every compiler
    // still running. Targets don't nest within a project context, so a set of context ids is enough.
    private readonly HashSet<int> _coreCompileContexts = [];

    // Events without a build context are grouped under a single sentinel id rather than dropped, so target
    // and task events that both lack one still pair up.
    //
    // The id alone is a safe key even under /m: a project-context id is unique across the whole build, not
    // just its own node. MSBuild's LoggingService seeds each node's counter with the node id and advances it
    // by MaxCPUCount + 2, so nodes allocate from disjoint ranges and never collide.
    private static int ProjectContextId(BuildEventArgs e)
        => e.BuildEventContext is BuildEventContext context ? context.ProjectContextId : int.MinValue;

    // MSBuild only forwards ITaskItem[] task inputs at normal verbosity (not scalar string parameters such
    // as DefineConstants), so only the compiler's item-group inputs are listed here. Preprocessor symbols are
    // recovered from the compiler command line instead.
    private static bool IsCompilerInput(string? itemType) => itemType is
        "Sources" or "References" or "Analyzers" or "AdditionalFiles" or "AnalyzerConfigFiles" or "EmbeddedFiles";

    // The item groups the SDK's resolution tasks output ahead of CoreCompile, kept so a build that fails before
    // the compiler runs still yields its resolved references and analyzers. MSBuild item names are
    // case-insensitive, unlike the task parameter names above.
    private static bool IsResolvedBeforeCompile(string? itemType) =>
        string.Equals(itemType, "ReferencePath", StringComparison.OrdinalIgnoreCase)
        || string.Equals(itemType, "Analyzer", StringComparison.OrdinalIgnoreCase)
        || string.Equals(itemType, "ResolvedAnalyzers", StringComparison.OrdinalIgnoreCase);

    private void TargetStarted(object sender, TargetStartedEventArgs e)
    {
        // Only send the CoreCompile target
        if (e.TargetName == "CoreCompile")
        {
            _coreCompileContexts.Add(ProjectContextId(e));
            Pipe!.Write(e);
        }
    }

    private void TargetFinished(object sender, TargetFinishedEventArgs e)
    {
        // Only send the CoreCompile target
        if (e.TargetName == "CoreCompile")
        {
            _coreCompileContexts.Remove(ProjectContextId(e));
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
