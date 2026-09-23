using System.Diagnostics;
using NutHub.Core.Abstractions;
using NutHub.Storage.History;
using NutHub.Storage.Tests.Support;

namespace NutHub.Storage.Tests;

public sealed class HistoryStoreTests
{
    private const string Charge = "battery.charge";
    private static readonly DateTimeOffset T0 = StorageFixture.Start; // 12:00:00 UTC, a 30-minute boundary

    [Fact]
    public async Task RecentRangesReturnRawSamplesAtTheSampleInterval()
    {
        await using var fx = new StorageFixture();
        for (int i = 0; i < 10; i++)
        {
            await fx.History.AppendSamplesAsync(T0.AddSeconds(30 * i),
                [new HistorySample("ups1", Charge, 90 + i), new HistorySample("ups1", "ups.load", 20)]);
        }

        fx.Time.SetUtcNow(T0.AddSeconds(280));

        HistoryResult result = await fx.History.QueryAsync(
            new HistoryQuery("ups1", [Charge, "ups.load"], T0, T0.AddSeconds(280)));

        Assert.Equal(30, result.StepSeconds);
        IReadOnlyList<HistoryPoint> points = result.Series[Charge];
        Assert.Equal(Enumerable.Range(0, 10).Select(i => T0.AddSeconds(30 * i)), points.Select(p => p.Timestamp));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => 90.0 + i), points.Select(p => p.Average));
        Assert.All(points, p => Assert.True(p.Min == p.Average && p.Max == p.Average));
        Assert.Equal(10, result.Series["ups.load"].Count);
    }

    [Fact]
    public async Task UpsNamesIgnoreCaseAndVariablesWithoutDataAreAbsent()
    {
        await using var fx = new StorageFixture();
        await fx.History.AppendSamplesAsync(T0, [new HistorySample("Rack1", Charge, 50)]);

        HistoryResult result = await fx.History.QueryAsync(
            new HistoryQuery("RACK1", [Charge, "input.voltage", "not.a.variable"], T0.AddHours(-1), T0));
        HistoryResult unknown = await fx.History.QueryAsync(new HistoryQuery("other", [Charge], T0.AddHours(-1), T0));
        HistoryResult reversed = await fx.History.QueryAsync(new HistoryQuery("rack1", [Charge], T0, T0.AddHours(-1)));

        Assert.Equal([Charge], result.Series.Keys);
        Assert.Empty(unknown.Series);
        Assert.Empty(reversed.Series);
    }

    [Fact]
    public async Task RollUpComputesAverageMinimumMaximumAndCountOfCompletedBuckets()
    {
        await using var fx = new StorageFixture();
        (long Ts, double Value)[] samples =
        [
            (T0.ToUnixTimeSeconds(), 1), (T0.AddMinutes(1).ToUnixTimeSeconds(), 5),
            (T0.AddSeconds(299).ToUnixTimeSeconds(), 3), (T0.AddMinutes(5).ToUnixTimeSeconds(), 10),
            (T0.AddSeconds(630).ToUnixTimeSeconds(), 7), // in the current bucket at 12:12
        ];
        await HistoryData.InsertAsync(fx, "ups1", Charge, samples);
        fx.Time.SetUtcNow(T0.AddMinutes(12));

        int rows = await fx.Maintenance.RollUpAsync();

        Assert.Equal(2, rows);
        var rollups = await fx.ReadAsync(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT ts, avg, min, max, count FROM rollups ORDER BY ts;";
            using var reader = command.ExecuteReader();
            var list = new List<(long, double, double, double, long)>();
            while (reader.Read())
            {
                list.Add((reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3),
                          reader.GetInt64(4)));
            }

            return list;
        });
        Assert.Equal(
            [(T0.ToUnixTimeSeconds(), 3.0, 1.0, 5.0, 3L), (T0.AddMinutes(5).ToUnixTimeSeconds(), 10.0, 10.0, 10.0, 1L)],
            rollups);

        // Nothing new: running again writes nothing.
        Assert.Equal(0, await fx.Maintenance.RollUpAsync());
    }

    [Fact]
    public async Task TheCurrentPartialBucketIsIncluded()
    {
        await using var fx = new StorageFixture();
        await fx.History.AppendSamplesAsync(T0.AddMinutes(1), [new HistorySample("ups1", Charge, 80)]);
        fx.Time.SetUtcNow(T0.AddMinutes(11));
        await fx.Maintenance.RollUpAsync();
        await fx.History.AppendSamplesAsync(T0.AddMinutes(10).AddSeconds(30), [new HistorySample("ups1", Charge, 60)]);

        HistoryResult result = await fx.History.QueryAsync(
            new HistoryQuery("ups1", [Charge], T0.AddDays(-30), fx.Time.GetUtcNow()));

        HistoryPoint point = Assert.Single(result.Series[Charge]);
        Assert.Equal(70, point.Average);
        Assert.Equal(60, point.Min);
        Assert.Equal(80, point.Max);
    }

    [Fact]
    public async Task LongRangesCombineRollUpsAndRawSamplesExactly()
    {
        await using var fx = new StorageFixture();
        fx.Config.Update(c => c.History.SampleIntervalSeconds = 60);
        DateTimeOffset now = T0.AddMinutes(7).AddSeconds(17); // the roll-up watermark falls inside a query bucket
        fx.Time.SetUtcNow(now);
        long end = now.ToUnixTimeSeconds();
        List<(long Ts, double Value)> samples = Enumerable.Range(0, 9 * 1440 + 8)
            .Select(i => (end - 9 * 86400L + 60L * i, (i * 7919 % 1000) / 10.0))
            .Where(s => s.Item1 <= end)
            .ToList();
        await HistoryData.InsertAsync(fx, "ups1", Charge, samples);
        await fx.Maintenance.RollUpAsync();
        var query = new HistoryQuery("ups1", [Charge], now.AddDays(-9), now);

        HistoryResult before = await fx.History.QueryAsync(query);
        RetentionResult deleted = await fx.Maintenance.DeleteExpiredAsync();
        HistoryResult after = await fx.History.QueryAsync(query);

        Assert.Equal(1800, before.StepSeconds);
        Assert.Equal(2 * 1440, deleted.Samples); // everything older than 7 days
        List<HistoryPoint> expected = HistoryData.Expected(samples, now.AddDays(-9).ToUnixTimeSeconds(), end, 1800);
        HistoryData.AssertSamePoints(expected, before.Series[Charge]);
        HistoryData.AssertSamePoints(expected, after.Series[Charge]);
        Assert.True(before.Series[Charge].Count <= 500);
    }

    [Fact]
    public async Task RangesOutsideTheRawWindowAreServedFromRollUps()
    {
        await using var fx = new StorageFixture();
        fx.Config.Update(c => c.History.SampleIntervalSeconds = 60);
        DateTimeOffset old = T0.AddDays(-8);
        List<(long Ts, double Value)> samples = Enumerable.Range(0, 60)
            .Select(i => (old.ToUnixTimeSeconds() + 60L * i, (double)i)).ToList();
        await HistoryData.InsertAsync(fx, "ups1", Charge, samples);
        await fx.Maintenance.RollUpAsync();
        await fx.Maintenance.DeleteExpiredAsync();
        long rawLeft = await fx.ReadAsync(c => (long)StorageFixture.Scalar(c, "SELECT count(*) FROM samples;")!);

        HistoryResult result = await fx.History.QueryAsync(new HistoryQuery("ups1", [Charge], old, old.AddHours(1)));

        Assert.Equal(0, rawLeft);
        Assert.Equal(300, result.StepSeconds); // roll-up resolution, although 60 s would fit 500 points
        HistoryData.AssertSamePoints(
            HistoryData.Expected(samples, old.ToUnixTimeSeconds(), old.AddHours(1).ToUnixTimeSeconds(), 300),
            result.Series[Charge]);
    }

    [Fact]
    public async Task ASampleOlderThanTheWatermarkReopensItsBucket()
    {
        await using var fx = new StorageFixture();
        await fx.History.AppendSamplesAsync(T0.AddMinutes(1), [new HistorySample("ups1", Charge, 10)]);
        fx.Time.SetUtcNow(T0.AddMinutes(7));
        await fx.Maintenance.RollUpAsync();

        // The clock was set back: this round is stamped inside an already rolled-up bucket.
        await fx.History.AppendSamplesAsync(T0.AddMinutes(2), [new HistorySample("ups1", Charge, 20)]);
        await fx.Maintenance.RollUpAsync();

        (double avg, long count) = await fx.ReadAsync(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT avg, count FROM rollups;";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            return (reader.GetDouble(0), reader.GetInt64(1));
        });
        Assert.Equal(15, avg);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task RetentionKeepsRawSamplesSevenDaysAndRollUpsForTheRetention()
    {
        await using var fx = new StorageFixture();
        long now = T0.ToUnixTimeSeconds();
        (long, double)[] samples = [(now - 100 * 86400L, 1), (now - 10 * 86400L, 2), (now - 2 * 86400L, 3)];
        await HistoryData.InsertAsync(fx, "ups1", Charge, samples);

        // Nothing rolled up yet: raw samples are never deleted before their roll-up exists.
        Assert.Equal(0, (await fx.Maintenance.DeleteExpiredAsync()).Samples);

        await fx.Maintenance.RollUpAsync();
        RetentionResult deleted = await fx.Maintenance.DeleteExpiredAsync();

        Assert.Equal(new RetentionResult(2, 1), deleted);
        Assert.Equal([now - 2 * 86400L], await Timestamps(fx, "samples"));
        Assert.Equal([HistoryLayout.AlignDown(now - 10 * 86400L, 300), HistoryLayout.AlignDown(now - 2 * 86400L, 300)],
                     await Timestamps(fx, "rollups"));
    }

    [Fact]
    public async Task AShortRetentionAlsoShortensTheRawWindow()
    {
        await using var fx = new StorageFixture();
        fx.Config.Update(c => c.History.RetentionDays = 3);
        long now = T0.ToUnixTimeSeconds();
        await HistoryData.InsertAsync(fx, "ups1", Charge, [(now - 5 * 86400L, 1), (now - 2 * 86400L, 2)]);
        await fx.Maintenance.RollUpAsync();

        RetentionResult deleted = await fx.Maintenance.DeleteExpiredAsync();

        Assert.Equal(new RetentionResult(1, 1), deleted);
        Assert.Equal([now - 2 * 86400L], await Timestamps(fx, "samples"));
    }

    [Theory]
    [InlineData(3600, 500, 30, 1, 30)]
    [InlineData(86400, 500, 30, 1, 180)]
    [InlineData(7 * 86400, 500, 30, 1, 1800)]
    [InlineData(30 * 86400, 500, 30, 300, 7200)]
    [InlineData(90 * 86400, 500, 30, 300, 21600)]
    [InlineData(3600, 500, 45, 1, 60)]
    [InlineData(3600, 500, 30, 300, 300)]
    [InlineData(3600, 10, 30, 1, 600)]
    [InlineData(365 * 86400, 100, 30, 300, 4 * 86400)]
    public void StepIsTheSmallestReadableWidthThatFits(long span, int maxPoints, int interval, int multipleOf,
                                                      int expected)
    {
        long from = T0.ToUnixTimeSeconds() - span;
        Assert.Equal(expected, HistoryLayout.ChooseStep(from, from + span, maxPoints, interval, multipleOf));
    }

    [Fact]
    public void StepAlwaysRespectsMaxPointsIntervalAndRollUpAlignment()
    {
        var random = new Random(1234);
        for (int i = 0; i < 2000; i++)
        {
            long from = 1_700_000_000L + random.Next(0, 10_000_000);
            long to = from + random.NextInt64(0, 400L * 86400);
            int maxPoints = random.Next(1, 2000);
            int interval = random.Next(5, 3601);
            int multipleOf = random.Next(2) == 0 ? 1 : 300;

            int step = HistoryLayout.ChooseStep(from, to, maxPoints, interval, multipleOf);

            Assert.True(HistoryLayout.BucketCount(from, to, step) <= maxPoints, $"{from} {to} {maxPoints} {step}");
            Assert.True(step >= interval);
            Assert.Equal(0, step % multipleOf);
        }
    }

    [Fact]
    public async Task ThirtyDaysOfThreeVariablesAreQueriedInUnderASecond()
    {
        await using var fx = new StorageFixture();
        long end = T0.ToUnixTimeSeconds();
        string[] variables = [Charge, "ups.load", "input.voltage"];
        foreach (string variable in variables)
        {
            await HistoryData.InsertAsync(fx, "ups1", variable,
                                          Enumerable.Range(0, 30 * 2880)
                                                    .Select(i => (end - 30 * 86400L + 30L * i, 50.0 + i % 50)));
        }

        await fx.Maintenance.RollUpAsync();

        var stopwatch = Stopwatch.StartNew();
        HistoryResult month = await fx.History.QueryAsync(new HistoryQuery("ups1", variables, T0.AddDays(-30), T0));
        TimeSpan monthTime = stopwatch.Elapsed;
        stopwatch.Restart();
        HistoryResult week = await fx.History.QueryAsync(new HistoryQuery("ups1", variables, T0.AddDays(-7), T0));
        TimeSpan weekTime = stopwatch.Elapsed;

        Assert.Equal(3, month.Series.Count);
        Assert.All(month.Series.Values, s => Assert.InRange(s.Count, 350, 500));
        Assert.Equal(3, week.Series.Count);
        Assert.True(monthTime < TimeSpan.FromSeconds(1), $"30 days took {monthTime}");
        Assert.True(weekTime < TimeSpan.FromSeconds(1), $"7 days of raw samples took {weekTime}");
    }

    [Fact]
    public async Task SamplingRoundsWhileQueryingStayConsistent()
    {
        await using var fx = new StorageFixture();
        SqliteHistoryStore store = fx.History;
        using var stop = new CancellationTokenSource();
        Task reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HistoryResult result = await store.QueryAsync(new HistoryQuery("ups1", [Charge], T0, T0.AddHours(2)));
                if (result.Series.TryGetValue(Charge, out IReadOnlyList<HistoryPoint>? points))
                {
                    Assert.All(points, p => Assert.Equal(42, p.Average));
                }
            }
        });

        await Task.WhenAll(Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await store.AppendSamplesAsync(T0.AddSeconds(30 * (w * 50 + i)),
                                               [new HistorySample("ups1", Charge, 42), new HistorySample($"ups{w + 2}", Charge, 1)]);
            }
        })));
        stop.Cancel();
        await reader;

        long count = await fx.ReadAsync(c => (long)StorageFixture.Scalar(c, "SELECT count(*) FROM samples;")!);
        Assert.Equal(400, count);
    }

    private static Task<long[]> Timestamps(StorageFixture fx, string table) =>
        fx.ReadAsync(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = $"SELECT ts FROM {table} ORDER BY ts;";
            using var reader = command.ExecuteReader();
            var list = new List<long>();
            while (reader.Read())
            {
                list.Add(reader.GetInt64(0));
            }

            return list.ToArray();
        });
}
