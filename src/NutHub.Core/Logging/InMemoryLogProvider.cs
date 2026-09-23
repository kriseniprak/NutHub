using Microsoft.Extensions.Logging;

namespace NutHub.Core.Logging;

/// <summary>A log line kept in memory for the "Logs" page of the web panel.</summary>
public sealed record LogEntry(long Id, DateTimeOffset Timestamp, LogLevel Level, string Category, string Message,
                              string? Exception);

/// <summary>The last application log lines, in memory.</summary>
public sealed class InMemoryLogSink
{
    private const int Capacity = 2000;
    private readonly LinkedList<LogEntry> _entries = new();
    private readonly object _lock = new();
    private long _nextId;

    public void Add(DateTimeOffset timestamp, LogLevel level, string category, string message, Exception? exception)
    {
        lock (_lock)
        {
            _entries.AddLast(new LogEntry(++_nextId, timestamp, level, category, message, exception?.ToString()));
            while (_entries.Count > Capacity)
            {
                _entries.RemoveFirst();
            }
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<LogEntry> Get(LogLevel minLevel = LogLevel.Information, int limit = 500, string? search = null)
    {
        lock (_lock)
        {
            var result = new List<LogEntry>(Math.Min(limit, _entries.Count));
            for (var node = _entries.Last; node is not null && result.Count < limit; node = node.Previous)
            {
                LogEntry e = node.Value;
                if (e.Level >= minLevel &&
                    (string.IsNullOrEmpty(search) || e.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                     e.Category.Contains(search, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(e);
                }
            }

            return result;
        }
    }
}

[ProviderAlias("Memory")]
public sealed class InMemoryLogProvider(InMemoryLogSink sink, TimeProvider time) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new MemoryLogger(categoryName, sink, time);

    public void Dispose()
    {
    }

    private sealed class MemoryLogger(string category, InMemoryLogSink sink, TimeProvider time) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            sink.Add(time.GetUtcNow(), logLevel, category, formatter(state, exception), exception);
        }
    }
}
