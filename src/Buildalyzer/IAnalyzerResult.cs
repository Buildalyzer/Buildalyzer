namespace Buildalyzer;

public interface IAnalyzerResult
{
    /// <summary>
    /// Gets the <see cref="ProjectAnalyzer"/> that generated this result
    /// or <c>null</c> if the result came from a binary log file.
    /// </summary>
    ProjectAnalyzer Analyzer { get; }

    IReadOnlyDictionary<string, IProjectItem[]> Items { get; }

    AnalyzerManager Manager { get; }

    /// <summary>
    /// Contains the <c>PackageReference</c> items for the project.
    /// The key is a package ID and the value is a <see cref="IReadOnlyDictionary{TKey, TValue}"/>
    /// that includes all the package reference metadata, typically including a "Version" key.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PackageReferences { get; }

    /// <summary>
    /// The full normalized path to the project file.
    /// </summary>
    string ProjectFilePath { get; }

    /// <summary>
    /// Gets a GUID for the project. This first attempts to get the <c>ProjectGuid</c>
    /// MSBuild property. If that's not available, checks for a GUID from the
    /// solution (if originally provided). If neither of those are available, it
    /// will generate a UUID GUID by hashing the project path relative to the solution path (so it's repeatable).
    /// </summary>
    Guid ProjectGuid { get; }

    IEnumerable<string> ProjectReferences { get; }

    IReadOnlyDictionary<string, string> Properties { get; }

    string[] References { get; }

    ImmutableDictionary<string, ImmutableArray<string>> ReferenceAliases { get; }

    string[] AnalyzerReferences { get; }

    string[] SourceFiles { get; }

    bool Succeeded { get; }

    string TargetFramework { get; }

    string[] PreprocessorSymbols { get; }

    string[] AdditionalFiles { get; }

    /// <summary>
    /// Gets the compiler command produced by the CoreCompile task.
    /// This is <see cref="CompilerFilePath"/> + <see cref="CompilerArguments"/>.
    /// </summary>
    string Command { get; }

    /// <summary>
    /// Gets the path of the compiler command produced by the CoreCompile task.
    /// </summary>
    string CompilerFilePath { get; }

    /// <summary>
    /// Gets the arguments passed to the compiler produced by the CoreCompile task.
    /// </summary>
    string[] CompilerArguments { get; }

    /// <summary>
    /// Gets the value of the specified property and returns <c>null</c>
    /// if the property could not be found.
    /// </summary>
    /// <param name="name">The name of the property.</param>
    /// <returns>The value of the property or <c>null</c>.</returns>
    string GetProperty(string name);
}
