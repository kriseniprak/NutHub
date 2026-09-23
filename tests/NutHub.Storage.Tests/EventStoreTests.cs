using Microsoft.Data.Sqlite;
using NutHub.Core.Abstractions;
using NutHub.Core.Model;
using NutHub.Storage.Events;
using NutHub.Storage.Tests.Support;

namespace NutHub.Storage.Tests;

public sealed class EventStoreTests
{
    private static readonly DateTimeOffset T0 = StorageFixture.Start;

    [Fact]
    public async Task AppendReturnsIncreasingIdsAndEveryFieldRoundTrips()
    {
        await using var fx = new StorageFixture();
        SqliteEventStore store = fx.Events;
        var data = new Dictionary<string, string> { ["battery.charge"] = "42", ["quote\"d"] = "é ✓" };
        UpsEvent first = await store.AppendAsync(new UpsEvent
        {
            Timestamp = T0.AddMilliseconds(123).ToOffset(TimeSpan.FromHours(2)),
            Ups = "rack1",
            Type = UpsEventType.CommandExecuted,
            Severity = EventSeverity.Warning,
            Category = EventCategory.Command,
            Message = "Command beeper.disable executed.",
            Actor = "web:admin@192.168.1.10",
            Data = data,
        });
        UpsEvent second = await store.AppendAsync(StorageFixture.Event(T0, null, UpsEventType.ServerStarted, "Up"));

        Assert.True(first.Id > 0);
        Assert.True(second.Id > first.Id);

        EventPage page = await store.QueryAsync(new EventQuery());
        Assert.Equal([second.Id, first.Id], page.Items.Select(e => e.Id));
        UpsEvent read = page.Items[1];
        Assert.Equal(first.Timestamp, read.Timestamp); // same instant (returned in UTC)
        Assert.Equal(TimeSpan.Zero, read.Timestamp.Offset);
        Assert.Equal("rack1", read.Ups);
        Assert.Equal(UpsEventType.CommandExecuted, read.Type);
        Assert.Equal(EventSeverity.Warning, read.Severity);
        Assert.Equal(EventCategory.Command, read.Category);
        Assert.Equal(first.Message, read.Message);
        Assert.Equal(first.Actor, read.Actor);
        Assert.Equal(data, read.Data);
        Assert.Null(page.Items[0].Ups);
        Assert.Null(page.Items[0].Data);
    }

    [Fact]
    public async Task QueryFiltersByUpsIgnoringCase()
    {
        await using var fx = new StorageFixture();
        await AppendAsync(fx, (T0, "Rack1", UpsEventType.OnBattery), (T0, "rack2", UpsEventType.OnBattery),
                          (T0, null, UpsEventType.ServerStarted), (T0, "RACK1", UpsEventType.Online));

        EventPage page = await fx.Events.QueryAsync(new EventQuery(Ups: "rack1"));

        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, e => Assert.Equal("rack1", e.Ups, ignoreCase: true));
        Assert.Equal(4, (await fx.Events.QueryAsync(new EventQuery(Ups: ""))).Items.Count);
    }

    [Fact]
    public async Task QueryFiltersByMinimumSeverityAndCategory()
    {
        await using var fx = new StorageFixture();
        await AppendAsync(fx, (T0, "u", UpsEventType.LowBattery), // critical, power
                          (T0, "u", UpsEventType.OnBattery), // warning, power
                          (T0, "u", UpsEventType.Online), // notice, power
                          (T0, "u", UpsEventType.CommunicationLost), // warning, communication
                          (T0, "u", UpsEventType.TestStarted)); // info, device

        EventPage warnings = await fx.Events.QueryAsync(new EventQuery(MinSeverity: EventSeverity.Warning));
        EventPage power = await fx.Events.QueryAsync(new EventQuery(Category: EventCategory.Power));
        EventPage both = await fx.Events.QueryAsync(new EventQuery(MinSeverity: EventSeverity.Warning,
                                                                    Category: EventCategory.Power));

        Assert.Equal(3, warnings.Items.Count);
        Assert.All(warnings.Items, e => Assert.True(e.Severity >= EventSeverity.Warning));
        Assert.Equal(3, power.Items.Count);
        Assert.All(power.Items, e => Assert.Equal(EventCategory.Power, e.Category));
        Assert.Equal([UpsEventType.OnBattery, UpsEventType.LowBattery], both.Items.Select(e => e.Type));
    }

    [Fact]
    public async Task QueryFiltersByTimeWithAnInclusiveStartAndAnExclusiveEnd()
    {
        await using var fx = new StorageFixture();
        await AppendAsync(fx, (T0, "u", UpsEventType.OnBattery), (T0.AddMinutes(1), "u", UpsEventType.Online),
                          (T0.AddMinutes(2), "u", UpsEventType.OnBattery), (T0.AddMinutes(3), "u", UpsEventType.Online));

        EventPage page = await fx.Events.QueryAsync(new EventQuery(From: T0.AddMinutes(1), To: T0.AddMinutes(3)));

        Assert.Equal([T0.AddMinutes(2), T0.AddMinutes(1)], page.Items.Select(e => e.Timestamp));
    }

    [Fact]
    public async Task PagingWithBeforeIdWalksEveryEventOnceNewestFirst()
    {
        await using var fx = new StorageFixture();
        var ids = new List<long>();
        for (int i = 0; i < 25; i++)
        {
            ids.Add((await fx.Events.AppendAsync(StorageFixture.Event(T0.AddSeconds(i), "u", UpsEventType.Online,
                                                                      $"Event {i}"))).Id);
        }

        var seen = new List<long>();
        var hasMore = new List<bool>();
        long? before = null;
        do
        {
            EventPage page = await fx.Events.QueryAsync(new EventQuery(BeforeId: before, Limit: 10));
            seen.AddRange(page.Items.Select(e => e.Id));
            hasMore.Add(page.HasMore);
            before = page.Items[^1].Id;
        }
        while (hasMore[^1]);

        Assert.Equal(Enumerable.Reverse(ids), seen);
        Assert.Equal([true, true, false], hasMore);
    }

    [Fact]
    public async Task SearchIsCaseInsensitiveAndMatchesWildcardCharactersLiterally()
    {
        await using var fx = new StorageFixture();
        string[] messages = ["Load at 50% now", "Load at 500 now", "value a_b", "value axb", @"path c:\temp", "LOAD"];
        foreach (string message in messages)
        {
            await fx.Events.AppendAsync(StorageFixture.Event(T0, "u", UpsEventType.Overload, message));
        }

        async Task<string[]> Search(string text) =>
            (await fx.Events.QueryAsync(new EventQuery(Search: text))).Items.Select(e => e.Message).Reverse().ToArray();

        Assert.Equal(["Load at 50% now"], await Search("50%"));
        Assert.Equal(["value a_b"], await Search("a_b"));
        Assert.Equal([@"path c:\temp"], await Search(@"c:\t"));
        Assert.Equal(["Load at 50% now", "Load at 500 now", "LOAD"], await Search("load"));
        Assert.Empty(await Search("missing"));
    }

    [Fact]
    public async Task TheLimitIsClampedBetweenOneAndOneThousand()
    {
        await using var fx = new StorageFixture();
        await InsertDirectlyAsync(fx, 1005, i => T0.AddSeconds(i));

        EventPage one = await fx.Events.QueryAsync(new EventQuery(Limit: 0));
        EventPage max = await fx.Events.QueryAsync(new EventQuery(Limit: 5000));

        Assert.Single(one.Items);
        Assert.True(one.HasMore);
        Assert.Equal(1000, max.Items.Count);
        Assert.True(max.HasMore);
    }

    [Fact]
    public async Task DeleteOlderThanRemovesOnlyOldEventsInBatches()
    {
        await using var fx = new StorageFixture();
        await InsertDirectlyAsync(fx, 12_000, i => T0.AddDays(-400).AddSeconds(i));
        await InsertDirectlyAsync(fx, 10, i => T0.AddSeconds(i));

        int deleted = await fx.Events.DeleteOlderThanAsync(T0.AddDays(-365), CancellationToken.None);

        Assert.Equal(12_000, deleted);
        EventPage rest = await fx.Events.QueryAsync(new EventQuery(Limit: 1000));
        Assert.Equal(10, rest.Items.Count);
        Assert.All(rest.Items, e => Assert.True(e.Timestamp >= T0));
    }

    [Fact]
    public async Task AppendsWhileQueryingNeitherBlockNorLoseEvents()
    {
        await using var fx = new StorageFixture();
        SqliteEventStore store = fx.Events;
        using var stop = new CancellationTokenSource();
        int queries = 0;

        Task[] readers = Enumerable.Range(0, 4).Select(r => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                EventPage page = await store.QueryAsync(new EventQuery(Ups: r % 2 == 0 ? "a" : null, Limit: 50));
                Assert.True(page.Items.Count <= 50);
                Interlocked.Increment(ref queries);
            }
        })).ToArray();

        long[][] ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            var mine = new long[100];
            for (int i = 0; i < mine.Length; i++)
            {
                mine[i] = (await store.AppendAsync(StorageFixture.Event(T0, w % 2 == 0 ? "a" : "b",
                                                                        UpsEventType.Online, $"{w}/{i}"))).Id;
            }

            return mine;
        })));
        stop.Cancel();
        await Task.WhenAll(readers);

        long[] all = ids.SelectMany(x => x).ToArray();
        Assert.Equal(800, all.Distinct().Count());
        Assert.True(queries > 0);
        long count = await fx.ReadAsync(c => (long)StorageFixture.Scalar(c, "SELECT count(*) FROM events;")!);
        Assert.Equal(800, count);
    }

    private static async Task AppendAsync(StorageFixture fx,
                                          params (DateTimeOffset At, string? Ups, UpsEventType Type)[] events)
    {
        foreach ((DateTimeOffset at, string? ups, UpsEventType type) in events)
        {
            await fx.Events.AppendAsync(StorageFixture.Event(at, ups, type, type.ToString()));
        }
    }

    private static Task InsertDirectlyAsync(StorageFixture fx, int count, Func<int, DateTimeOffset> timestamp) =>
        fx.Database.WriteAsync((session, _) => session.InTransaction(() =>
        {
            SqliteCommand insert = session.Command(
                "INSERT INTO events (ts, ups, type, severity, category, message) " +
                "VALUES (@ts, 'u', 'Online', 1, 'Power', 'bulk');", "@ts");
            for (int i = 0; i < count; i++)
            {
                insert.Parameters["@ts"].Value = timestamp(i).ToUnixTimeMilliseconds();
                insert.ExecuteNonQuery();
            }

            return count;
        }));
}
