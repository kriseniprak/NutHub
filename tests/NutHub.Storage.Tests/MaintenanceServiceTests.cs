using NutHub.Core.Abstractions;
using NutHub.Core.Model;
using NutHub.Storage.History;
using NutHub.Storage.Maintenance;
using NutHub.Storage.Tests.Support;

namespace NutHub.Storage.Tests;

public sealed class MaintenanceServiceTests
{
    private static readonly DateTimeOffset T0 = StorageFixture.Start;

    [Theory]
    [InlineData(437, 620)] // 12:07:17 -> 12:10:20
    [InlineData(605, 620)] // 12:10:05 -> 12:10:20
    [InlineData(620, 920)] // 12:10:20 -> 12:15:20
    public void RollUpsRunShortlyAfterEachFiveMinuteBoundary(int nowOffset, int expectedOffset)
    {
        Assert.Equal(T0.AddSeconds(expectedOffset), StorageMaintenanceService.NextRollup(T0.AddSeconds(nowOffset)));
    }

    [Fact]
    public async Task StartupRollsUpAndAppliesRetention()
    {
        await using var fx = new StorageFixture();
        fx.Config.Update(c => c.History.EventRetentionDays = 30);
        await fx.Events.AppendAsync(StorageFixture.Event(T0.AddDays(-31), "u", UpsEventType.OnBattery, "Old"));
        await fx.Events.AppendAsync(StorageFixture.Event(T0.AddDays(-29), "u", UpsEventType.Online, "Recent"));
        await HistoryData.InsertAsync(fx, "u", "battery.charge", [(T0.AddDays(-10).ToUnixTimeSeconds(), 50)]);

        using var service = new StorageMaintenanceService(fx.Database, fx.Maintenance, fx.Events, fx.Config, fx.Time,
                                                          fx.Logs.CreateLogger<StorageMaintenanceService>());
        await service.StartAsync(CancellationToken.None);
        try
        {
            EventPage page = await fx.Events.QueryAsync(new EventQuery());
            long samples = 1;
            for (int i = 0; i < 200 && (page.Items.Count > 1 || samples > 0); i++)
            {
                await Task.Delay(20);
                page = await fx.Events.QueryAsync(new EventQuery());
                samples = await fx.ReadAsync(c => (long)StorageFixture.Scalar(c, "SELECT count(*) FROM samples;")!);
            }

            Assert.Equal("Recent", Assert.Single(page.Items).Message);
            Assert.Equal(0, samples); // rolled up, then past the raw window
            HistoryResult history = await fx.History.QueryAsync(
                new HistoryQuery("u", ["battery.charge"], T0.AddDays(-11), T0));
            Assert.Equal(50, Assert.Single(history.Series["battery.charge"]).Average);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TheDailyJobChecksAndCompactsTheDatabase()
    {
        await using var fx = new StorageFixture();
        await HistoryData.InsertAsync(fx, "u", "battery.charge",
                                      Enumerable.Range(0, 20_000).Select(i => ((long)i * 30, 1.0)));
        await fx.Database.WriteAsync((session, _) =>
        {
            session.Command("DELETE FROM samples;").ExecuteNonQuery();
            return true;
        });
        long freeBefore = await fx.ReadAsync(c => (long)StorageFixture.Scalar(c, "PRAGMA freelist_count;")!);

        using var service = new StorageMaintenanceService(fx.Database, fx.Maintenance, fx.Events, fx.Config, fx.Time,
                                                          fx.Logs.CreateLogger<StorageMaintenanceService>());
        await service.OptimizeAsync(CancellationToken.None);

        long freeAfter = await fx.ReadAsync(c => (long)StorageFixture.Scalar(c, "PRAGMA freelist_count;")!);
        Assert.True(freeBefore > 0);
        Assert.Equal(0, freeAfter);
    }
}
