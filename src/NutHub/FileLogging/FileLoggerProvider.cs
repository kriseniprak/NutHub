using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace NutHub.FileLogging;

/// <summary>
/// Writes the log to rolling files (<see cref="RollingLogFile"/>) from a background task, so logging never waits for
/// the disk: when the queue is full, lines are dropped and counted instead. The level follows the standard
/// "Logging" configuration; the alias "File" allows a level for this provider only ("Logging:File:LogLevel").
/// </summary>
[ProviderAlias("File")]
internal sealed class FileLoggerProvider : ILoggerProvider
{
    public const long DefaultMaxFileBytes = 10L * 1024 * 1024;
    public const int DefaultRetainDays = 14;

    private readonly Channel<LogLine> _queue;
    private readonly RollingLogFile _file;
    private readonly TimeProvider _time;
    private readonly Task _writer;
    private long _dropped;
    private int _disposed;

    public FileLoggerProvider(string directory, TimeProvider time, long maxFileBytes = DefaultMaxFileBytes,
                              int retainDays = DefaultRetainDays, int queueCapacity = 10_000)
    {
        Directory = directory;
        _time = time;
        _file = new RollingLogFile(directory, maxFileBytes, retainDays, time);
        _queue = Channel.CreateBounded<LogLine>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        }, _ => Interlocked.Increment(ref _dropped));
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Where the files are written.</summary>
    public string Directory { get; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    /// <summary>Stops accepting lines and waits (briefly) for the queue to reach the disk.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The writer loop handles its own I/O errors; nothing else is expected here.
        }

        _file.Dispose();
    }

    internal void Enqueue(LogLevel level, string category, string message, Exception? exception)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _queue.Writer.TryWrite(new LogLine(_time.GetLocalNow(), level, category, message, exception?.ToString()));
    }

    private async Task WriteLoopAsync()
    {
        ChannelReader<LogLine> reader = _queue.Reader;
        var builder = new StringBuilder(512);
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            long dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0)
            {
                var now = _time.GetLocalNow();
                _file.Write(now, Format(builder, new LogLine(now, LogLevel.Warning, "NutHub.FileLogging",
                    $"{dropped} log messages were dropped because the log file could not keep up.", null)));
            }

            while (reader.TryRead(out LogLine line))
            {
                if (!_file.Write(line.Timestamp, Format(builder, line)))
                {
                    Interlocked.Increment(ref _dropped);
                }
            }

            _file.Flush();
        }
    }

    internal static string Format(StringBuilder builder, in LogLine line)
    {
        builder.Clear();
        builder.Append(line.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
        builder.Append(" [").Append(LevelText(line.Level)).Append("] ");
        builder.Append(line.Category).Append(": ");
        AppendIndented(builder, line.Message);
        builder.Append('\n');
        if (line.Exception is not null)
        {
            builder.Append("    ");
            AppendIndented(builder, line.Exception);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Keeps one entry per line start: continuation lines are indented, so tools can split entries.</summary>
    private static void AppendIndented(StringBuilder builder, string text)
    {
        foreach (char c in text)
        {
            if (c == '\r')
            {
                continue;
            }

            builder.Append(c);
            if (c == '\n')
            {
                builder.Append("    ");
            }
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    internal readonly record struct LogLine(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message,
                                            string? Exception);

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // The logger factory applies the configured levels before calling Log.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null)
            {
                return;
            }

            provider.Enqueue(logLevel, category, message, exception);
        }
    }
}
