using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NutHub.Core.Abstractions;
using NutHub.Core.Model;
using NutHub.Storage.Database;
using NutHub.Storage.Tests.Support;

namespace NutHub.Storage.Tests;

public sealed class DatabaseTests
{
    [Fact]
    public async Task NewDatabaseHasTheCurrentSchemaAndPragmas()
    {
        await using var fx = new StorageFixture();
        await fx.Database.InitializeAsync();

        Assert.True(File.Exists(fx.Paths.DatabaseFile));
        Assert.False(fx.Database.IsInMemory);
        (long version, string mode, long autoVacuum, long foreignKeys, long tables) = await fx.ReadAsync(c => (
            (long)StorageFixture.Scalar(c, "PRAGMA user_version;")!,
            (string)StorageFixture.Scalar(c, "PRAGMA journal_mode;")!,
            (long)StorageFixture.Scalar(c, "PRAGMA auto_vacuum;")!,
            (long)StorageFixture.Scalar(c, "PRAGMA foreign_keys;")!,
            (long)StorageFixture.Scalar(c, "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name IN " +
                                           "('events', 'ups_names', 'variable_names', 'samples', 'rollups', " +
                                           "'storage_state');")!));

        Assert.Equal(SchemaMigrations.CurrentVersion, version);
        Assert.Equal("wal", mode);
        Assert.Equal(2, autoVacuum); // INCREMENTAL
        Assert.Equal(1, foreignKeys);
        Assert.Equal(6, tables);
        Assert.True(fx.Logs.Has(LogLevel.Information, "Created the database"));
    }

    [Fact]
    public async Task ReopeningKeepsTheDataAndDoesNotMigrateAgain()
    {
        await using var fx = new StorageFixture();
        UpsEvent stored = await fx.Events.AppendAsync(
            StorageFixture.Event(StorageFixture.Start, "ups1", UpsEventType.OnBattery, "On battery"));
        await fx.ReopenAsync();
        int generation = fx.Database.Generation;

        EventPage page = await fx.Events.QueryAsync(new EventQuery());

        Assert.Equal(stored.Id, Assert.Single(page.Items).Id);
        Assert.Equal(generation + 1, fx.Database.Generation);
        // Both messages come from the first start only.
        Assert.Single(fx.Logs.Entries, e => e.Message.Contains("schema upgraded", StringComparison.Ordinal));
        Assert.Single(fx.Logs.Entries, e => e.Message.Contains("Created the database", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyDatabaseAtVersionZeroIsMigrated()
    {
        await using var fx = new StorageFixture();
        await using (var connection = new SqliteConnection($"Data Source={fx.Paths.DatabaseFile};Pooling=False"))
        {
            connection.Open();
            StorageFixture.Scalar(connection, "CREATE TABLE unrelated (x INTEGER); PRAGMA user_version = 0;");
        }

        await fx.Database.InitializeAsync();

        long version = await fx.ReadAsync(c => (long)StorageFixture.Scalar(c, "PRAGMA user_version;")!);
        Assert.Equal(SchemaMigrations.CurrentVersion, version);
        Assert.True(fx.Logs.Has(LogLevel.Information, "upgraded from schema version 0"));
    }

    [Fact]
    public async Task AFileThatIsNotADatabaseIsMovedAsideAndReplaced()
    {
        await using var fx = new StorageFixture();
        byte[] garbage = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("this is not a database ", 400)));
        await File.WriteAllBytesAsync(fx.Paths.DatabaseFile, garbage);

        UpsEvent stored = await fx.Events.AppendAsync(
            StorageFixture.Event(StorageFixture.Start, null, UpsEventType.ServerStarted, "Started"));

        Assert.True(stored.Id > 0);
        Assert.False(fx.Database.IsInMemory);
        string backup = Assert.Single(Directory.GetFiles(fx.Directory, "nuthub.db.corrupt-*"));
        Assert.Equal(garbage, await File.ReadAllBytesAsync(backup));
        Assert.True(fx.Logs.Has(LogLevel.Error, "is damaged"));
        Assert.Single((await fx.Events.QueryAsync(new EventQuery())).Items);
    }

    [Fact]
    public async Task ADatabaseFromANewerVersionIsMovedAside()
    {
        await using var fx = new StorageFixture();
        await using (var connection = new SqliteConnection($"Data Source={fx.Paths.DatabaseFile};Pooling=False"))
        {
            connection.Open();
            StorageFixture.Scalar(connection, "CREATE TABLE future (x INTEGER); PRAGMA user_version = 99;");
        }

        await fx.Database.InitializeAsync();

        Assert.Single(Directory.GetFiles(fx.Directory, "nuthub.db.newer-*"));
        Assert.True(fx.Logs.Has(LogLevel.Error, "newer version"));
        long version = await fx.ReadAsync(c => (long)StorageFixture.Scalar(c, "PRAGMA user_version;")!);
        Assert.Equal(SchemaMigrations.CurrentVersion, version);
    }

    [Fact]
    public async Task DamagedPagesFoundByTheIntegrityCheckAreReplacedAtTheNextWrite()
    {
        await using var fx = new StorageFixture();
        await fx.Database.WriteAsync((session, _) => session.InTransaction(() =>
        {
            SqliteCommand insert = session.Command(
                "INSERT INTO events (ts, ups, type, severity, category, message) " +
                "VALUES (@ts, 'ups1', 'OnBattery', 2, 'Power', @message);", "@ts", "@message");
            for (int i = 0; i < 3000; i++)
            {
                insert.Parameters["@ts"].Value = i;
                insert.Parameters["@message"].Value = new string((char)('a' + i % 26), 200);
                insert.ExecuteNonQuery();
            }

            return true;
        }));
        await fx.ReopenAsync();

        // Page 1 (the header and the schema) stays intact, so the file still opens; data pages are overwritten.
        await using (FileStream file = File.Open(fx.Paths.DatabaseFile, FileMode.Open, FileAccess.ReadWrite))
        {
            Assert.True(file.Length > 200_000);
            file.Position = 16 * 4096;
            file.Write(Enumerable.Repeat((byte)0xA5, 64 * 4096).ToArray());
        }

        bool healthy;
        try
        {
            healthy = await fx.Database.CheckIntegrityAsync();
        }
        catch (SqliteException)
        {
            healthy = false;
        }

        Assert.False(healthy);
        await fx.Events.AppendAsync(StorageFixture.Event(StorageFixture.Start, "ups1", UpsEventType.Online, "Back"));

        Assert.Single(Directory.GetFiles(fx.Directory, "nuthub.db.corrupt-*"));
        Assert.Single((await fx.Events.QueryAsync(new EventQuery())).Items);
        Assert.True(await fx.Database.CheckIntegrityAsync());
    }

    [Fact]
    public async Task AnUnusableLocationFallsBackToMemory()
    {
        string parent = StorageFixture.CreateTempDirectory();
        string notADirectory = Path.Combine(parent, "data");
        await File.WriteAllTextAsync(notADirectory, "a file where the data directory should be");
        await using var fx = new StorageFixture(notADirectory);

        UpsEvent stored = await fx.Events.AppendAsync(
            StorageFixture.Event(StorageFixture.Start, "ups1", UpsEventType.OnBattery, "On battery"));

        Assert.True(fx.Database.IsInMemory);
        Assert.True(fx.Logs.Has(LogLevel.Error, "kept in memory"));
        Assert.Equal(stored.Id, Assert.Single((await fx.Events.QueryAsync(new EventQuery())).Items).Id);
        Directory.Delete(parent, recursive: true);
    }

    [Fact]
    public async Task WritesAreSerialisedAndRunOffTheCallersThread()
    {
        await using var fx = new StorageFixture();
        await fx.Database.InitializeAsync();
        int active = 0, maximum = 0, inline = 0;

        s_insideCaller = true;
        Task<bool>[] writes = Enumerable.Range(0, 20).Select(_ => fx.Database.WriteAsync((_, _) =>
        {
            if (s_insideCaller)
            {
                Interlocked.Increment(ref inline);
            }

            InterlockedMax(ref maximum, Interlocked.Increment(ref active));
            Thread.Sleep(2);
            Interlocked.Decrement(ref active);
            return true;
        })).ToArray();
        s_insideCaller = false;
        await Task.WhenAll(writes);

        Assert.Equal(1, maximum);
        Assert.Equal(0, inline);
    }

    [ThreadStatic]
    private static bool s_insideCaller;

    [Fact]
    public async Task ACancelledReadThrowsOperationCanceled()
    {
        await using var fx = new StorageFixture();
        await fx.Database.InitializeAsync();
        using var cts = new CancellationTokenSource();

        // A statement that would run for a long time: the cancellation interrupts it inside SQLite.
        Task<long> read = fx.Database.ReadAsync((connection, ct) =>
        {
            cts.CancelAfter(50);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 2000000000) " +
                "SELECT count(*) FROM n;";
            return (long)command.ExecuteScalar()!;
        }, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
