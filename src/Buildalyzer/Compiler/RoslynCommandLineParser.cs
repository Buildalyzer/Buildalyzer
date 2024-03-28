﻿#nullable enable

using Microsoft.CodeAnalysis;

namespace Buildalyzer;

internal static class RoslynCommandLineParser
{
    [Pure]
    public static string[]? SplitCommandLineIntoArguments(string? commandLine, params string[] execs)
        => Split(CommandLineParser.SplitCommandLineIntoArguments(commandLine ?? string.Empty, removeHashComments: true).ToArray(), execs);

    [Pure]
    private static string[]? Split(string[] args, string[] execs)
    {
        foreach (var exec in execs)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i].EndsWith(exec, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i..];
                }
            }
        }
        return null;
    }
}
