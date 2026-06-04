using System.IO;
using Microsoft.Extensions.Logging;

namespace Buildalyzer.Logging;

/// <summary>Implements <see cref="ILogger"/> using a <see cref="TextWriter"/>.</summary>
internal sealed class TextWriterLogger(TextWriter textWriter) : ILogger
{
    private readonly TextWriter _textWriter = textWriter;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
        _textWriter.Write(formatter(state, exception));

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public IDisposable BeginScope<TState>(TState state) => new Scope();

    private sealed class Scope : IDisposable
    {
        public void Dispose()
        {
            // Nothing to dispose.
        }
    }
}
