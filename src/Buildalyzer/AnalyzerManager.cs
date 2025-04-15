extern alias StructuredLogger;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Buildalyzer.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.SolutionPersistence;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using StructuredLogger::Microsoft.Build.Logging.StructuredLogger;

namespace Buildalyzer;

public class AnalyzerManager : IAnalyzerManager
{
    private readonly ConcurrentDictionary<string, IProjectAnalyzer> _projects = new ConcurrentDictionary<string, IProjectAnalyzer>();

    public IReadOnlyDictionary<string, IProjectAnalyzer> Projects => _projects;

    public ILoggerFactory LoggerFactory { get; set; }

    internal ConcurrentDictionary<string, string> GlobalProperties { get; } = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal ConcurrentDictionary<string, string> EnvironmentVariables { get; } = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// This maps Roslyn project IDs to full normalized project file paths of references (since the Roslyn Project doesn't provide access to this data)
    /// which allows us to match references with Roslyn projects that already exist in the Workspace/Solution (instead of rebuilding them).
    /// This cache exists in <see cref="AnalyzerManager"/> so that it's lifetime can be controlled and it can be collected when <see cref="AnalyzerManager"/> goes out of scope.
    /// </summary>
#pragma warning disable SA1401 // Fields should be private
    internal ConcurrentDictionary<Guid, string[]> WorkspaceProjectReferences = new ConcurrentDictionary<Guid, string[]>();
#pragma warning restore SA1401 // Fields should be private

    public string SolutionFilePath { get; }

    public SolutionModel SolutionFile { get; }

    public AnalyzerManager(AnalyzerManagerOptions options = null)
        : this(null, options)
    {
    }

    public AnalyzerManager(string solutionFilePath, AnalyzerManagerOptions options = null)
    {
        options ??= new AnalyzerManagerOptions();
        LoggerFactory = options.LoggerFactory;

        if (!string.IsNullOrEmpty(solutionFilePath))
        {
            SolutionFilePath = NormalizePath(solutionFilePath);
            if (SolutionSerializers.GetSerializerByMoniker(SolutionFilePath) is ISolutionSerializer serializer)
            {
                SolutionFile = serializer.OpenAsync(SolutionFilePath, CancellationToken.None).GetAwaiter().GetResult();
                SolutionFile.DistillProjectConfigurations();
            }
            else
            {
                throw new ArgumentException($"The solution file {solutionFilePath} is not supported.");
            }

            // Initialize all the projects in the solution
            foreach (var projectInSolution in SolutionFile.SolutionProjects)
            {
                if (options?.ProjectFilter != null && !options.ProjectFilter(projectInSolution))
                {
                    continue;
                }
                if (!Path.IsPathRooted(projectInSolution.FilePath))
                {
                    projectInSolution.FilePath = Path.GetFullPath(projectInSolution.FilePath);
                }
                GetProject(projectInSolution.FilePath, projectInSolution);
            }
        }
    }

    public void SetGlobalProperty(string key, string value)
    {
        GlobalProperties[key] = value;
    }

    public void RemoveGlobalProperty(string key)
    {
        // Nulls are removed before passing to MSBuild and can be used to ignore values in lower-precedence collections
        GlobalProperties[key] = null;
    }

    public void SetEnvironmentVariable(string key, string value)
    {
        EnvironmentVariables[key] = value;
    }

    public IProjectAnalyzer GetProject(string projectFilePath) => GetProject(projectFilePath, null);

    /// <inheritdoc/>
    public IAnalyzerResults Analyze(string binLogPath, IEnumerable<Microsoft.Build.Framework.ILogger> buildLoggers = null)
    {
        binLogPath = NormalizePath(binLogPath);
        if (!File.Exists(binLogPath))
        {
            throw new ArgumentException($"The path {binLogPath} could not be found.");
        }

        BinLogReader reader = new BinLogReader();

        using EventProcessor eventProcessor = new EventProcessor(this, null, buildLoggers, reader, true);
        reader.Replay(binLogPath);
        return new AnalyzerResults
        {
            { eventProcessor.Results, eventProcessor.OverallSuccess }
        };
    }

    private IProjectAnalyzer GetProject(string projectFilePath, SolutionProjectModel projectInSolution)
    {
        Guard.NotNull(projectFilePath);

        projectFilePath = NormalizePath(projectFilePath);
        if (!File.Exists(projectFilePath))
        {
            if (projectInSolution == null)
            {
                throw new ArgumentException($"The path {projectFilePath} could not be found.");
            }
            return null;
        }
        return _projects.GetOrAdd(projectFilePath, new ProjectAnalyzer(this, projectFilePath, projectInSolution));
    }

    internal static string NormalizePath(string path) =>
        path == null ? null : Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
}