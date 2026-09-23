using Microsoft.Data.Sqlite;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Storage.Database;

namespace NutHub.Storage.History;

/// <summary>One numeric value of one UPS variable in a sampling round.</summary>
internal readonly record struct HistorySample(string Ups, string Variable, double Value);

/// <summary>
/// The time series of the numeric UPS variables: raw samples for the last days, 5-minute roll-ups beyond (see
/// <see cref="HistoryMaintenance"/>). History is keyed by UPS name: a renamed UPS starts a new series and the old one
/// stays readable under the old name until it expires; a deleted UPS's history expires the same way.
/// </summary>
public sealed class SqliteHistoryStore : IHistoryStore
{
    private const string InsertSampleSql =
        "INSERT INTO samples (ups_id, var_id, ts, value) VALUES (@ups, @var, @ts, @value) " +
        "ON CONFLICT (ups_id, var_id, ts) DO UPDATE SET value = excluded.value;";

    private const string SelectUpsSql = "SELECT id FROM ups_names WHERE name = @name;";
    private const string SelectVariableSql = "SELECT id FROM variable_names WHERE name = @name;";
    private const string SelectWatermarkSql = "SELECT value FROM storage_state WHERE key = @key;";

    // Each query bucket gathers the sum and the count of its values, so raw samples and roll-ups (which carry their
    // count) combine into an exact average when one bucket takes from both.
    private const string RawBucketsSql =
        "SELECT (ts / @step) * @step AS bucket, sum(value), count(*), min(value), max(value) FROM samples " +
        "WHERE ups_id = @ups AND var_id = @var AND ts >= @from AND ts <= @to GROUP BY bucket ORDER BY bucket;";

    private const string RollupBucketsSql =
        "SELECT (ts / @step) * @step AS bucket, sum(avg * count), sum(count), min(min), max(max) FROM rollups " +
        "WHERE ups_id = @ups AND var_id = @var AND ts >= @from AND ts < @to GROUP BY bucket ORDER BY bucket;";

    private readonly SqliteDatabase _database;
    private readonly IConfigStore _config;
    private readonly TimeProvider _time;

    internal SqliteHistoryStore(SqliteDatabase database, IConfigStore config, TimeProvider time)
    {
        _database = database;
        _config = config;
        _time = time;
    }

    /// <summary>
    /// Buckets are aligned to multiples of <see cref="HistoryResult.StepSeconds"/> since the Unix epoch and stamped
    /// with their start, so the first point may be up to one step before <see cref="HistoryQuery.From"/>. The last
    /// bucket is the current, partial one when the range reaches the present.
    /// </summary>
    public Task<HistoryResult> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        HistorySettings settings = _config.Current.History;
        long fromSeconds = query.From.ToUnixTimeSeconds();
        long toSeconds = query.To.ToUnixTimeSeconds();
        long nowSeconds = _time.GetUtcNow().ToUnixTimeSeconds();
        int maxPoints = query.MaxPoints <= 0
            ? HistoryLayout.DefaultMaxPoints
            : Math.Min(query.MaxPoints, HistoryLayout.MaxPointsLimit);
        int minimumStep = Math.Clamp(settings.SampleIntervalSeconds, 1, 3600);

        // Raw samples cover the whole range only if it starts inside the raw window; otherwise the old part comes
        // from the roll-ups, whose buckets must then fit exactly into the query buckets.
        long rawWindowStart = nowSeconds - (long)HistoryLayout.RawRetentionDays(settings) * HistoryLayout.SecondsPerDay;
        bool useRollups = fromSeconds < rawWindowStart;
        int step = HistoryLayout.ChooseStep(fromSeconds, toSeconds, maxPoints, minimumStep,
                                            useRollups ? HistoryLayout.RollupSeconds : 1);

        List<string> variables = (query.Variables ?? [])
                                 .Where(v => !string.IsNullOrWhiteSpace(v))
                                 .Distinct(StringComparer.Ordinal)
                                 .ToList();
        if (toSeconds < fromSeconds || variables.Count == 0 || string.IsNullOrWhiteSpace(query.Ups))
        {
            return Task.FromResult(Empty(query, step));
        }

        var plan = new QueryPlan(query.Ups.Trim(), variables, HistoryLayout.AlignDown(fromSeconds, step), toSeconds,
                                 step, useRollups);
        return _database.ReadAsync((connection, ct) => new HistoryResult(query.From, query.To, step,
                                                                         ReadSeries(connection, plan, ct)),
                                   cancellationToken);
    }

    /// <summary>
    /// Stores one sampling round in a single transaction. A sample stamped before the roll-up watermark (the clock
    /// was set back) moves the watermark back, so its bucket is rolled up again.
    /// </summary>
    internal Task<int> AppendSamplesAsync(DateTimeOffset timestamp, IReadOnlyList<HistorySample> samples,
                                          CancellationToken cancellationToken = default)
    {
        if (samples.Count == 0)
        {
            return Task.FromResult(0);
        }

        long ts = timestamp.ToUnixTimeSeconds();
        return _database.WriteAsync((session, _) =>
        {
            HistoryWriterState state = session.GetState(static () => new HistoryWriterState());
            try
            {
                return session.InTransaction(() =>
                {
                    SqliteCommand insert = session.Command(InsertSampleSql, "@ups", "@var", "@ts", "@value");
                    foreach (HistorySample sample in samples)
                    {
                        insert.Parameters["@ups"].Value = state.UpsId(session, sample.Ups);
                        insert.Parameters["@var"].Value = state.VariableId(session, sample.Variable);
                        insert.Parameters["@ts"].Value = ts;
                        insert.Parameters["@value"].Value = sample.Value;
                        insert.ExecuteNonQuery();
                    }

                    long bucket = HistoryLayout.AlignDown(ts, HistoryLayout.RollupSeconds);
                    if (state.GetWatermark(session) is { } watermark && bucket < watermark)
                    {
                        state.SetWatermark(session, bucket);
                    }

                    return samples.Count;
                });
            }
            catch
            {
                state.Reset();
                throw;
            }
        }, cancellationToken);
    }

    private static HistoryResult Empty(HistoryQuery query, int step) =>
        new(query.From, query.To, step, new Dictionary<string, IReadOnlyList<HistoryPoint>>());

    private static Dictionary<string, IReadOnlyList<HistoryPoint>> ReadSeries(SqliteConnection connection,
                                                                               QueryPlan plan, CancellationToken ct)
    {
        var series = new Dictionary<string, IReadOnlyList<HistoryPoint>>(StringComparer.Ordinal);

        // One read transaction: the watermark, the roll-ups and the raw samples come from the same snapshot, so a
        // roll-up or a retention run in between cannot count a sample twice or drop it.
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        if (LookupId(connection, transaction, SelectUpsSql, plan.Ups) is not { } upsId)
        {
            return series;
        }

        long split = plan.AlignedFrom;
        if (plan.UseRollups)
        {
            using SqliteCommand watermarkCommand = connection.CreateCommand();
            watermarkCommand.Transaction = transaction;
            watermarkCommand.CommandText = SelectWatermarkSql;
            watermarkCommand.Parameters.AddWithValue("@key", HistoryLayout.WatermarkKey);
            object? value = watermarkCommand.ExecuteScalar();
            long watermark = value is null or DBNull ? plan.AlignedFrom : Convert.ToInt64(value);
            split = Math.Clamp(watermark, plan.AlignedFrom, plan.ToSeconds + 1);
        }

        using SqliteCommand rollups = CreateBucketCommand(connection, transaction, RollupBucketsSql, plan.Step,
                                                          upsId, plan.AlignedFrom, split);
        using SqliteCommand raw = CreateBucketCommand(connection, transaction, RawBucketsSql, plan.Step, upsId, split,
                                                      plan.ToSeconds);
        var buckets = new List<Bucket>();
        foreach (string variable in plan.Variables)
        {
            ct.ThrowIfCancellationRequested();
            if (LookupId(connection, transaction, SelectVariableSql, variable) is not { } varId)
            {
                continue;
            }

            buckets.Clear();
            if (split > plan.AlignedFrom)
            {
                rollups.Parameters["@var"].Value = varId;
                ReadBuckets(rollups, buckets, ct);
            }

            raw.Parameters["@var"].Value = varId;
            ReadBuckets(raw, buckets, ct);

            if (buckets.Count > 0)
            {
                var points = new HistoryPoint[buckets.Count];
                for (int i = 0; i < points.Length; i++)
                {
                    Bucket b = buckets[i];
                    points[i] = new HistoryPoint(DateTimeOffset.FromUnixTimeSeconds(b.Start), b.Sum / b.Count, b.Min,
                                                 b.Max);
                }

                series[variable] = points;
            }
        }

        return series;
    }

    private static SqliteCommand CreateBucketCommand(SqliteConnection connection, SqliteTransaction transaction,
                                                     string sql, int step, long upsId, long from, long to)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("@step", step);
        command.Parameters.AddWithValue("@ups", upsId);
        command.Parameters.AddWithValue("@var", 0L);
        command.Parameters.AddWithValue("@from", from);
        command.Parameters.AddWithValue("@to", to);
        return command;
    }

    /// <summary>Appends the command's buckets (in time order) to <paramref name="buckets"/>, merging a shared one.</summary>
    private static void ReadBuckets(SqliteCommand command, List<Bucket> buckets, CancellationToken ct)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            long count = reader.GetInt64(2);
            if (count <= 0)
            {
                continue;
            }

            var bucket = new Bucket(reader.GetInt64(0), reader.GetDouble(1), count, reader.GetDouble(3),
                                    reader.GetDouble(4));
            if (buckets.Count > 0 && buckets[^1].Start == bucket.Start)
            {
                // The bucket that straddles the roll-up watermark: part roll-ups, part raw samples.
                Bucket previous = buckets[^1];
                buckets[^1] = new Bucket(bucket.Start, previous.Sum + bucket.Sum, previous.Count + bucket.Count,
                                         Math.Min(previous.Min, bucket.Min), Math.Max(previous.Max, bucket.Max));
            }
            else
            {
                buckets.Add(bucket);
            }
        }
    }

    private static long? LookupId(SqliteConnection connection, SqliteTransaction transaction, string sql, string name)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("@name", name);
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private sealed record QueryPlan(string Ups, IReadOnlyList<string> Variables, long AlignedFrom, long ToSeconds,
                                    int Step, bool UseRollups);

    private readonly record struct Bucket(long Start, double Sum, long Count, double Min, double Max);
}
