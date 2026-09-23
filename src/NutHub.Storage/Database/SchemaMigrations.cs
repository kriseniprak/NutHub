using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace NutHub.Storage.Database;

/// <summary>
/// Creates and upgrades the schema, tracked by <c>PRAGMA user_version</c>. Each step upgrades from the previous
/// version in its own transaction, so an interrupted upgrade resumes from the last completed step at the next start.
/// To change the schema, append a step (version 2, 3...) and never edit a step that has shipped.
/// </summary>
internal static class SchemaMigrations
{
    private static readonly Migration[] Steps =
    [
        new(1, "Events, history samples and roll-ups", V1),
    ];

    public static int CurrentVersion => Steps[^1].Version;

    /// <summary>Brings the database to <see cref="CurrentVersion"/> and returns the version found before.</summary>
    /// <exception cref="DatabaseTooNewException">The file comes from a newer NutHub.</exception>
    public static int Apply(SqliteConnection connection, ILogger logger)
    {
        int found = ReadVersion(connection);
        if (found > CurrentVersion)
        {
            throw new DatabaseTooNewException(found, CurrentVersion);
        }

        foreach (Migration step in Steps)
        {
            if (step.Version <= found)
            {
                continue;
            }

            using SqliteTransaction transaction = connection.BeginTransaction();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = step.Sql + $"\nPRAGMA user_version = {step.Version};";
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            logger.LogInformation("Database schema upgraded to version {Version}: {Description}.", step.Version,
                                  step.Description);
        }

        return found;
    }

    public static int ReadVersion(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private sealed record Migration(int Version, string Description, string Sql);

    // Events: severity is stored as its rank (Info=0 ... Critical=3) so "at least" is a numeric comparison; type and
    // category are stored by name so that reordering the enums in a later version cannot change old rows.
    // History: UPS and variable names live in lookup tables, samples and roll-ups refer to them by integer id in
    // WITHOUT ROWID tables clustered on (ups, variable, time): one series is one contiguous index range.
    // Names are never deleted: the history of a renamed or removed UPS stays under its old name.
    private const string V1 = """
        CREATE TABLE events (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            ts       INTEGER NOT NULL,
            ups      TEXT COLLATE NOCASE,
            type     TEXT NOT NULL,
            severity INTEGER NOT NULL,
            category TEXT NOT NULL,
            message  TEXT NOT NULL,
            actor    TEXT,
            data     TEXT
        );
        CREATE INDEX events_ts ON events (ts);
        CREATE INDEX events_ups ON events (ups, id);

        CREATE TABLE ups_names (
            id   INTEGER PRIMARY KEY,
            name TEXT NOT NULL COLLATE NOCASE UNIQUE
        );

        CREATE TABLE variable_names (
            id   INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE
        );

        CREATE TABLE samples (
            ups_id INTEGER NOT NULL REFERENCES ups_names (id),
            var_id INTEGER NOT NULL REFERENCES variable_names (id),
            ts     INTEGER NOT NULL,
            value  REAL NOT NULL,
            PRIMARY KEY (ups_id, var_id, ts)
        ) WITHOUT ROWID;

        CREATE TABLE rollups (
            ups_id INTEGER NOT NULL REFERENCES ups_names (id),
            var_id INTEGER NOT NULL REFERENCES variable_names (id),
            ts     INTEGER NOT NULL,
            avg    REAL NOT NULL,
            min    REAL NOT NULL,
            max    REAL NOT NULL,
            count  INTEGER NOT NULL,
            PRIMARY KEY (ups_id, var_id, ts)
        ) WITHOUT ROWID;

        CREATE TABLE storage_state (
            key   TEXT PRIMARY KEY,
            value INTEGER NOT NULL
        ) WITHOUT ROWID;
        """;
}
