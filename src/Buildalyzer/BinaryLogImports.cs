namespace Buildalyzer;

/// <summary>
/// Controls how MSBuild collects the project files (and imports) in a binary log,
/// mirroring MSBuild's <c>/bl:ProjectImports=</c> switch values.
/// </summary>
public enum BinaryLogImports
{
    /// <summary>Do not collect project imports.</summary>
    None = 0,

    /// <summary>Embed the project imports in the binary log (default).</summary>
    Embed = 1,

    /// <summary>Collect the project imports in an external .ProjectImports.zip file.</summary>
    ZipFile = 2,
}
