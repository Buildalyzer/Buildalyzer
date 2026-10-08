using System.Collections.Generic;
using Basic.CompilerLog.Util;

namespace Buildalyzer.CompilerLogs;

/// <summary>
/// The result of writing Buildalyzer build results to a compiler log.
/// </summary>
public sealed class CompilerLogCreateResult
{
    internal CompilerLogCreateResult(bool succeeded, IReadOnlyList<CompilerCall> compilerCalls, IReadOnlyList<string> diagnostics)
    {
        Succeeded = succeeded;
        CompilerCalls = compilerCalls;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// <c>true</c> when every build result with a supported compiler command was written to the log.
    /// The log may still be usable when <c>false</c>; <see cref="CompilerCalls"/> lists what was written.
    /// </summary>
    public bool Succeeded { get; }

    /// <summary>
    /// The compiler calls written to the log, one per built target framework.
    /// </summary>
    public IReadOnlyList<CompilerCall> CompilerCalls { get; }

    /// <summary>
    /// Diagnostics produced while writing the log, including those raised by the
    /// underlying compiler log writer (missing files, unreadable content and the like).
    /// </summary>
    public IReadOnlyList<string> Diagnostics { get; }
}
