#pragma warning disable CA1710 // Identifiers should have correct suffix: being a collection is not its main purpose.

using System.Threading.Tasks;
using Buildalyzer.IO;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Buildalyzer;

/// <summary>Represents info about the MS Build solution file.</summary>
[DebuggerTypeProxy(typeof(Diagnostics.CollectionDebugView<ProjectInfo>))]
[DebuggerDisplay("{Path.File().Name}, Count = {Count}")]
public sealed class SolutionInfo : IReadOnlyCollection<ProjectInfo>
{
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly Dictionary<Guid, ProjectInfo> Lookup;

    private SolutionInfo(object reference, IOPath path, IEnumerable<ProjectInfo> projects)
    {
        Reference = reference;
        Path = path;
        Projects = [.. projects];
        Lookup = Projects.ToDictionary(p => p.Guid, p => p);
    }

    /// <summary>The path to the solution.</summary>
    public IOPath Path { get; }

    /// <summary>The projects in the solution.</summary>
    public ImmutableArray<ProjectInfo> Projects { get; }

    /// <summary>Tries to get a project based on its <see cref="ProjectInfo.Guid"/>.</summary>
    public ProjectInfo? this[Guid projectGuid] => Lookup[projectGuid];

    /// <summary>Gets the reference object.</summary>
    public object Reference { get; }

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
    public static SolutionInfo Load(IOPath path, Predicate<ProjectInfo>? filter = null)
    {
        var serializer = SolutionSerializers.GetSerializerByMoniker(path.ToString())
            ?? throw new NotSupportedException($"No solution serializer available for {path}.");

        var solution = serializer.OpenAsync(path.ToString(), default).Sync();
        var root = IOPath.Parse(path.File()?.Directory?.FullName);
        var projects = solution.SolutionProjects
            .Select(p => ProjectInfo.New(p, root))
            .Where(p => (filter?.Invoke(p) ?? true) && p.Path.File() is { Exists: true });

        return new(solution, path, projects);
    }
}
