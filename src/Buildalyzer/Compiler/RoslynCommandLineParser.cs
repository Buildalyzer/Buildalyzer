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
    /// command-line parser. Surrounding quotes are removed; backslashes are preserved except where they
    /// escape a quote.
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
                        quoteCount++;
                        i++;
                        break;

                    default:
                        builder.Append(current);
                        i++;
                        break;
                }
            }

            yield return builder.ToString();
        }
    }
}
