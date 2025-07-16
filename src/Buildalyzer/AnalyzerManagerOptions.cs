using System.IO;
using Buildalyzer.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.SolutionPersistence.Model;

namespace Buildalyzer;

public class AnalyzerManagerOptions
{
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// A filter that indicates whether a give project should be loaded.
    /// Return <c>true</c> to load the project, <c>false</c> to filter it out.
    /// </summary>
    public Func<SolutionProjectModel, bool>? ProjectFilter { get; set; }

    public TextWriter? LogWriter
    {
        set
        {
            if (value == null)
            {
                LoggerFactory = null;
                return;
            }

            LoggerFactory ??= new LoggerFactory();
            LoggerFactory.AddProvider(new TextWriterLoggerProvider(value));
        }
    }
}
