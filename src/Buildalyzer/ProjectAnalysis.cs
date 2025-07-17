using Buildalyzer.IO;
using Microsoft.Build.Framework;

namespace Buildalyzer;

public record ProjectAnalysis
{
    /// <summary>
    /// The projectId, commenly resolved from <see cref="ProjectStartedEventArgs.ProjectId"/>.
    /// </summary>
    public int ProjectId { get; init; }

    /// <summary>The project file (location).</summary>
    public IOPath ProjectFile { get; init; }

    /// <summary>The parsed compiler command.</summary>
    public CompilerCommand? Command { get; init; }

    /// <summary>The compiler properties.</summary>
    public CompilerProperties Properties { get; init; } = CompilerProperties.Empty;

    /// <summary>The compiler items.</summary>
    public CompilerItemsCollection Items { get; init; } = CompilerItemsCollection.Empty;

    /// <summary>The target framework of the project compilation.</summary>
    public string? TargetFramework { get; init; }

    /// <summary>The target name of the project.</summary>
    public string? TargetName { get; init; }

    /// <summary>Indiciated if the build succeeded (null if not finalized).</summary>
    public bool? Succeeded { get; init; }

    /// <summary>Start time.</summary>
    public DateTime Started { get; init; }

    /// <summary>Finihs time.</summary>
    public DateTime Finished { get; init; }

    /// <summary>Gets the duration of the build.</summary>
    public TimeSpan Duration => Finished - Started;

    /// <summary>The compiled source files.</summary>
    public ImmutableArray<IOPath> SourceFiles { get; init; } = [];

    /// <summary>The available addtional files for further analysis.</summary>
    public ImmutableArray<IOPath> AdditionalFiles { get; init; } = [];

    /// <summary>The available embedded files for further analysis.</summary>
    public ImmutableArray<IOPath> EmbeddedFiles { get; init; } = [];

    /// <summary>Build errors.</summary>
    public ImmutableArray<BuildErrorEventArgs> Errors { get; init; } = [];

    /// <summary>Involved events.</summary>
    public ImmutableArray<BuildEventArgs> Events { get; init; } = [];
}
