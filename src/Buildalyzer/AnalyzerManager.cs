extern alias StructuredLogger;
using System.Collections.Concurrent;
using System.IO;
using Buildalyzer.IO;
using Buildalyzer.Logging;
using Microsoft.Build.Construction;
using Microsoft.Extensions.Logging;
using StructuredLogger::Microsoft.Build.Logging.StructuredLogger;

namespace Buildalyzer;

public class AnalyzerManager : IAnalyzerManager
{
    internal static readonly SolutionProjectType[] SupportedProjectTypes =
    [
        SolutionProjectType.KnownToBeMSBuildFormat,
        SolutionProjectType.WebProject
    ];

    private readonly ConcurrentDictionary<string, IProjectAnalyzer> _projects = new();

    public IReadOnlyDictionary<string, IProjectAnalyzer> Projects => _projects;

    public ILoggerFactory? LoggerFactory { get; set; }

    internal ConcurrentDictionary<string, string> GlobalProperties { get; } = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal ConcurrentDictionary<string, string> EnvironmentVariables { get; } = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// This maps Roslyn project IDs to full normalized project file paths of references (since the Roslyn Project doesn't provide access to this data)
    /// which allows us to match references with Roslyn projects that already exist in the Workspace/Solution (instead of rebuilding them).
    /// This cache exists in <see cref="AnalyzerManager"/> so that it's lifetime can be controlled and it can be collected when <see cref="AnalyzerManager"/> goes out of scope.
    /// </summary>
#pragma warning disable SA1401 // Fields should be private
    internal ConcurrentDictionary<Guid, string[]> WorkspaceProjectReferences = new();
#pragma warning restore SA1401 // Fields should be private

    public string? SolutionFilePath => Solution?.Path.ToString();

    [Obsolete("Use Solution instead.")]
    public SolutionFile? SolutionFile => Solution?.Reference as SolutionFile;

    public SolutionInfo? Solution { get; }

    public AnalyzerManager(AnalyzerManagerOptions? options = null)
        : this(IOPath.Empty, options)
    {
    }

    [Obsolete("Use AnalyzerManager(IOPath, AnalyzerManagerOptions) instead.")]
    public AnalyzerManager(string solutionFilePath, AnalyzerManagerOptions? options = null)
        : this(IOPath.Parse(solutionFilePath), options) { }

    public AnalyzerManager(IOPath solutionFilePath, AnalyzerManagerOptions? options = null)
    {
        options ??= new AnalyzerManagerOptions();
        LoggerFactory = options.LoggerFactory;

        if (solutionFilePath.HasValue)
        {
            Solution = SolutionInfo.Load(solutionFilePath, p => options.ProjectFilter?.Invoke(p) ?? true);

            // init projects. 
            foreach (var proj in Solution)
            {
                var analyzer = new ProjectAnalyzer(this, proj.Path, proj);
                _projects.TryAdd(proj.Path.ToString(), analyzer);
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

    [Obsolete("Use GetProject(IOPath) instead.")]
    public IProjectAnalyzer? GetProject(string projectFilePath) => GetProject(IOPath.Parse(projectFilePath));

    public IProjectAnalyzer? GetProject(IOPath projectFilePath) => GetProject(projectFilePath, null);

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

    private IProjectAnalyzer? GetProject(IOPath path, ProjectInfo? project)
    {
        Guard.NotDefault(path);

        if (!Guard.NotDefault(path).File()!.Exists)
        {
            return project is not null
                ? null
                : throw new ArgumentException($"The path {path} could not be found.");
        }

        return _projects.GetOrAdd(path.ToString(), new ProjectAnalyzer(this, path, project));
    }

    [Obsolete("Use IOPath instead.")]
    internal static string? NormalizePath(string? path) =>
        path == null ? null : Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
}
