namespace Buildalyzer.Construction;

public interface IProjectFile
{
    /// <summary>
    /// Whether the project file contains <c>PackageReference</c> items.
    /// </summary>
    bool ContainsPackageReferences { get; }

    /// <summary>
    /// Whether the project file is multi-targeted.
    /// </summary>
    /// <remarks>
    /// Checks for an <c>TargetFrameworks</c> element.
    /// </remarks>
    bool IsMultiTargeted { get; }

    /// <summary>
    /// The list of <c>PackageReference</c> items in the project file.
    /// </summary>
    IReadOnlyList<IPackageReference> PackageReferences { get; }

    /// <summary>
    /// The absolute paths of the <c>ProjectReference</c> items declared in the project file.
    /// </summary>
    /// <remarks>
    /// This only parses the project XML; it does not evaluate the project, so references added by
    /// MSBuild logic or hidden behind conditions are not included. It is a fast, best-effort way to
    /// discover the project-reference graph without building.
    /// </remarks>
    IReadOnlyList<string> ProjectReferences { get; }

    /// <summary>
    /// The full path to the project file.
    /// </summary>
    string Path { get; }

    /// <summary>
    /// Project name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Whether the project file requires a .NET Framework host and build tools to build.
    /// </summary>
    /// <remarks>
    /// Checks for an <c>Import</c> element with a <c>Project</c> attribute ending with one of the targets in <see cref="ProjectFile.ImportsThatRequireNetFramework"/>.
    /// Also looks for a <c>LanguageTargets</c> ending with one of the targets in <see cref="ProjectFile.ImportsThatRequireNetFramework"/>.
    /// Projects that use these targets are known not to build under a .NET Core host or build tools.
    /// Also checks for a <c>ToolsVersion</c> attribute and uses the .NET Framework if one is found.
    /// </remarks>
    bool RequiresNetFramework { get; }

    /// <summary>
    /// The target framework(s) in the project file.
    /// </summary>
    /// <remarks>
    /// This does not perform evaluation of the project file, only parsing.
    /// If TargetFramework or TargetFrameworks contains a property that
    /// needs to be evaluated, this will contain the pre-evaluated value(s).
    /// Try to find a TargetFrameworkIdentifier in the same PropertyGroup
    /// and if no TargetFrameworkIdentifier was found, assume ".NETFramework".
    /// </remarks>
    string[] TargetFrameworks { get; }

    /// <summary>
    /// Gets the <c>ToolsVersion</c> attribute of the <c>Project</c> element (or <c>null</c> if there isn't one).
    /// </summary>
    string ToolsVersion { get; }

    /// <summary>
    /// Whether the project file uses an SDK.
    /// </summary>
    /// <remarks>
    /// Checks for an <c>Sdk</c> attribute on the <c>Project</c> element. If one can't be found,
    /// also checks for <c>Import</c> elements with an <c>Sdk</c> attribute (see https://github.com/Microsoft/msbuild/issues/1493).
    /// </remarks>
    bool UsesSdk { get; }

    /// <summary>
    /// The output type of the project.
    /// </summary>
    /// <remarks>
    /// Checks for an <c>OutputType</c> element.
    /// </remarks>
    string OutputType { get; }
}
