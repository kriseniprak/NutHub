using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NutHub.Core;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Storage.Events;
using NutHub.Storage.History;
using NutHub.Storage.Maintenance;
using NutHub.Storage.Tests.Support;

namespace NutHub.Storage.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public async Task StorageReplacesTheInMemoryDefaultsAndRecordsASimulatedUps()
    {
        string directory = StorageFixture.CreateTempDirectory();
        await using (var fx = new StorageFixture(directory))
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNutHubStorage(); // before Core on purpose: the order must not matter
            services.AddNutHubCore(new NutHubPaths(directory));
            await using ServiceProvider provider = services.BuildServiceProvider();

            Assert.IsType<SqliteEventStore>(provider.GetRequiredService<IEventStore>());
            Assert.IsType<SqliteHistoryStore>(provider.GetRequiredService<IHistoryStore>());
            List<IHostedService> hosted = provider.GetServices<IHostedService>().ToList();
            HistoryRecorderService recorder = Assert.Single(hosted.OfType<HistoryRecorderService>());
            Assert.Single(hosted.OfType<StorageMaintenanceService>());

            foreach (IHostedService service in hosted)
            {
                await service.StartAsync(CancellationToken.None);
            }

            try
            {
                await provider.GetRequiredService<IConfigStore>().UpdateAsync(
                    c => c.Ups.Add(new UpsConfig { Name = "sim", Driver = "simulated" }), CommandOrigin.System,
                    "Add a simulated UPS");
                IUpsRegistry registry = provider.GetRequiredService<IUpsRegistry>();
                for (int i = 0; i < 300 && registry.Find("sim")?.Snapshot.IsAvailable != true; i++)
                {
                    await Task.Delay(50);
                }

                Assert.True(registry.Find("sim")?.Snapshot.IsAvailable, "the simulated UPS never had data");
                Assert.True(await recorder.SampleOnceAsync() > 0);

                DateTimeOffset now = DateTimeOffset.UtcNow;
                HistoryResult history = await provider.GetRequiredService<IHistoryStore>().QueryAsync(
                    new HistoryQuery("SIM", ["battery.charge", "ups.load"], now.AddHours(-1), now.AddMinutes(1)));
                Assert.Equal(2, history.Series.Count);

                // Core's event recorder moves hub events into the SQLite store.
                provider.GetRequiredService<EventHub>().Publish(new UpsEventMessage(
                    UpsEvent.Create(UpsEventType.OnBattery, now, "sim", "Marker event for the storage test.")));
                IEventStore events = provider.GetRequiredService<IEventStore>();
                var query = new EventQuery(Search: "marker event");
                EventPage page = await events.QueryAsync(query);
                for (int i = 0; i < 100 && page.Items.Count == 0; i++)
                {
                    await Task.Delay(50);
                    page = await events.QueryAsync(query);
                }

                Assert.Equal("sim", Assert.Single(page.Items).Ups);
                Assert.True(File.Exists(Path.Combine(directory, "nuthub.db")));
            }
            finally
            {
                foreach (IHostedService service in Enumerable.Reverse(hosted))
                {
                    await service.StopAsync(CancellationToken.None);
                }
            }
        }
    }
}
