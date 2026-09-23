using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NutHub.Core.Abstractions;
using NutHub.Core.Model;
using NutHub.Storage.Database;

namespace NutHub.Storage.Events;

/// <summary>
/// The event log in the <c>events</c> table. Timestamps are stored as Unix milliseconds and come back in UTC; the UPS
/// name is compared ignoring case (like upsd), as is the searched text for ASCII letters (SQLite's LIKE).
/// </summary>
public sealed class SqliteEventStore : IEventStore
{
    public const int MaxLimit = 1000;

    // Deleting in slices keeps each write short, so events and samples keep flowing during a large clean-up
    // (after the retention was shortened, for example).
    private const int DeleteBatchSize = 5000;

    private const string InsertSql =
        "INSERT INTO events (ts, ups, type, severity, category, message, actor, data) " +
        "VALUES (@ts, @ups, @type, @severity, @category, @message, @actor, @data) RETURNING id;";

    private static readonly string DeleteSql =
        $"DELETE FROM events WHERE id IN (SELECT id FROM events WHERE ts < @cutoff LIMIT {DeleteBatchSize});";

    private readonly SqliteDatabase _database;
    private readonly ILogger<SqliteEventStore> _logger;

    internal SqliteEventStore(SqliteDatabase database, ILogger<SqliteEventStore> logger)
    {
        _database = database;
        _logger = logger;
    }

    public async ValueTask<UpsEvent> AppendAsync(UpsEvent e, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(e);
        long id = await _database.WriteAsync((session, _) =>
        {
            SqliteCommand command = session.Command(InsertSql, "@ts", "@ups", "@type", "@severity", "@category",
                                                    "@message", "@actor", "@data");
            command.Parameters["@ts"].Value = e.Timestamp.ToUnixTimeMilliseconds();
            command.Parameters["@ups"].Value = (object?)e.Ups ?? DBNull.Value;
            command.Parameters["@type"].Value = e.Type.ToString();
            command.Parameters["@severity"].Value = (int)e.Severity;
            command.Parameters["@category"].Value = e.Category.ToString();
            command.Parameters["@message"].Value = e.Message ?? string.Empty;
            command.Parameters["@actor"].Value = (object?)e.Actor ?? DBNull.Value;
            command.Parameters["@data"].Value = (object?)EventDataJson.Serialize(e.Data) ?? DBNull.Value;
            return Convert.ToInt64(command.ExecuteScalar());
        }, cancellationToken).ConfigureAwait(false);

        return e with { Id = id };
    }

    /// <summary>
    /// Newest first (by id). An empty <see cref="EventQuery.Ups"/> or <see cref="EventQuery.Search"/> means no filter.
    /// </summary>
    public Task<EventPage> QueryAsync(EventQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        int limit = Math.Clamp(query.Limit, 1, MaxLimit);
        return _database.ReadAsync((connection, ct) =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = BuildQuery(query, command.Parameters, limit + 1);

            var items = new List<UpsEvent>(Math.Min(limit + 1, 256));
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (ReadEvent(reader) is { } e)
                    {
                        items.Add(e);
                    }
                }
            }

            bool hasMore = items.Count > limit;
            if (hasMore)
            {
                items.RemoveRange(limit, items.Count - limit);
            }

            return new EventPage(items, hasMore);
        }, cancellationToken);
    }

    /// <summary>Deletes the events recorded before <paramref name="cutoff"/>; returns how many.</summary>
    internal async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        long cutoffMs = cutoff.ToUnixTimeMilliseconds();
        int total = 0;
        while (true)
        {
            int deleted = await _database.WriteAsync((session, _) =>
            {
                SqliteCommand command = session.Command(DeleteSql, "@cutoff");
                command.Parameters["@cutoff"].Value = cutoffMs;
                return command.ExecuteNonQuery();
            }, cancellationToken).ConfigureAwait(false);

            total += deleted;
            if (deleted < DeleteBatchSize)
            {
                return total;
            }
        }
    }

    internal static string BuildQuery(EventQuery query, SqliteParameterCollection parameters, int rowLimit)
    {
        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Ups))
        {
            // The column is COLLATE NOCASE, so this is case-insensitive and still uses the (ups, id) index.
            conditions.Add("ups = @ups");
            parameters.AddWithValue("@ups", query.Ups.Trim());
        }

        if (query.MinSeverity is { } severity)
        {
            conditions.Add("severity >= @severity");
            parameters.AddWithValue("@severity", (int)severity);
        }

        if (query.Category is { } category)
        {
            conditions.Add("category = @category");
            parameters.AddWithValue("@category", category.ToString());
        }

        if (query.From is { } from)
        {
            conditions.Add("ts >= @from");
            parameters.AddWithValue("@from", from.ToUnixTimeMilliseconds());
        }

        if (query.To is { } to)
        {
            conditions.Add("ts < @to");
            parameters.AddWithValue("@to", to.ToUnixTimeMilliseconds());
        }

        if (query.BeforeId is { } beforeId)
        {
            conditions.Add("id < @beforeId");
            parameters.AddWithValue("@beforeId", beforeId);
        }

        if (!string.IsNullOrEmpty(query.Search))
        {
            conditions.Add(@"message LIKE @search ESCAPE '\'");
            parameters.AddWithValue("@search", "%" + EscapeLike(query.Search) + "%");
        }

        var sql = new StringBuilder("SELECT id, ts, ups, type, severity, category, message, actor, data FROM events");
        if (conditions.Count > 0)
        {
            sql.Append(" WHERE ").AppendJoin(" AND ", conditions);
        }

        sql.Append(" ORDER BY id DESC LIMIT @limit;");
        parameters.AddWithValue("@limit", rowLimit);
        return sql.ToString();
    }

    /// <summary>Makes '%' and '_' typed by the user match themselves instead of acting as wildcards.</summary>
    internal static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private UpsEvent? ReadEvent(SqliteDataReader reader)
    {
        long id = reader.GetInt64(0);
        string typeName = reader.GetString(3);
        if (!Enum.TryParse(typeName, ignoreCase: false, out UpsEventType type) || !Enum.IsDefined(type))
        {
            // Only possible if an event type is ever retired; skipping is better than failing the whole page.
            _logger.LogDebug("Skipping event {Id} of unknown type {Type}.", id, typeName);
            return null;
        }

        int rank = reader.GetInt32(4);
        EventSeverity severity = Enum.IsDefined((EventSeverity)rank)
            ? (EventSeverity)rank
            : UpsEventCatalog.DefaultSeverity(type);
        EventCategory category = Enum.TryParse(reader.GetString(5), ignoreCase: false, out EventCategory parsed) &&
                                 Enum.IsDefined(parsed)
            ? parsed
            : UpsEventCatalog.CategoryOf(type);

        return new UpsEvent
        {
            Id = id,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            Ups = reader.IsDBNull(2) ? null : reader.GetString(2),
            Type = type,
            Severity = severity,
            Category = category,
            Message = reader.GetString(6),
            Actor = reader.IsDBNull(7) ? null : reader.GetString(7),
            Data = reader.IsDBNull(8) ? null : EventDataJson.Deserialize(reader.GetString(8)),
        };
    }
}
