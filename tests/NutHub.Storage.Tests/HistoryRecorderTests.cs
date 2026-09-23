using System.Collections.Immutable;
using NutHub.Core.Abstractions;
using NutHub.Core.Model;
using NutHub.Storage.History;
using NutHub.Storage.Tests.Support;

namespace NutHub.Storage.Tests;

public sealed class HistoryRecorderTests
{
    private static readonly DateTimeOffset T0 = StorageFixture.Start;

    [Fact]
    public void OnlyFreshFiniteNumericValuesOfTheConfiguredVariablesAreCollected()
    {
        UpsSnapshot[] snapshots =
        [
            Snapshot("fresh", DataAvailability.Available, ("battery.charge", "97"), ("ups.status", "OL CHRG"),
                     ("ups.load", "NaN"), ("input.voltage", "231.5"), ("ups.model", "Smart")),
            Snapshot("stale", DataAvailability.Stale, ("battery.charge", "50")),
            Snapshot("gone", DataAvailability.DriverNotConnected, ("battery.charge", "40")),
        ];

        List<HistorySample> samples = HistoryRecorderService.CollectSamples(
            snapshots, ["battery.charge", "ups.status", "ups.load", "input.voltage", "battery.charge", "ups.power", " "]);

        Assert.Equal([new HistorySample("fresh", "battery.charge", 97), new HistorySample("fresh", "input.voltage", 231.5)],
                     samples);
    }

    [Fact]
    public async Task ASamplingRoundStoresEveryUpsAtTheCurrentTime()
    {
        await using var fx = new StorageFixture();
        fx.Config.Update(c => c.History.Variables = ["battery.charge", "ups.load"]);
        UpsSnapshot[] snapshots =
        [
            Snapshot("a", DataAvailability.Available, ("battery.charge", "90"), ("ups.load", "20")),
            Snapshot("b", DataAvailability.Available, ("battery.charge", "80")),
        ];
        var recorder = new HistoryRecorderService(() => snapshots, fx.Config, fx.History, fx.Time,
                                                  fx.Logs.CreateLogger<HistoryRecorderService>());

        Assert.Equal(3, await recorder.SampleOnceAsync());
        fx.Config.Update(c => c.History.Enabled = false);
        fx.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, await recorder.SampleOnceAsync());

        HistoryResult a = await fx.History.QueryAsync(new HistoryQuery("a", ["battery.charge", "ups.load"],
                                                                       T0.AddMinutes(-5), T0.AddMinutes(5)));
        Assert.Equal(T0, Assert.Single(a.Series["battery.charge"]).Timestamp);
        Assert.Equal(20, Assert.Single(a.Series["ups.load"]).Average);
    }

    [Fact]
    public void TicksFallOnMultiplesOfTheInterval()
    {
        Assert.Equal(T0.AddSeconds(30), HistoryRecorderService.NextTick(T0, 30));
        Assert.Equal(T0.AddSeconds(30), HistoryRecorderService.NextTick(T0.AddSeconds(29.9), 30));
        Assert.Equal(T0.AddMinutes(1), HistoryRecorderService.NextTick(T0.AddSeconds(1), 60));
    }

    [Fact]
    public async Task TheRunningRecorderSamplesEachIntervalAndFollowsIntervalChanges()
    {
        await using var fx = new StorageFixture();
        fx.Config.Update(c =>
        {
            c.History.Variables = ["battery.charge"];
            c.History.SampleIntervalSeconds = 3600;
        });
        UpsSnapshot[] snapshots = [Snapshot("ups1", DataAvailability.Available, ("battery.charge", "100"))];
        using var recorder = new HistoryRecorderService(() => snapshots, fx.Config, fx.History, fx.Time,
                                                        fx.Logs.CreateLogger<HistoryRecorderService>());
        await recorder.StartAsync(CancellationToken.None);
        try
        {
            // The service is waiting for the next full hour; a shorter interval must take effect at once.
            await Task.Delay(50);
            fx.Config.Update(c => c.History.SampleIntervalSeconds = 10);

            int count = 0;
            for (int i = 0; i < 400 && count < 3; i++)
            {
                fx.Time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(10);
                count = await CountSamplesAsync(fx);
            }

            Assert.True(count >= 3, $"only {count} samples");
            Assert.True(fx.Time.GetUtcNow() < T0.AddMinutes(5), "the one-hour interval was still in use");
            long[] timestamps = await fx.ReadAsync(c =>
            {
                using var command = c.CreateCommand();
                command.CommandText = "SELECT ts FROM samples ORDER BY ts;";
                using var reader = command.ExecuteReader();
                var list = new List<long>();
                while (reader.Read())
                {
                    list.Add(reader.GetInt64(0));
                }

                return list.ToArray();
            });
            Assert.All(timestamps, ts => Assert.Equal(0, ts % 10));
        }
        finally
        {
            await recorder.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<int> CountSamplesAsync(StorageFixture fx) =>
        (int)await fx.ReadAsync(c => (long)StorageFixture.Scalar(c, "SELECT count(*) FROM samples;")!);

    private static UpsSnapshot Snapshot(string name, DataAvailability availability,
                                        params (string Name, string Value)[] variables) =>
        new()
        {
            Name = name,
            DriverId = "simulated",
            Availability = availability,
            Variables = variables.ToImmutableSortedDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal),
        };
}
