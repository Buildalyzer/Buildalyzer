
using Microsoft.CodeAnalysis;

namespace Buildalyzer;

public abstract record RoslynBasedCompilerCommand<TArguments> : CompilerCommand
    where TArguments : CommandLineArguments
{
    /// <summary>The Roslyn comppiler arguments.</summary>
    public TArguments? CommandLineArguments { get; init; }
}
