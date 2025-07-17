using System.IO;
using Buildalyzer.Construction;
using Buildalyzer.Logging;

namespace Buildalyzer;

[DebuggerDisplay("{DebuggerDisplay}")]
public class AnalyzerResult : IAnalyzerResult
{
    private readonly Dictionary<string, string> _properties = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IProjectItem[]> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Guid _projectGuid;

    public CompilerCommand CompilerCommand { get; private set; }

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
      CompilerCommand?.SourceFiles.Select(file => file.ToString()).ToArray() ?? [];

    public string[] References =>
        CompilerCommand?.MetadataReferences.ToArray() ?? [];

    public ImmutableDictionary<string, ImmutableArray<string>> ReferenceAliases =>
        CompilerCommand?.Aliases ?? ImmutableDictionary<string, ImmutableArray<string>>.Empty;

    public string[] AnalyzerReferences =>
          CompilerCommand?.AnalyzerReferences.Select(r => r.ToString()).ToArray() ?? [];

    public string[] PreprocessorSymbols => CompilerCommand?.PreprocessorSymbolNames.ToArray() ?? [];

    public string[] AdditionalFiles =>
          CompilerCommand?.AdditionalFiles.Select(file => file.ToString()).ToArray() ?? [];

    public IEnumerable<string> ProjectReferences =>
        Items.TryGetValue("ProjectReference", out IProjectItem[] items)
            ? items.Distinct(new ProjectItemItemSpecEqualityComparer())
                   .Select(x => AnalyzerManager.NormalizePath(
                        Path.Combine(Path.GetDirectoryName(ProjectFilePath), x.ItemSpec)))
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

    internal void ProcessCscCommandLine(string commandLine, bool coreCompile)
    {
        // Some projects can have multiple Csc calls (see #92) so if this is the one inside CoreCompile use it, otherwise use the first
        if (string.IsNullOrWhiteSpace(commandLine) || (CompilerCommand != null && !coreCompile))
        {
            return;
        }
        CompilerCommand = Compiler.CommandLine.Parse(new FileInfo(ProjectFilePath).Directory, commandLine, CompilerLanguage.CSharp);
    }

    internal void ProcessVbcCommandLine(string commandLine)
    {
        CompilerCommand = Compiler.CommandLine.Parse(new FileInfo(ProjectFilePath).Directory, commandLine, CompilerLanguage.VisualBasic);
    }

    internal void ProcessFscCommandLine(string commandLine)
    {
        CompilerCommand = Compiler.CommandLine.Parse(new FileInfo(ProjectFilePath).Directory, commandLine, CompilerLanguage.FSharp);
    }

    private sealed class ProjectItemItemSpecEqualityComparer : IEqualityComparer<IProjectItem>
    {
        public bool Equals(IProjectItem x, IProjectItem y) => x.ItemSpec.Equals(y.ItemSpec, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(IProjectItem obj) => obj.ItemSpec.ToLowerInvariant().GetHashCode();
    }
}