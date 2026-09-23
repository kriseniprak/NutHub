using Microsoft.Data.Sqlite;
using NutHub.Storage.Database;

namespace NutHub.Storage.History;

/// <summary>
/// What the history writer caches about the open database file: the ids of UPS and variable names and the roll-up
/// watermark. Lives in the <see cref="WriteSession"/>, so only one writer uses it at a time and a replaced database
/// starts with a fresh one. After a failed transaction the caller calls <see cref="Reset"/>, because ids and the
/// watermark cached during the transaction were rolled back with it.
/// </summary>
internal sealed class HistoryWriterState
{
    private const string SelectUpsSql = "SELECT id FROM ups_names WHERE name = @name;";
    private const string InsertUpsSql = "INSERT INTO ups_names (name) VALUES (@name) RETURNING id;";
    private const string SelectVariableSql = "SELECT id FROM variable_names WHERE name = @name;";
    private const string InsertVariableSql = "INSERT INTO variable_names (name) VALUES (@name) RETURNING id;";
    private const string SelectWatermarkSql = "SELECT value FROM storage_state WHERE key = @key;";

    private const string UpsertWatermarkSql =
        "INSERT INTO storage_state (key, value) VALUES (@key, @value) " +
        "ON CONFLICT (key) DO UPDATE SET value = excluded.value;";

    // UPS names are case-insensitive everywhere in NutHub (and COLLATE NOCASE in the table); variable names are not.
    private readonly Dictionary<string, long> _upsIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _variableIds = new(StringComparer.Ordinal);
    private bool _watermarkLoaded;
    private long? _watermark;

    public long UpsId(WriteSession session, string name) =>
        GetOrCreateId(session, _upsIds, name, SelectUpsSql, InsertUpsSql);

    public long VariableId(WriteSession session, string name) =>
        GetOrCreateId(session, _variableIds, name, SelectVariableSql, InsertVariableSql);

    /// <summary>Every sample before this time is in the roll-ups; null until the first roll-up.</summary>
    public long? GetWatermark(WriteSession session)
    {
        if (!_watermarkLoaded)
        {
            SqliteCommand command = session.Command(SelectWatermarkSql, "@key");
            command.Parameters["@key"].Value = HistoryLayout.WatermarkKey;
            object? value = command.ExecuteScalar();
            _watermark = value is null or DBNull ? null : Convert.ToInt64(value);
            _watermarkLoaded = true;
        }

        return _watermark;
    }

    public void SetWatermark(WriteSession session, long value)
    {
        SqliteCommand command = session.Command(UpsertWatermarkSql, "@key", "@value");
        command.Parameters["@key"].Value = HistoryLayout.WatermarkKey;
        command.Parameters["@value"].Value = value;
        command.ExecuteNonQuery();
        _watermark = value;
        _watermarkLoaded = true;
    }

    public void Reset()
    {
        _upsIds.Clear();
        _variableIds.Clear();
        _watermarkLoaded = false;
        _watermark = null;
    }

    private static long GetOrCreateId(WriteSession session, Dictionary<string, long> cache, string name,
                                      string selectSql, string insertSql)
    {
        if (cache.TryGetValue(name, out long id))
        {
            return id;
        }

        SqliteCommand select = session.Command(selectSql, "@name");
        select.Parameters["@name"].Value = name;
        object? found = select.ExecuteScalar();
        if (found is null or DBNull)
        {
            SqliteCommand insert = session.Command(insertSql, "@name");
            insert.Parameters["@name"].Value = name;
            found = insert.ExecuteScalar();
        }

        id = Convert.ToInt64(found);
        cache[name] = id;
        return id;
    }
}
