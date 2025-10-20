using Microsoft.CodeAnalysis;

namespace Buildalyzer;

internal static class RoslynCommandLineParser
{
    [Pure]
    public static string[]? SplitCommandLineIntoArguments(string? commandLine, params string[] execs)
        => Split([.. CommandLineParser.SplitCommandLineIntoArguments(commandLine ?? string.Empty, removeHashComments: true)], execs);

    [Pure]
    private static string[]? Split(string[] args, string[] execs)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            foreach (var exec in execs)
            {
                if (args[i].IsMatchEnd(exec))
                {
                    return args[i..];
                }
            }
        }
        return null;
    }
}
