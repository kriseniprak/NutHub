using Microsoft.Data.Sqlite;

namespace NutHub.Storage.Database;

/// <summary>
/// The single connection that writes to the database, with its prepared commands. Only one caller holds it at a time
/// (see <see cref="SqliteDatabase.WriteAsync{T}"/>), so it needs no locking of its own. It lives as long as the file it
/// is open on: when a damaged database is replaced, a new session starts with empty caches, which is why per-file
/// state (name ids, the roll-up watermark) is kept here through <see cref="GetState{T}"/>.
/// </summary>
internal sealed class WriteSession : IDisposable
{
    private readonly Dictionary<string, SqliteCommand> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, object> _state = [];
    private SqliteTransaction? _transaction;

    public WriteSession(SqliteConnection connection, bool inMemory)
    {
        Connection = connection;
        InMemory = inMemory;
    }

    public SqliteConnection Connection { get; }

    /// <summary>True for the in-memory fallback used when no database file could be opened.</summary>
    public bool InMemory { get; }

    /// <summary>
    /// A command prepared once and reused: SQLite keeps the compiled statement as long as the text does not change.
    /// Parameters are created on first use with the given names; callers set their values each time.
    /// </summary>
    public SqliteCommand Command(string sql, params ReadOnlySpan<string> parameters)
    {
        if (!_commands.TryGetValue(sql, out SqliteCommand? command))
        {
            command = Connection.CreateCommand();
            command.CommandText = sql;
            foreach (string name in parameters)
            {
                command.Parameters.Add(new SqliteParameter { ParameterName = name });
            }

            _commands[sql] = command;
        }

        // Microsoft.Data.Sqlite refuses to run a command outside the connection's pending transaction.
        command.Transaction = _transaction;
        return command;
    }

    /// <summary>Runs <paramref name="work"/> in one IMMEDIATE transaction, committed only if it returns.</summary>
    public T InTransaction<T>(Func<T> work)
    {
        if (_transaction is not null)
        {
            return work();
        }

        using SqliteTransaction transaction = Connection.BeginTransaction(deferred: false);
        _transaction = transaction;
        try
        {
            T result = work();
            transaction.Commit();
            return result;
        }
        finally
        {
            _transaction = null;
        }
    }

    /// <summary>State tied to this database file, created on first use.</summary>
    public T GetState<T>(Func<T> create)
        where T : class
    {
        if (!_state.TryGetValue(typeof(T), out object? value))
        {
            value = create();
            _state[typeof(T)] = value;
        }

        return (T)value;
    }

    public void Dispose()
    {
        foreach (SqliteCommand command in _commands.Values)
        {
            command.Dispose();
        }

        _commands.Clear();
        _state.Clear();
        Connection.Dispose();
    }
}
