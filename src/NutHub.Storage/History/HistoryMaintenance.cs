using Microsoft.Data.Sqlite;
using NutHub.Core.Configuration;
using NutHub.Storage.Database;

namespace NutHub.Storage.History;

/// <summary>
/// Turns raw samples into 5-minute roll-ups and deletes what is past its retention. Work is done per series
/// (UPS × variable), so every statement is a range scan of the (ups_id, var_id, ts) primary key rather than a scan
/// of the whole table.
/// </summary>
internal sealed class HistoryMaintenance
{
    private const string PairsSql = "SELECT u.id, v.id FROM ups_names u CROSS JOIN variable_names v;";
    private const string UpsIdsSql = "SELECT id FROM ups_names;";
    private const string VariableIdsSql = "SELECT id FROM variable_names;";
    private const string OldestSampleSql = "SELECT min(ts) FROM samples;";

    // Recomputes whole buckets from the raw samples, so running it again over the same range (after the clock was
    // set back, or after a crash between the insert and the watermark update) gives the same result.
    private const string RollupSql =
        "INSERT INTO rollups (ups_id, var_id, ts, avg, min, max, count) " +
        "SELECT ups_id, var_id, (ts / 300) * 300 AS bucket, avg(value), min(value), max(value), count(*) " +
        "FROM samples WHERE ups_id = @ups AND var_id = @var AND ts >= @from AND ts < @to GROUP BY bucket " +
        "ON CONFLICT (ups_id, var_id, ts) DO UPDATE SET " +
        "avg = excluded.avg, min = excluded.min, max = excluded.max, count = excluded.count;";

    private const string DeleteSamplesSql =
        "DELETE FROM samples WHERE ups_id = @ups AND var_id = @var AND ts < @cutoff;";

    private const string DeleteRollupsSql =
        "DELETE FROM rollups WHERE ups_id = @ups AND var_id = @var AND ts < @cutoff;";

    private readonly SqliteDatabase _database;
    private readonly IConfigStore _config;
    private readonly TimeProvider _time;

    public HistoryMaintenance(SqliteDatabase database, IConfigStore config, TimeProvider time)
    {
        _database = database;
        _config = config;
        _time = time;
    }

    /// <summary>
    /// Rolls up every completed 5-minute bucket not rolled up yet (the current bucket is left to the raw samples) and
    /// returns the number of roll-up rows written.
    /// </summary>
    public Task<int> RollUpAsync(CancellationToken cancellationToken = default)
    {
        long end = HistoryLayout.AlignDown(_time.GetUtcNow().ToUnixTimeSeconds(), HistoryLayout.RollupSeconds);
        return _database.WriteAsync((session, ct) =>
        {
            HistoryWriterState state = session.GetState(static () => new HistoryWriterState());
            try
            {
                return session.InTransaction(() =>
                {
                    long start = state.GetWatermark(session) ?? OldestSampleBucket(session) ?? end;
                    int rows = 0;
                    if (start < end)
                    {
                        SqliteCommand rollup = session.Command(RollupSql, "@ups", "@var", "@from", "@to");
                        rollup.Parameters["@from"].Value = start;
                        rollup.Parameters["@to"].Value = end;
                        foreach ((long upsId, long varId) in ReadPairs(session))
                        {
                            ct.ThrowIfCancellationRequested();
                            rollup.Parameters["@ups"].Value = upsId;
                            rollup.Parameters["@var"].Value = varId;
                            rows += rollup.ExecuteNonQuery();
                        }
                    }

                    // Also moves the watermark back when the clock was set back, so queries read raw samples for any
                    // bucket that may still change.
                    if (state.GetWatermark(session) != end)
                    {
                        state.SetWatermark(session, end);
                    }

                    return rows;
                });
            }
            catch
            {
                state.Reset();
                throw;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes raw samples older than the raw retention (never ones not rolled up yet) and roll-ups older than
    /// <c>history.retentionDays</c>. One transaction per UPS keeps each write short.
    /// </summary>
    public async Task<RetentionResult> DeleteExpiredAsync(CancellationToken cancellationToken = default)
    {
        HistorySettings settings = _config.Current.History;
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        long rawCutoff = now - (long)HistoryLayout.RawRetentionDays(settings) * HistoryLayout.SecondsPerDay;
        long rollupCutoff = now - (long)HistoryLayout.RollupRetentionDays(settings) * HistoryLayout.SecondsPerDay;

        (List<long> upsIds, List<long> varIds) = await _database.WriteAsync(
            static (session, _) => (ReadIds(session, UpsIdsSql), ReadIds(session, VariableIdsSql)),
            cancellationToken).ConfigureAwait(false);

        var result = new RetentionResult(0, 0);
        foreach (long upsId in upsIds)
        {
            result += await _database.WriteAsync((session, ct) =>
            {
                HistoryWriterState state = session.GetState(static () => new HistoryWriterState());
                return session.InTransaction(() =>
                {
                    // Until a roll-up has run nothing is safe to drop; after, only what it has covered.
                    long? watermark = state.GetWatermark(session);
                    long sampleCutoff = watermark is { } w ? Math.Min(rawCutoff, w) : long.MinValue;
                    SqliteCommand deleteSamples = session.Command(DeleteSamplesSql, "@ups", "@var", "@cutoff");
                    SqliteCommand deleteRollups = session.Command(DeleteRollupsSql, "@ups", "@var", "@cutoff");
                    int samples = 0, rollups = 0;
                    foreach (long varId in varIds)
                    {
                        ct.ThrowIfCancellationRequested();
                        deleteSamples.Parameters["@ups"].Value = upsId;
                        deleteSamples.Parameters["@var"].Value = varId;
                        deleteSamples.Parameters["@cutoff"].Value = sampleCutoff;
                        samples += deleteSamples.ExecuteNonQuery();

                        deleteRollups.Parameters["@ups"].Value = upsId;
                        deleteRollups.Parameters["@var"].Value = varId;
                        deleteRollups.Parameters["@cutoff"].Value = rollupCutoff;
                        rollups += deleteRollups.ExecuteNonQuery();
                    }

                    return new RetentionResult(samples, rollups);
                });
            }, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Where the very first roll-up starts: the oldest sample (one full scan, once per database).</summary>
    private static long? OldestSampleBucket(WriteSession session)
    {
        object? value = session.Command(OldestSampleSql).ExecuteScalar();
        return value is null or DBNull
            ? null
            : HistoryLayout.AlignDown(Convert.ToInt64(value), HistoryLayout.RollupSeconds);
    }

    private static List<(long UpsId, long VarId)> ReadPairs(WriteSession session)
    {
        var pairs = new List<(long, long)>();
        using SqliteDataReader reader = session.Command(PairsSql).ExecuteReader();
        while (reader.Read())
        {
            pairs.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        return pairs;
    }

    private static List<long> ReadIds(WriteSession session, string sql)
    {
        var ids = new List<long>();
        using SqliteDataReader reader = session.Command(sql).ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }
}

/// <summary>What a retention run deleted.</summary>
internal readonly record struct RetentionResult(int Samples, int Rollups)
{
    public static RetentionResult operator +(RetentionResult a, RetentionResult b) =>
        new(a.Samples + b.Samples, a.Rollups + b.Rollups);
}
