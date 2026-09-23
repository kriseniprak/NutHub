using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NutHub.Core;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Storage.Database;
using NutHub.Storage.Events;
using NutHub.Storage.History;

namespace NutHub.Storage.Tests.Support;

/// <summary>A private data directory with a database, the stores on top of it, a fake clock and captured logs.</summary>
internal sealed class StorageFixture : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private SqliteDatabase? _database;

    public StorageFixture(string? directory = null)
    {
        Directory = directory ?? CreateTempDirectory();
        Paths = new NutHubPaths(Directory);
        Time = new FakeTimeProvider(Start);
        Config = new TestConfigStore();
        Logs = new TestLoggerFactory();
    }

    public string Directory { get; }

    public NutHubPaths Paths { get; }

    public FakeTimeProvider Time { get; }

    public TestConfigStore Config { get; }

    public TestLoggerFactory Logs { get; }

    public SqliteDatabase Database => _database ??= new SqliteDatabase(Paths, Time, Logs.CreateLogger<SqliteDatabase>());

    public SqliteEventStore Events => new(Database, Logs.CreateLogger<SqliteEventStore>());

    public SqliteHistoryStore History => new(Database, Config, Time);

    public HistoryMaintenance Maintenance => new(Database, Config, Time);

    public static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "nuthub-storage-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Closes the database (so the next access opens the file again, as after a restart).</summary>
    public async Task ReopenAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null;
        }
    }

    public static UpsEvent Event(DateTimeOffset at, string? ups, UpsEventType type, string message,
                                 EventSeverity? severity = null) =>
        UpsEvent.Create(type, at, ups, message, severity: severity);

    /// <summary>Runs SQL on a separate connection, for checks the stores do not expose.</summary>
    public Task<T> ReadAsync<T>(Func<SqliteConnection, T> read) =>
        Database.ReadAsync((connection, _) => read(connection));

    public static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public async ValueTask DisposeAsync()
    {
        await ReopenAsync();
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                await Task.Delay(50);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(50);
            }
        }
    }
}

/// <summary>An <see cref="IConfigStore"/> whose configuration the test sets directly.</summary>
internal sealed class TestConfigStore : IConfigStore
{
    private NutHubConfig _current = new();

    public NutHubConfig Current => Volatile.Read(ref _current);

    public event EventHandler<ConfigChangedEventArgs>? Changed;

    public void Update(Action<NutHubConfig> mutate)
    {
        NutHubConfig previous = Current;
        NutHubConfig next = NutHubJson.Clone(previous);
        mutate(next);
        Volatile.Write(ref _current, next);
        Changed?.Invoke(this, new ConfigChangedEventArgs(previous, next, CommandOrigin.System, "test"));
    }

    public Task<NutHubConfig> UpdateAsync(Action<NutHubConfig> mutate, CommandOrigin origin, string description,
                                          CancellationToken cancellationToken = default)
    {
        Update(mutate);
        return Task.FromResult(Current);
    }
}

internal sealed record LogEntry(LogLevel Level, string Category, string Message, Exception? Exception);

internal sealed class TestLoggerFactory
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public ILogger<T> CreateLogger<T>() => new TestLogger<T>(Entries);

    public bool Has(LogLevel level, string text) =>
        Entries.Any(e => e.Level == level && e.Message.Contains(text, StringComparison.OrdinalIgnoreCase));

    private sealed class TestLogger<T>(ConcurrentQueue<LogEntry> entries) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(logLevel, typeof(T).Name, formatter(state, exception), exception));
    }
}
