using Microsoft.Extensions.Logging;

namespace Buildalyzer.Logging;

/// <summary>
/// A tiny <see cref="ILoggerFactory"/> so Buildalyzer depends only on Microsoft.Extensions.Logging.Abstractions
/// (which has the interfaces but no concrete <c>LoggerFactory</c>). It fans a category out to the loggers of
/// each registered provider - all Buildalyzer needs for its <c>LogWriter</c> convenience.
/// </summary>
internal sealed class SimpleLoggerFactory : ILoggerFactory
{
    private readonly List<ILoggerProvider> _providers = [];

    public void AddProvider(ILoggerProvider provider) => _providers.Add(Guard.NotNull(provider));

    public ILogger CreateLogger(string categoryName)
        => new CompositeLogger(_providers.ConvertAll(p => p.CreateLogger(categoryName)));

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
    }

    private sealed class CompositeLogger(IReadOnlyList<ILogger> loggers) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => loggers.Any(logger => logger.IsEnabled(logLevel));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            foreach (var logger in loggers)
            {
                logger.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
            // Nothing to dispose.
        }
    }
}
