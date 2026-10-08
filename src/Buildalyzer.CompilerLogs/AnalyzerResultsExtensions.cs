using System;
using System.Collections.Generic;
using System.IO;
using Basic.CompilerLog.Util;

namespace Buildalyzer.CompilerLogs;

/// <summary>
/// Creates compiler log (.complog) files from Buildalyzer build results.
/// </summary>
/// <remarks>
/// The compiler log is built from the raw csc/vbc command line Buildalyzer captured from the
/// build, so it carries everything the compiler was actually given — including the emit-time
/// inputs (embedded resources, Win32 manifest/icon/resource, source link, app.config, strong
/// name key, rulesets) that a Roslyn workspace cannot surface. The referenced file contents are
/// read from disk at the time of this call, so create the log while the build outputs are still
/// in place.
/// </remarks>
public static class AnalyzerResultsExtensions
{
    /// <summary>
    /// Creates a compiler log file from the build results and returns the diagnostics.
    /// </summary>
    /// <exception cref="CompilerLogException">The log could not be fully created.</exception>
    public static IReadOnlyList<string> CreateCompilerLog(this IEnumerable<IAnalyzerResult> results, string compilerLogFilePath)
    {
        CompilerLogCreateResult result = TryCreateCompilerLog(results, compilerLogFilePath);
        return result.Succeeded
            ? result.Diagnostics
            : throw new CompilerLogException("Could not create compiler log", [.. result.Diagnostics]);
    }

    /// <summary>
    /// Creates a compiler log file from the build results.
    /// </summary>
    public static CompilerLogCreateResult TryCreateCompilerLog(this IEnumerable<IAnalyzerResult> results, string compilerLogFilePath)
    {
        using FileStream stream = new(compilerLogFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        return TryCreateCompilerLog(results, stream);
    }

    /// <summary>
    /// Writes a compiler log for the build results to the given stream.
    /// </summary>
    public static CompilerLogCreateResult TryCreateCompilerLog(this IEnumerable<IAnalyzerResult> results, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(results);

        List<string> diagnostics = [];
        List<CompilerCall> compilerCalls = [];
        bool succeeded = true;

        // Internal to Basic.CompilerLog.Util, reached via publicizing until an upstream producer API exists.
        using var builder = new CompilerLogBuilder(stream, diagnostics);

        foreach (IAnalyzerResult analyzerResult in results)
        {
            string name = $"{Path.GetFileName(analyzerResult.ProjectFilePath)} ({analyzerResult.TargetFramework})";

            CompilerCommand? command = (analyzerResult as AnalyzerResult)?.CompilerCommand;
            if (command is null)
            {
                diagnostics.Add($"{name}: no compiler command line was captured; the build may have failed before the compiler was invoked.");
                succeeded = false;
                continue;
            }

            if (command.Language is not (CompilerLanguage.CSharp or CompilerLanguage.VisualBasic))
            {
                diagnostics.Add($"{name}: compiler logs only support C# and Visual Basic; skipping.");
                continue;
            }

            CompilerCall compilerCall = new(
                analyzerResult.ProjectFilePath,
                CompilerCallKind.Regular,
                analyzerResult.TargetFramework,
                isCSharp: command.Language == CompilerLanguage.CSharp,
                compilerFilePath: command.CompilerLocation?.FullName);

            try
            {
                builder.AddFromDisk(compilerCall, command.Arguments);
                compilerCalls.Add(compilerCall);
            }
            catch (Exception ex)
            {
                diagnostics.Add($"Error adding {compilerCall.GetDiagnosticName()}: {ex.Message}");
                succeeded = false;
            }
        }

        if (compilerCalls.Count == 0)
        {
            diagnostics.Add("No compiler calls were written to the log.");
            succeeded = false;
        }

        builder.Close();
        return new CompilerLogCreateResult(succeeded, compilerCalls, diagnostics);
    }
}
