using System.IO;
using Microsoft.Extensions.Logging;

namespace Buildalyzer.Logging;

public class TextWriterLoggerProvider(TextWriter textWriter) : ILoggerProvider
{
    private readonly TextWriter _textWriter = Guard.NotNull(textWriter);

    public void Dispose() { }

    public ILogger CreateLogger(string categoryName) => new TextWriterLogger(_textWriter);
}