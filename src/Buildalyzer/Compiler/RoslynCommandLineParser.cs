namespace Buildalyzer;

/// <summary>
/// Splits a C#/VB compiler command line into arguments. The tokenizer is vendored from Roslyn's
/// <c>CommandLineParser.SplitCommandLineIntoArguments</c> (MIT) so Buildalyzer no longer needs a
/// Microsoft.CodeAnalysis dependency just to tokenize the command line.
/// </summary>
internal static class RoslynCommandLineParser
{
    [Pure]
    public static string[]? SplitCommandLineIntoArguments(string? commandLine, params string[] execs)
        => Split([.. SplitCommandLineIntoArguments(commandLine ?? string.Empty, removeHashComments: true)], execs);

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

    /// <summary>
    /// Splits a command line into individual arguments using the same quoting/backslash rules as Roslyn's
    /// command-line parser (a copy of its tokenizer, differing only in that an illegal character - a control
    /// character or '|' - is dropped without being reported). An argument enclosed in a single pair of
    /// quotes has them removed, other quotes are kept, empty arguments are dropped, and backslashes are
    /// preserved except where they escape a quote.
    /// </summary>
    [Pure]
    internal static IEnumerable<string> SplitCommandLineIntoArguments(string commandLine, bool removeHashComments)
    {
        var builder = new StringBuilder(commandLine.Length);
        var i = 0;

        while (i < commandLine.Length)
        {
            while (i < commandLine.Length && char.IsWhiteSpace(commandLine[i]))
            {
                i++;
            }

            if (i == commandLine.Length)
            {
                break;
            }

            if (commandLine[i] == '#' && removeHashComments)
            {
                break;
            }

            var quoteCount = 0;
            builder.Clear();
            while (i < commandLine.Length && (!char.IsWhiteSpace(commandLine[i]) || (quoteCount % 2 != 0)))
            {
                var current = commandLine[i];
                switch (current)
                {
                    case '\\':
                        var slashCount = 0;
                        do
                        {
                            builder.Append(commandLine[i]);
                            i++;
                            slashCount++;
                        }
                        while (i < commandLine.Length && commandLine[i] == '\\');

                        // Backslashes not followed by a quote are literal.
                        if (i >= commandLine.Length || commandLine[i] != '"')
                        {
                            break;
                        }

                        // An even number of backslashes leaves the quote acting as a delimiter; an odd number
                        // escapes it (a literal quote).
                        if (slashCount % 2 == 0)
                        {
                            quoteCount++;
                        }

                        builder.Append('"');
                        i++;
                        break;

                    case '"':
                        builder.Append(current);
                        quoteCount++;
                        i++;
                        break;

                    default:
                        // Control characters and '|' are illegal in an argument, so they are dropped as
                        // Roslyn drops them. Roslyn also reports the first one for the compiler to turn
                        // into an error, which has no equivalent here.
                        if (current is (>= '\u0001' and <= '\u001f') or '|')
                        {
                            i++;
                            break;
                        }

                        builder.Append(current);
                        i++;
                        break;
                }
            }

            // Only an argument enclosed in a single pair of quotes has them removed; quotes inside an
            // argument (/define:CONFIG="Debug") are kept for the compiler's own parser to interpret, as
            // Roslyn does.
            if (quoteCount == 2 && builder[0] == '"' && builder[^1] == '"')
            {
                builder.Remove(0, 1);
                builder.Remove(builder.Length - 1, 1);
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
            }
        }
    }
}
