using System;
using System.Linq;
using Microsoft.Build.Framework;
using MsBuildPipeLogger;

namespace Buildalyzer.Logger;

public class BuildalyzerLogger : PipeLogger
{
    private string _pipeHandleAsString;
    private bool _logEverything;

    public override void Initialize(IEventSource eventSource)
    {
        // Parse the parameters
        string[] parameters = [..(Parameters?.Split(';').Select(x => x.Trim())).OfType<string>()];
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
        eventSource.ProjectStarted += (_, e) => Pipe.Write(e);
        eventSource.ProjectFinished += (_, e) => Pipe.Write(e);
        eventSource.BuildFinished += (_, e) => Pipe.Write(e);
        eventSource.ErrorRaised += (_, e) => Pipe.Write(e);
        eventSource.TargetStarted += TargetStarted;
        eventSource.TargetFinished += TargetFinished;
        eventSource.MessageRaised += MessageRaised;
    }

    private void TargetStarted(object sender, TargetStartedEventArgs e)
    {
        // Only send the CoreCompile target
        if (e.TargetName == "CoreCompile")
        {
            Pipe.Write(e);
        }
    }

    private void TargetFinished(object sender, TargetFinishedEventArgs e)
    {
        // Only send the CoreCompile target
        if (e.TargetName == "CoreCompile")
        {
            Pipe.Write(e);
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
            Pipe.Write(e);
        }
    }
}
