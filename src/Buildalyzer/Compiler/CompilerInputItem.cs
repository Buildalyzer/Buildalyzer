namespace Buildalyzer;

/// <summary>
/// A single compiler task-input item collected from an MSBuild <c>TaskParameter</c> (TaskInput) event:
/// the item spec together with its metadata. This is the structured replacement for parsing the compiler
/// command line - the resolved compiler inputs (Sources, References, Analyzers, etc.) are read directly
/// from the Csc/Vbc/Fsc task's input parameters.
/// </summary>
internal readonly record struct CompilerInputItem(string Spec, IReadOnlyList<(string Name, string? Value)> Metadata)
{
    public static readonly IReadOnlyList<(string Name, string? Value)> NoMetadata = [];
}
