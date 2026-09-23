using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NutHub.Core;
using NutHub.Core.Security;

namespace NutHub.Storage.Database;

/// <summary>
/// Owns <c>nuthub.db</c>: opens it with the right pragmas, creates and migrates the schema, serialises every write on
/// one long-lived connection and runs reads on pooled connections of their own (WAL lets them proceed while a write
/// is in progress). All the work runs on the thread pool, never on the caller's thread, because Microsoft.Data.Sqlite
/// is synchronous underneath its async methods.
/// <para>
/// The database must never stop NutHub from protecting its machines: a damaged file (or one from a newer version) is
/// moved aside as <c>nuthub.db.corrupt-&lt;timestamp&gt;</c> and replaced by an empty one, and if no file can be used
/// at all the data is kept in memory until the next start. The file is opened on first use, not at construction.
/// </para>
/// </summary>
internal sealed class SqliteDatabase : IDisposable, IAsyncDisposable
{
    private const int BusyTimeoutMilliseconds = 5000;
    private const int CommandTimeoutSeconds = 30;

    // Keeps the -wal file from staying huge after a burst (a long retention run); SQLite truncates it to this size.
    private const long JournalSizeLimitBytes = 32L * 1024 * 1024;

    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);

    private readonly NutHubPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<SqliteDatabase> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _fileWriteConnectionString;
    private readonly string _fileReadConnectionString;
    private WriteSession? _session;
    private volatile string? _readConnectionString;
    private volatile bool _corrupt;
    private volatile bool _disposed;
    private bool _useMemory;
    private int _generation;

    public SqliteDatabase(NutHubPaths paths, TimeProvider time, ILogger<SqliteDatabase> logger)
    {
        _paths = paths;
        _time = time;
        _logger = logger;

        // The writer is not pooled: it lives as long as this object, and closing it must really release the file
        // (to move a damaged one aside, and on shutdown so the WAL is checkpointed).
        _fileWriteConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = CommandTimeoutSeconds,
        }.ToString();

        // Readers never create the file: if it has just been moved aside they must fail rather than create an empty
        // database behind the writer's back.
        _fileReadConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = CommandTimeoutSeconds,
        }.ToString();
    }

    public string FilePath => _paths.DatabaseFile;

    /// <summary>True when no database file could be used and the data only lives until NutHub stops.</summary>
    public bool IsInMemory => Volatile.Read(ref _session)?.InMemory ?? _useMemory;

    /// <summary>Incremented every time a database is opened (a replaced damaged file counts as a new one).</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Opens (and if needed creates, migrates or replaces) the database now rather than at first use.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        WriteAsync(static (_, _) => true, cancellationToken);

    /// <summary>
    /// Runs <paramref name="work"/> with exclusive use of the writer connection, on the thread pool. Writers queue in
    /// order; the work should check <paramref name="cancellationToken"/> between steps of long jobs.
    /// </summary>
    public async Task<T> WriteAsync<T>(Func<WriteSession, CancellationToken, T> work,
                                       CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => RunWrite(work, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a pooled read connection, on the thread pool, concurrently with other reads
    /// and with the writer. Cancellation interrupts the running SQL statement.
    /// </summary>
    public async Task<T> ReadAsync<T>(Func<SqliteConnection, CancellationToken, T> work,
                                      CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? connectionString = _readConnectionString;
        if (connectionString is null || _corrupt)
        {
            // Not opened yet, or damage was detected: opening and repairing happen on the writer's side.
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            connectionString = _readConnectionString
                               ?? throw new InvalidOperationException("The database is not available.");
        }

        return await Task.Run(() => RunRead(connectionString, work, cancellationToken), cancellationToken)
                         .ConfigureAwait(false);
    }

    /// <summary>
    /// Records that the file is damaged. The next write moves it aside and starts a new database; until then reads
    /// and writes may keep failing.
    /// </summary>
    public void ReportCorruption(string detail)
    {
        if (!_corrupt)
        {
            _corrupt = true;
            _logger.LogError("The database {Path} is damaged ({Detail}). It will be moved aside and replaced by a new one.",
                             FilePath, detail);
        }
    }

    /// <summary>
    /// Daily housekeeping: refreshes the query planner statistics, returns free pages to the file system and
    /// truncates the write-ahead log.
    /// </summary>
    public Task OptimizeAsync(CancellationToken cancellationToken = default) =>
        WriteAsync(static (session, _) =>
        {
            Execute(session.Connection, "PRAGMA optimize;");
            if (!session.InMemory)
            {
                Execute(session.Connection, "PRAGMA incremental_vacuum;");
                Execute(session.Connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            }

            return true;
        }, cancellationToken);

    /// <summary>
    /// Runs SQLite's quick integrity check (reads the whole file, so it is kept for daily maintenance) and reports
    /// corruption, so a damaged file is replaced even when no ordinary query happened to touch the bad pages.
    /// </summary>
    public Task<bool> CheckIntegrityAsync(CancellationToken cancellationToken = default) =>
        ReadAsync((connection, ct) =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check(5);";
            var problems = new List<string>();
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    problems.Add(reader.GetString(0));
                }
            }

            if (problems is ["ok"])
            {
                return true;
            }

            ReportCorruption(string.Join("; ", problems));
            return false;
        }, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        bool acquired = _writeLock.Wait(DisposeTimeout);
        try
        {
            CloseCore();
        }
        finally
        {
            if (acquired)
            {
                _writeLock.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        bool acquired = await _writeLock.WaitAsync(DisposeTimeout).ConfigureAwait(false);
        try
        {
            CloseCore();
        }
        finally
        {
            if (acquired)
            {
                _writeLock.Release();
            }
        }
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private void CloseCore()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseSession();
    }

    private T RunWrite<T>(Func<WriteSession, CancellationToken, T> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteSession session = EnsureOpen();
        try
        {
            return work(session, cancellationToken);
        }
        catch (Exception ex) when (SqliteErrors.IsCorruption(ex))
        {
            ReportCorruption(ex.Message);
            throw;
        }
    }

    private T RunRead<T>(string connectionString, Func<SqliteConnection, CancellationToken, T> work,
                         CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            Execute(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};");

            // Declared after the connection so it is disposed first: the interrupt can never hit the connection once
            // it is back in the pool serving someone else.
            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state => SQLitePCL.raw.sqlite3_interrupt(((SqliteConnection)state!).Handle), connection);
            return work(connection, cancellationToken);
        }
        catch (Exception ex) when (SqliteErrors.IsInterrupt(ex) && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The query was cancelled.", ex, cancellationToken);
        }
        catch (Exception ex) when (SqliteErrors.IsCorruption(ex))
        {
            ReportCorruption(ex.Message);
            throw;
        }
    }

    /// <summary>Returns the writer session, opening or replacing the database first when needed. Holds the lock.</summary>
    private WriteSession EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null && !_corrupt)
        {
            return _session;
        }

        if (_session is not null)
        {
            bool wasInMemory = _session.InMemory;
            CloseSession();
            if (!wasInMemory)
            {
                if (TryMoveAside("corrupt", out string? backup))
                {
                    _logger.LogError("The damaged database {Path} was moved to {Backup} (the events and history " +
                                     "recorded so far are in that file); a new database is created.", FilePath,
                                     backup);
                }
                else
                {
                    _useMemory = true;
                }
            }
        }

        _corrupt = false;
        WriteSession session = Open();
        Volatile.Write(ref _session, session);
        Interlocked.Increment(ref _generation);
        return session;
    }

    private WriteSession Open()
    {
        if (!_useMemory)
        {
            try
            {
                return OpenFile();
            }
            catch (Exception ex) when (SqliteErrors.IsCorruption(ex) || ex is DatabaseTooNewException)
            {
                string kind = ex is DatabaseTooNewException ? "newer" : "corrupt";
                ClearPools();
                if (TryMoveAside(kind, out string? backup))
                {
                    if (ex is DatabaseTooNewException tooNew)
                    {
                        _logger.LogError(
                            "The database {Path} was created by a newer version of NutHub (schema version {Version}). " +
                            "It was moved to {Backup} and a new database was created.", FilePath, tooNew.Version,
                            backup);
                    }
                    else
                    {
                        _logger.LogError(ex,
                            "The database {Path} is damaged. It was moved to {Backup} (the events and history " +
                            "recorded so far are in that file) and a new database was created.", FilePath, backup);
                    }

                    try
                    {
                        return OpenFile();
                    }
                    catch (Exception retry)
                    {
                        _logger.LogError(retry, "Could not create a new database {Path}.", FilePath);
                    }
                }
                else
                {
                    _logger.LogError(ex, "The database {Path} cannot be used and could not be moved aside.", FilePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not open the database {Path}.", FilePath);
            }

            _useMemory = true;
            _logger.LogError("Events and history are kept in memory only, until NutHub is restarted with a usable " +
                             "database file ({Path}).", FilePath);
        }

        return OpenMemory();
    }

    private WriteSession OpenFile()
    {
        EnsureDirectory();
        bool created = !File.Exists(FilePath);
        var connection = new SqliteConnection(_fileWriteConnectionString);
        try
        {
            connection.Open();
            Execute(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};");

            // Only effective while the file has no tables, hence before anything else; free pages are then returned
            // to the file system by OptimizeAsync instead of a full VACUUM rewriting the whole file.
            Execute(connection, "PRAGMA auto_vacuum = INCREMENTAL;");
            string? mode = Scalar(connection, "PRAGMA journal_mode = WAL;") as string;
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("The database {Path} could not use write-ahead logging (journal mode {Mode}); " +
                                   "reads may wait for writes.", FilePath, mode);
            }

            Execute(connection, "PRAGMA synchronous = NORMAL;");
            Execute(connection, "PRAGMA foreign_keys = ON;");
            Execute(connection, $"PRAGMA journal_size_limit = {JournalSizeLimitBytes};");

            int previous = SchemaMigrations.Apply(connection, _logger);

            // Parses the whole schema now, so a damaged file is detected at startup rather than by the first query.
            Scalar(connection, "SELECT count(*) FROM sqlite_schema;");

            if (created)
            {
                // Events hold user names and addresses: keep the file to the service account like the rest of the data.
                FilePermissions.TryRestrictFile(FilePath);
                _logger.LogInformation("Created the database {Path}.", FilePath);
            }
            else if (previous < SchemaMigrations.CurrentVersion)
            {
                _logger.LogInformation("Database {Path} upgraded from schema version {From} to {To}.", FilePath,
                                       previous, SchemaMigrations.CurrentVersion);
            }

            _readConnectionString = _fileReadConnectionString;
            return new WriteSession(connection, inMemory: false);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private WriteSession OpenMemory()
    {
        // A named shared-cache memory database lives as long as one connection is open on it: the writer keeps it
        // alive and the readers share it.
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = "nuthub-" + Guid.NewGuid().ToString("N"),
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = CommandTimeoutSeconds,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            SchemaMigrations.Apply(connection, _logger);
            _readConnectionString = connectionString;
            return new WriteSession(connection, inMemory: true);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void EnsureDirectory()
    {
        try
        {
            _paths.EnsureCreated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The data directory alone is enough here; the others are someone else's concern.
            Directory.CreateDirectory(_paths.DataDirectory);
        }
    }

    private void CloseSession()
    {
        WriteSession? session = Interlocked.Exchange(ref _session, null);
        string? readConnectionString = _readConnectionString;
        _readConnectionString = null;
        session?.Dispose();
        ClearPools(readConnectionString);
    }

    private void ClearPools(string? readConnectionString = null)
    {
        // Idle pooled readers keep the file open, which would prevent moving it aside on Windows.
        foreach (string? connectionString in new[] { _fileReadConnectionString, readConnectionString })
        {
            if (connectionString is not null)
            {
                using var connection = new SqliteConnection(connectionString);
                SqliteConnection.ClearPool(connection);
            }
        }
    }

    /// <summary>
    /// Renames the database and its -wal / -shm companions to <c>nuthub.db.&lt;kind&gt;-&lt;timestamp&gt;</c>. The
    /// write-ahead log must go too: SQLite would otherwise replay the old file's pages into the new one.
    /// </summary>
    private bool TryMoveAside(string kind, out string? backup)
    {
        string stamp = _time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        backup = $"{FilePath}.{kind}-{stamp}";
        for (int n = 2; File.Exists(backup); n++)
        {
            backup = $"{FilePath}.{kind}-{stamp}-{n}";
        }

        // A reader that was running when the damage was found may still hold the file for a moment.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Move(FilePath, backup);
                }

                foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
                {
                    if (File.Exists(FilePath + suffix))
                    {
                        File.Move(FilePath + suffix, backup + suffix);
                    }
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 5)
                {
                    _logger.LogError(ex, "Could not move the database {Path} aside.", FilePath);
                    return false;
                }

                Thread.Sleep(100 * attempt);
            }
        }
    }
}
