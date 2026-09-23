using Microsoft.Data.Sqlite;
using NutHub.Core.Abstractions;
using NutHub.Storage.History;

namespace NutHub.Storage.Tests.Support;

/// <summary>Bulk history data for tests, and the reference aggregation query results are compared with.</summary>
internal static class HistoryData
{
    /// <summary>Inserts samples straight into the table (much faster than sampling rounds for weeks of data).</summary>
    public static Task<int> InsertAsync(StorageFixture fx, string ups, string variable,
                                        IEnumerable<(long Ts, double Value)> samples) =>
        fx.Database.WriteAsync((session, _) =>
        {
            HistoryWriterState state = session.GetState(static () => new HistoryWriterState());
            return session.InTransaction(() =>
            {
                long upsId = state.UpsId(session, ups);
                long varId = state.VariableId(session, variable);
                SqliteCommand insert = session.Command(
                    "INSERT INTO samples (ups_id, var_id, ts, value) VALUES (@u, @v, @t, @x);", "@u", "@v", "@t", "@x");
                insert.Parameters["@u"].Value = upsId;
                insert.Parameters["@v"].Value = varId;
                int count = 0;
                foreach ((long ts, double value) in samples)
                {
                    insert.Parameters["@t"].Value = ts;
                    insert.Parameters["@x"].Value = value;
                    insert.ExecuteNonQuery();
                    count++;
                }

                return count;
            });
        });

    /// <summary>What a query should return: the samples in [aligned from, to] averaged into aligned buckets.</summary>
    public static List<HistoryPoint> Expected(IEnumerable<(long Ts, double Value)> samples, long from, long to,
                                              int step)
    {
        long alignedFrom = HistoryLayout.AlignDown(from, step);
        return samples.Where(s => s.Ts >= alignedFrom && s.Ts <= to)
                      .GroupBy(s => HistoryLayout.AlignDown(s.Ts, step))
                      .OrderBy(g => g.Key)
                      .Select(g => new HistoryPoint(DateTimeOffset.FromUnixTimeSeconds(g.Key),
                                                    g.Average(s => s.Value), g.Min(s => s.Value),
                                                    g.Max(s => s.Value)))
                      .ToList();
    }

    public static void AssertSamePoints(IReadOnlyList<HistoryPoint> expected, IReadOnlyList<HistoryPoint> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Timestamp, actual[i].Timestamp);
            Assert.Equal(expected[i].Average, actual[i].Average, 9);
            Assert.Equal(expected[i].Min, actual[i].Min, 9);
            Assert.Equal(expected[i].Max, actual[i].Max, 9);
        }
    }
}
