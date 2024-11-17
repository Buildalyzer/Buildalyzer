#pragma warning disable CA1710 // Identifiers should have correct suffix: being a collection is not its main purpose.

using Buildalyzer.IO;
using Microsoft.Build.Construction;

namespace Buildalyzer;

/// <summary>Represents info about the MS Build solution file.</summary>
[DebuggerTypeProxy(typeof(Diagnostics.CollectionDebugView<ProjectInfo>))]
[DebuggerDisplay("{Path.File().Name}, Count = {Count}")]
public sealed class SolutionInfo : IReadOnlyCollection<ProjectInfo>
{
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly Dictionary<Guid, ProjectInfo> _lookup;

    private SolutionInfo(IOPath path, SolutionFile file, Predicate<ProjectInSolution>? filter)
    {
        Path = path;
        File = file;
        Projects = file.ProjectsInOrder
            .Where(p => (filter?.Invoke(p) ?? true) && System.IO.File.Exists(p.AbsolutePath))
            .Select(ProjectInfo.New)
            .ToImmutableArray();

        _lookup = Projects.ToDictionary(p => p.Guid, p => p);
    }

    /// <summary>The path to the solution.</summary>
    public IOPath Path { get; }

    /// <summary>The <see cref="SolutionFile"/> representation of the solution.</summary>
    internal SolutionFile File { get; }

    /// <inheritdoc cref="SolutionFile.SolutionConfigurations" />
    public IReadOnlyList<SolutionConfigurationInSolution> Configurations => File.SolutionConfigurations;

    /// <summary>The projects in the solution.</summary>
    public ImmutableArray<ProjectInfo> Projects { get; }

    /// <summary>Tries to get a project based on its <see cref="ProjectInfo.Guid"/>.</summary>
    public ProjectInfo? this[Guid projectGuid] => _lookup[projectGuid];

    /// <inheritdoc />
    public int Count => Projects.Length;

    /// <inheritdoc />
    [Pure]
    public IEnumerator<ProjectInfo> GetEnumerator() => ((IReadOnlyCollection<ProjectInfo>)Projects).GetEnumerator();

    /// <inheritdoc />
    [Pure]
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Loads the <see cref="SolutionInfo"/> from disk.</summary>
    /// <param name="path">
    /// The path to load from.
    /// </param>
    /// <param name="filter">
    /// The project to include.
    /// </param>
    [Pure]
    public static SolutionInfo Load(IOPath path, Predicate<ProjectInSolution>? filter = null)
        => new(path, SolutionFile.Parse(path.ToString()), filter);
}
