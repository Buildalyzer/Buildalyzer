using System.IO;
using System.Xml.Linq;

namespace Buildalyzer.Construction;

/// <summary>
/// Encapsulates an MSBuild project file and provides some information about it's format.
/// This class only parses the existing XML and does not perform any evaluation.
/// </summary>
public class ProjectFile : IProjectFile
{
    /// <summary>
    /// These imports are known to require a .NET Framework host and build tools.
    /// </summary>
    public static readonly string[] ImportsThatRequireNetFramework =
    [
        "Microsoft.Portable.CSharp.targets",
        "Microsoft.Windows.UI.Xaml.CSharp.targets"
    ];

    private readonly XDocument _document;
    private readonly XElement _projectElement;

    // The project file path should already be normalized
    internal ProjectFile(string path)
    {
        Path = path;
        Name = new FileInfo(path).Name;
        _document = XDocument.Load(path);

        // Get the project element
        _projectElement = _document.GetDescendants(ProjectFileNames.Project).FirstOrDefault()
            ?? throw new ArgumentException("Unrecognized project file format");
    }

    /// <inheritdoc />
    public string Path { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string[] TargetFrameworks => field
        ??= GetTargetFrameworks(
            _projectElement.GetDescendants(ProjectFileNames.TargetFrameworks).Select(x => x.Value),
            _projectElement.GetDescendants(ProjectFileNames.TargetFramework).Select(x => x.Value),
            _projectElement.GetDescendants(ProjectFileNames.TargetFrameworkVersion)
                .Select(x => (x.Parent.GetDescendants(ProjectFileNames.TargetFrameworkIdentifier).FirstOrDefault()?.Value ?? ".NETFramework", x.Value)));

    /// <inheritdoc />
    public bool UsesSdk =>
        _projectElement.GetAttributeValue(ProjectFileNames.Sdk) != null
            || _projectElement.GetDescendants(ProjectFileNames.Import).Any(x => x.GetAttributeValue(ProjectFileNames.Sdk) != null);

    /// <inheritdoc />
    public bool RequiresNetFramework =>
        _projectElement.GetDescendants(ProjectFileNames.Import).Any(x => ImportsThatRequireNetFramework.Exists(i => x.GetAttributeValue(ProjectFileNames.Project)?.IsMatchEnd(i) ?? false))
        || _projectElement.GetDescendants(ProjectFileNames.LanguageTargets).Any(x => ImportsThatRequireNetFramework.Exists(i => x.Value.IsMatchEnd(i)))
        || ToolsVersion != null;

    /// <inheritdoc />
    public bool IsMultiTargeted => _projectElement.GetDescendants(ProjectFileNames.TargetFrameworks).Any();

    /// <inheritdoc />
    public string OutputType => _projectElement.GetDescendants(ProjectFileNames.OutputType).FirstOrDefault()?.Value;

    /// <inheritdoc />
    public bool ContainsPackageReferences => _projectElement.GetDescendants(ProjectFileNames.PackageReference).Any();

    /// <inheritdoc />
    public IReadOnlyList<IPackageReference> PackageReferences => _projectElement.GetDescendants(ProjectFileNames.PackageReference).Select(s => new PackageReference(s)).ToList();

    /// <summary>
    /// The absolute paths of the <c>ProjectReference</c> items declared directly in the project XML.
    /// </summary>
    /// <remarks>
    /// Internal, best-effort scheduling hint - deliberately not part of <see cref="IProjectFile"/>. It
    /// parses the project XML only, so references added by MSBuild logic, injected by SDKs, hidden behind
    /// conditions, or expanded from globs/properties are not seen. It exists solely to discover the
    /// reference closure cheaply, up front, so the workspace can build it in one parallel wave; anything
    /// missed is built on demand, so correctness never depends on this being complete.
    /// </remarks>
    internal IReadOnlyList<string> ProjectReferences => field ??=
    [
        .. _projectElement.GetDescendants(ProjectFileNames.ProjectReference)
            .Select(x => x.GetAttributeValue(ProjectFileNames.Include))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(TryResolveReferencePath)
            .Where(x => x is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
    ];

    // ProjectReference Include paths are project-relative and typically use Windows separators
    // (e.g. "..\Library\Library.csproj"); normalize and resolve against the project directory. A
    // malformed Include (invalid path characters, an unexpanded $(property)) must not take down the
    // parse - this is only a best-effort hint - so an unresolvable entry is dropped rather than thrown.
    private string TryResolveReferencePath(string include)
    {
        try
        {
            return System.IO.Path.GetFullPath(
                include.Replace('\\', System.IO.Path.DirectorySeparatorChar),
                System.IO.Path.GetDirectoryName(Path)!);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public string ToolsVersion => _projectElement.GetAttributeValue(ProjectFileNames.ToolsVersion);

    internal static string[] GetTargetFrameworks(
        IEnumerable<string> targetFrameworksValues,
        IEnumerable<string> targetFrameworkValues,
        IEnumerable<(string, string)> targetFrameworkIdentifierAndVersionValues)
    {
        // Use TargetFrameworks and/or TargetFramework if either were found
        IEnumerable<string> allTargetFrameworks = null;
        if (targetFrameworksValues != null)
        {
            allTargetFrameworks = targetFrameworksValues
                .Where(x => x != null)
                .SelectMany(x => x.Split([';'], StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()));
        }
        if (targetFrameworkValues != null)
        {
            allTargetFrameworks = allTargetFrameworks == null
                ? targetFrameworkValues.Where(x => x != null).Select(x => x.Trim())
                : allTargetFrameworks.Concat(targetFrameworkValues.Where(x => x != null).Select(x => x.Trim()));
        }
        if (allTargetFrameworks != null)
        {
            string[] distinctTargetFrameworks = [.. allTargetFrameworks.Distinct()];
            if (distinctTargetFrameworks.Length > 0)
            {
                // Only return if we actually found any
                return distinctTargetFrameworks;
            }
        }

        // Otherwise, try to find a TargetFrameworkIdentifier and/or TargetFrameworkVersion and puzzle it out
        // This is really hacky, would be great to find an official mapping
        // This is also unreliable because a particular TargetFrameworkIdentifier could result from different TargetFramework
        // For example, both "win" and "uap" TargetFramework map back to ".NETCore" TargetFrameworkIdentifier
        return targetFrameworkIdentifierAndVersionValues?
            .Where(value => value.Item1 != null && value.Item2 != null)
            .Select(value =>
            {
                // If we have a mapping, use it
                if (TargetFrameworkIdentifierToTargetFramework.TryGetValue(value.Item1, out (string, bool) targetFramework))
                {
                    // Append the TargetFrameworkVersion, stripping non-digits (this probably isn't correct in some cases)
                    return targetFramework.Item1 + new string([.. value.Item2.Where(x => char.IsDigit(x) || (targetFramework.Item2 && x == '.'))]);
                }

                // Otherwise ¯\_(ツ)_/¯
                return null;
            })
            .Where(x => x != null).ToArray() ?? [];
    }

    // Map from TargetFrameworkIdentifier back to a TargetFramework
    // Partly from https://github.com/onovotny/sdk/blob/83d93a58c0955386218d536580eac2ab1582b397/src/Tasks/Microsoft.NET.Build.Tasks/build/Microsoft.NET.TargetFrameworkInference.targets
    // See also https://blog.stephencleary.com/2012/05/framework-profiles-in-net.html
    // Can't handle ".NETPortable" because those split out as complex "portable-" TargetFramework
    // Value = (TargetFramework, preserve dots in version)
    private static readonly Dictionary<string, (string, bool)> TargetFrameworkIdentifierToTargetFramework = new()
    {
        { ".NETStandard", ("netstandard", true) },
        { ".NETCoreApp", ("netcoreapp", true) },
        { ".NETFramework", ("net", false) },
        { ".NETCore", ("uap", true) },
        { "WindowsPhoneApp", ("wpa", false) },
        { "WindowsPhone", ("wp", false) },
        { "Xamarin.iOS", ("xamarinios", false) },
        { "MonoAndroid", ("monoandroid", false) },
        { "Xamarin.TVOS", ("xamarintvos", false) },
        { "Xamarin.WatchOS", ("xamarinwatchos", false) },
        { "Xamarin.Mac", ("xamarinmac", false) }
    };
}
