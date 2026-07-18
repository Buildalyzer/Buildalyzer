using System.IO;
using Buildalyzer.Construction;
using Buildalyzer.IO;
using Buildalyzer.Logging;

namespace Buildalyzer;

[DebuggerDisplay("{DebuggerDisplay}")]
public class AnalyzerResult : IAnalyzerResult
{
    private readonly Dictionary<string, string> _properties = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IProjectItem[]> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Guid _projectGuid;

    // Compiler inputs collected from the Csc/Vbc/Fsc task's resolved input parameters (structured items,
    // no command-line parsing), plus the raw compiler command line captured from the build.
    private readonly Dictionary<string, List<CompilerInputItem>> _taskInputs = new(StringComparer.OrdinalIgnoreCase);
    private string? _commandLineText;
    private CompilerLanguage _commandLineLanguage;
    private CompilerCommand? _compilerCommand;
    private bool _compilerCommandBuilt;

    /// <summary>
    /// The compiler command, built lazily from the collected task-input parameters and the raw compiler
    /// command line once the build has produced them.
    /// </summary>
    public CompilerCommand? CompilerCommand
    {
        get
        {
            if (!_compilerCommandBuilt)
            {
                _compilerCommandBuilt = true;
                _compilerCommand = CompilerCommandBuilder.Build(
                    _commandLineLanguage,
                    Path.GetDirectoryName(ProjectFilePath) ?? string.Empty,
                    _commandLineText,
                    _taskInputs);
            }

            return _compilerCommand;
        }
    }

    internal AnalyzerResult(string projectFilePath, AnalyzerManager manager, ProjectAnalyzer analyzer)
    {
        ProjectFilePath = projectFilePath;
        Manager = manager;
        Analyzer = analyzer;

        string projectGuid = GetProperty(nameof(ProjectGuid));
        if (string.IsNullOrEmpty(projectGuid) || !Guid.TryParse(projectGuid, out _projectGuid))
        {
            _projectGuid = analyzer == null
                ? Buildalyzer.ProjectGuid.Create(ProjectFilePath)
                : analyzer.ProjectGuid;
        }
    }

    /// <inheritdoc/>
    public string ProjectFilePath { get; }

    public AnalyzerManager Manager { get; }

    /// <inheritdoc/>
    public ProjectAnalyzer Analyzer { get; }

    public bool Succeeded { get; internal set; }

    public IReadOnlyDictionary<string, string> Properties => _properties;

    public IReadOnlyDictionary<string, IProjectItem[]> Items => _items;

    /// <inheritdoc/>
    public Guid ProjectGuid => _projectGuid;

    /// <inheritdoc/>
    public string Command => CompilerCommand?.Text ?? string.Empty;

    /// <inheritdoc/>
    public string CompilerFilePath => CompilerCommand?.CompilerLocation?.ToString() ?? string.Empty;

    /// <inheritdoc/>
    public string[] CompilerArguments => CompilerCommand?.Arguments.ToArray() ?? [];

    /// <inheritdoc/>
    public string GetProperty(string name) =>
        Properties.TryGetValue(name, out string value) ? value : null;

    public string TargetFramework =>
        ProjectFile.GetTargetFrameworks(
            null,  // Don't want all target frameworks since the result is just for one
            [GetProperty(ProjectFileNames.TargetFramework)],
            [(GetProperty(ProjectFileNames.TargetFrameworkIdentifier), GetProperty(ProjectFileNames.TargetFrameworkVersion))])
        .FirstOrDefault();

    public string[] SourceFiles =>
      CompilerCommand?.SourceFiles.ToArray() ?? [];

    public string[] References =>
        CompilerCommand?.MetadataReferences.ToArray() ?? [];

    public ImmutableDictionary<string, ImmutableArray<string>> ReferenceAliases =>
        CompilerCommand?.Aliases ?? ImmutableDictionary<string, ImmutableArray<string>>.Empty;

    public ImmutableHashSet<string> ReferencesEmbeddingInteropTypes =>
        CompilerCommand?.EmbedInteropTypes ?? ImmutableHashSet<string>.Empty;

    public string[] AnalyzerReferences =>
          CompilerCommand?.AnalyzerReferences.ToArray() ?? [];

    public string[] PreprocessorSymbols => CompilerCommand?.PreprocessorSymbolNames.ToArray() ?? [];

    public string[] AdditionalFiles =>
          CompilerCommand?.AdditionalFiles.ToArray() ?? [];

    public string[] AnalyzerConfigFiles =>
          CompilerCommand?.AnalyzerConfigPaths.ToArray() ?? [];

    public IEnumerable<string> ProjectReferences =>
        Items.TryGetValue("ProjectReference", out IProjectItem[] items)
            ? items.Distinct(new ProjectItemItemSpecEqualityComparer())
                   .Select(x => IOPath.Parse(
                        Path.Combine(Path.GetDirectoryName(ProjectFilePath), x.ItemSpec)).Root().ToString())
            : [];

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PackageReferences =>
        Items.TryGetValue("PackageReference", out IProjectItem[] items)
            ? items.Distinct(new ProjectItemItemSpecEqualityComparer()).ToDictionary(x => x.ItemSpec, x => x.Metadata)
            : [];

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private string DebuggerDisplay
    {
        get
        {
            var sb = new StringBuilder();

            sb
                .Append(Path.GetFileName(ProjectFilePath))
                .Append($", TFM = {TargetFramework}")
                .Append($", Properties = {Properties.Count}")
                .Append($", Items = {Items.Count}")
                .Append($", Source Files = {SourceFiles.Length}");

            if (!Succeeded)
            {
                sb.Append(", Succeeded = false");
            }

            if (ProjectReferences.Any())
            {
                sb.Append($", Project References = {ProjectReferences.Count()}");
            }
            if (AdditionalFiles is { Length: > 0 } af)
            {
                sb.Append($", Additional Files = {af.Length}");
            }
            if (PackageReferences is { Count: > 0 } pr)
            {
                sb.Append($", Package References = {pr.Count}");
            }
            return sb.ToString();
        }
    }

    internal void ProcessProject(PropertiesAndItems propertiesAndItems)
    {
        // Add properties
        foreach (var entry in propertiesAndItems.Properties)
        {
            _properties[entry.Key] = entry.StringValue;
        }

        // Add items
        foreach (var items in propertiesAndItems.Items)
        {
            _items[items.Key] = [.. items.Values.Select(task => new ProjectItem(task))];
        }
    }

    /// <summary>True once a compiler command line has been captured for this result.</summary>
    internal bool HasCommandLine => _commandLineText is { Length: > 0 };

    /// <summary>Records a compiler task's resolved input parameter (a structured item group with metadata).</summary>
    internal void AddTaskParameterInput(string itemType, IEnumerable<CompilerInputItem> items)
    {
        if (!_taskInputs.TryGetValue(itemType, out var list))
        {
            list = [];
            _taskInputs[itemType] = list;
        }

        list.AddRange(items);
    }

    internal void ProcessCscCommandLine(string? commandLine, bool coreCompile)
    {
        // Some projects can have multiple Csc calls (see #92) so if this is the one inside CoreCompile use it, otherwise use the first.
        if (string.IsNullOrWhiteSpace(commandLine) || (_commandLineText is not null && !coreCompile))
        {
            return;
        }

        _commandLineText = commandLine;
        _commandLineLanguage = CompilerLanguage.CSharp;
    }

    internal void ProcessVbcCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return;
        }

        _commandLineText = commandLine;
        _commandLineLanguage = CompilerLanguage.VisualBasic;
    }

    internal void ProcessFscCommandLine(string? commandLine)
    {
        // F# writes its command line as an Fsc message; keep the first one seen inside CoreCompile.
        if (string.IsNullOrWhiteSpace(commandLine) || _commandLineText is not null)
        {
            return;
        }

        _commandLineText = commandLine;
        _commandLineLanguage = CompilerLanguage.FSharp;
    }

    private sealed class ProjectItemItemSpecEqualityComparer : IEqualityComparer<IProjectItem>
    {
        public bool Equals(IProjectItem x, IProjectItem y) => x.ItemSpec.Equals(y.ItemSpec, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(IProjectItem obj) => obj.ItemSpec.ToLowerInvariant().GetHashCode();
    }
}
