using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace NutHub.Protocol.Tests.Infrastructure;

/// <summary>Keeps every log line (all levels) for assertions, and echoes them to the test output.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper? _output;

    public CapturingLoggerProvider(ITestOutputHelper? output)
    {
        _output = output;
    }

    public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(LogLevel level, string category, string message)
    {
        Entries.Enqueue((level, category, message));
        try
        {
            _output?.WriteLine($"{level,-11} {category}: {message}");
        }
        catch (InvalidOperationException)
        {
            // The test already finished; its output is closed.
        }
    }

    private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            if (exception is not null)
            {
                message += " | " + exception.GetType().Name + ": " + exception.Message;
            }

            provider.Write(logLevel, category, message);
        }
    }
}
