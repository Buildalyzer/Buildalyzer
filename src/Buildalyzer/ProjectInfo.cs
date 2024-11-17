using Buildalyzer.Construction;
using Buildalyzer.IO;
using Microsoft.Build.Construction;

namespace Buildalyzer;

/// <summary>Represents info about the MS Build solution file.</summary>
[DebuggerDisplay("{DebuggerDisplay}")]
public sealed class ProjectInfo
{
    private ProjectInfo(IOPath path, Guid guid)
    {
        Path = path;
        File = new ProjectFile(path.ToString());
        Guid = guid;
    }

    /// <summary>The GUID of the project.</summary>
    public Guid Guid { get; }

    /// <summary>The path to the solution.</summary>
    public IOPath Path { get; }

    public IProjectFile File { get; }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private string DebuggerDisplay => $"{Path.File().Name}, TFM = {string.Join(", ", File.TargetFrameworks)}";

    /// <summary>Loads the <see cref="ProjectInfo"/> from disk.</summary>
    /// <param name="path">
    /// The path to load from.
    /// </param>
    [Pure]
    public static ProjectInfo Load(IOPath path)
        => new(path, ProjectGuid.Create(path.File().Name));

    [Pure]
    internal static ProjectInfo New(ProjectInSolution proj)
        => new(IOPath.Parse(proj.AbsolutePath), Guid.Parse(proj.ProjectGuid));
}
