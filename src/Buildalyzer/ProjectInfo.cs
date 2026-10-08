using System.IO;
using System.Xml;
using Buildalyzer.IO;
using Microsoft.VisualStudio.SolutionPersistence.Model;

namespace Buildalyzer;

/// <summary>Represents info about the MS Build solution file.</summary>
[DebuggerDisplay("{DebuggerDisplay}")]
public sealed class ProjectInfo
{
    private ProjectInfo(object reference, IOPath path, Guid guid, IEnumerable<string> tfms)
    {
        Reference = reference;
        Location = path;
        Path = path.ToString();
        Guid = guid;
        TargetFrameworks = [.. tfms];
    }

    /// <summary>The GUID of the project.</summary>
    public Guid Guid { get; }

    /// <summary>The path to the project.</summary>
    public string Path { get; }

    /// <summary>The normalized path to the project, used for file-system-aware comparisons.</summary>
    internal IOPath Location { get; }

    /// <summary>Gets the target framework(s) of the project.</summary>
    public ImmutableArray<string> TargetFrameworks { get; }

    /// <summary>Gets the reference object.</summary>
    public object Reference { get; }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private string DebuggerDisplay => $"{Location.File()?.Name}, TFM = {string.Join(", ", TargetFrameworks)}";

    [Pure]
    internal static ProjectInfo New(SolutionProjectModel reference, IOPath root)
    {
        // Canonicalize: solution files may reference projects with relative segments
        // (e.g. "../DependentProject/DependentProject.csproj") that MSBuild reports back fully resolved.
        var path = IOPath.Parse(System.IO.Path.GetFullPath(root.Combine(Guard.NotNull(reference).FilePath).ToString()));
        var prop = reference.FindProperties("TargetFrameworks")
            ?? reference.FindProperties("TargetFramework");

        return new(reference, path, reference.Id, prop?.Values ?? TargetFrameworksOf(path));
    }

    /// <remarks>
    /// The solution only carries target frameworks in a project's property bag when something put them
    /// there, which an ordinary .sln or .slnx entry does not, so they are read from the project file. A
    /// project the solution lists but that is missing or unreadable (an unloaded project, say) has none
    /// rather than failing the whole solution.
    /// </remarks>
    [Pure]
    private static string[] TargetFrameworksOf(IOPath path)
    {
        if (path.File() is not { Exists: true } file)
        {
            return [];
        }

        try
        {
            return new Construction.ProjectFile(file.FullName).TargetFrameworks;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or ArgumentException)
        {
            return [];
        }
    }
}
