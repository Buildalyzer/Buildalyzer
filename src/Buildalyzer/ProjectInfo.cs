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
        Path = path;
        Guid = guid;
        TargetFrameworks = [.. tfms];
    }

    /// <summary>The GUID of the project.</summary>
    public Guid Guid { get; }

    /// <summary>The path to the protject.</summary>
    public IOPath Path { get; }

    /// <summary>Gets the target framework(s) of the project.</summary>
    public ImmutableArray<string> TargetFrameworks { get; }

    /// <summary>Gets the reference object.</summary>
    public object Reference { get; }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private string DebuggerDisplay => $"{Path.File()?.Name}, TFM = {string.Join(", ", TargetFrameworks)}";

    [Pure]
    internal static ProjectInfo New(SolutionProjectModel reference, IOPath root)
    {
        // Canonicalize: solution files may reference projects with relative segments
        // (e.g. "../DependentProject/DependentProject.csproj") that MSBuild reports back fully resolved.
        var path = IOPath.Parse(System.IO.Path.GetFullPath(root.Combine(Guard.NotNull(reference).FilePath).ToString()));
        var prop = reference.FindProperties("TargetFrameworks")
            ?? reference.FindProperties("TargetFramework");

        return new(reference, path, reference.Id, prop?.Values ?? []);
    }
}
