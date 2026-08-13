using Microsoft.Extensions.Logging;

namespace Nanto.Core.Tests;

internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    public List<RecordedLogEntry> Entries { get; } = [];

    public void AddProvider(ILoggerProvider provider) => ArgumentNullException.ThrowIfNull(provider);

    public ILogger CreateLogger(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        return new RecordingLogger(categoryName, Entries);
    }

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(string category, List<RecordedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            entries.Add(new RecordedLogEntry(category, logLevel, eventId, formatter(state, exception), exception, [.. properties]));
        }
    }
}

internal sealed record RecordedLogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> Properties);
