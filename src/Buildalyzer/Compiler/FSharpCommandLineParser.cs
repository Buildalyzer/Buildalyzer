
using System;

namespace Buildalyzer;

internal static class FSharpCommandLineParser
{
    [Pure]
    public static string[]? SplitCommandLineIntoArguments(string? commandLine)
        => commandLine?.Split(Splitters, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } args
        && First(args[0]).ToArray() is { Length: >= 1 } first
            ? first.Concat(args[1..]).ToArray()
            : null;

    [Pure]
    private static IEnumerable<string> First(string arg)
        => Tokenize(arg)
        .SkipWhile(NotCompilerLocation)
        .Select(a => a.Trim())
        .Where(a => a.Length > 0);

    [Pure]
    private static IEnumerable<string> Tokenize(string arg)
    {
        var first = 0;
        var cursor = 0;
        var quote = false;

        foreach (var ch in arg)
        {
            if (ch == '"')
            {
                if (quote)
                {
                    quote = false;
                    yield return arg[first..cursor];
                }
                else
                {
                    quote = true;
                }
                first = cursor + 1;
            }
            else if (ch == ' ' && cursor >= first && !quote)
            {
                yield return arg[first..cursor];
                first = cursor + 1;
            }
            cursor++;
        }
        yield return arg[first..];
    }

    [Pure]
    public static bool NotCompilerLocation(string s)
        => !s.IsMatchEnd("fsc.dll")
        && !s.IsMatchEnd("fsc.exe");

    private static readonly char[] Splitters = ['\r', '\n'];
}
