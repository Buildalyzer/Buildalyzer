using System.IO;
using Microsoft.Extensions.Logging;

namespace Buildalyzer.Logging;

public class TextWriterLoggerProvider(TextWriter textWriter) : ILoggerProvider
{
    // Buildalyzer logs from several threads at once (most notably the process stdout and
    // stderr readers), and every logger created here shares this single writer. Wrap it once
    // in a synchronized writer so callers can safely pass a non-thread-safe TextWriter such as
    // a StringWriter without risking corrupted output or an ArgumentOutOfRangeException.
    private readonly TextWriter _textWriter = TextWriter.Synchronized(Guard.NotNull(textWriter));

    public void Dispose()
    {
    }

    public ILogger CreateLogger(string categoryName) => new TextWriterLogger(_textWriter);
}
