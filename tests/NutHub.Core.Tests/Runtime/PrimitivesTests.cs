using NutHub.Core.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Core.Tests.Support;

namespace NutHub.Core.Tests.Runtime;

public sealed class EventHubTests
{
    private static UpsEventMessage Message(string text) =>
        new(UpsEvent.Create(UpsEventType.Online, TestTime.Start, "ups", text));

    [Fact]
    public void Subscribers_receive_messages_until_they_unsubscribe()
    {
        EventHub hub = TestTime.Hub();
        var received = new List<HubMessage>();
        IDisposable subscription = hub.Subscribe(received.Add);

        hub.Publish(Message("one"));
        subscription.Dispose();
        subscription.Dispose(); // idempotent
        hub.Publish(Message("two"));

        Assert.Single(received);
    }

    // Two subscriptions of the same handler (a method group compares equal to itself) are still two subscriptions.
    [Fact]
    public void Disposing_a_subscription_ends_only_that_subscription()
    {
        EventHub hub = TestTime.Hub();
        var received = new List<HubMessage>();
        Action<HubMessage> handler = received.Add;
        IDisposable first = hub.Subscribe(handler);
        using IDisposable second = hub.Subscribe(handler);

        first.Dispose();
        hub.Publish(Message("one"));

        Assert.Single(received);
    }

    [Fact]
    public void A_failing_subscriber_does_not_stop_the_others()
    {
        EventHub hub = TestTime.Hub();
        int reached = 0;
        using var failing = hub.Subscribe(_ => throw new InvalidOperationException("boom"));
        using var working = hub.Subscribe(_ => reached++);

        hub.Publish(Message("x"));

        Assert.Equal(1, reached);
    }

    [Fact]
    public async Task Channel_subscriptions_filter_and_drop_the_oldest_when_full()
    {
        EventHub hub = TestTime.Hub();
        using ChannelSubscription subscription = hub.SubscribeChannel(2, m => m is UpsEventMessage);

        hub.Publish(new UpsRemovedMessage("ignored"));
        hub.Publish(Message("1"));
        hub.Publish(Message("2"));
        hub.Publish(Message("3"));

        var texts = new List<string>();
        while (subscription.Reader.TryRead(out HubMessage? m))
        {
            texts.Add(((UpsEventMessage)m).Event.Message);
        }

        Assert.Equal(["2", "3"], texts);

        subscription.Dispose();
        hub.Publish(Message("4"));
        Assert.False(await subscription.Reader.WaitToReadAsync());
    }
}

public sealed class DriverOptionReaderTests
{
    private static DriverOptionReader Reader(params (string Key, string Value)[] options) =>
        new(options.ToDictionary(o => o.Key, o => o.Value));

    [Fact]
    public void Keys_ignore_case_and_blank_values_mean_absent()
    {
        DriverOptionReader read = Reader(("Port", " COM3 "), ("empty", "  "));
        Assert.Equal("COM3", read.GetString("port"));
        Assert.Equal("fallback", read.GetString("empty", "fallback"));
        Assert.Null(read.GetString("missing"));
    }

    [Fact]
    public void Required_options_throw_a_configuration_error_naming_the_option()
    {
        var ex = Assert.Throws<DriverConfigurationException>(() => Reader().GetRequiredString("host"));
        Assert.Equal("host", ex.OptionKey);
    }

    [Fact]
    public void Numbers_are_parsed_and_bounded()
    {
        Assert.Equal(42, Reader(("n", "42")).GetInt("n", 1, 0, 100));
        Assert.Equal(7, Reader().GetInt("n", 7));
        Assert.Throws<DriverConfigurationException>(() => Reader(("n", "x")).GetInt("n", 1));
        Assert.Throws<DriverConfigurationException>(() => Reader(("n", "101")).GetInt("n", 1, 0, 100));
        Assert.Equal(1.5, Reader(("d", "1.5")).GetDouble("d", 0));
        Assert.Throws<DriverConfigurationException>(() => Reader(("d", "1,5")).GetDouble("d", 0));
        Assert.Throws<DriverConfigurationException>(() => Reader(("d", "NaN")).GetDouble("d", 0));
        Assert.Throws<DriverConfigurationException>(() => Reader(("p", "70000")).GetPort("p", 161));
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("ON", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("off", false)]
    public void Booleans_accept_the_usual_words(string text, bool expected) =>
        Assert.Equal(expected, Reader(("b", text)).GetBool("b", !expected));

    [Fact]
    public void Invalid_boolean_and_choice_throw()
    {
        Assert.Throws<DriverConfigurationException>(() => Reader(("b", "maybe")).GetBool("b", false));
        Assert.Equal("v2c", Reader(("v", "V2C")).GetChoice("v", "v1", "v1", "v2c", "v3"));
        Assert.Equal("v1", Reader().GetChoice("v", "v1", "v1", "v2c"));
        Assert.Throws<DriverConfigurationException>(() => Reader(("v", "v4")).GetChoice("v", "v1", "v1", "v2c"));
    }

    [Fact]
    public void Hex_values_accept_an_optional_prefix()
    {
        Assert.Equal(0x051d, Reader(("vid", "0x051D")).GetHex("vid"));
        Assert.Equal(0x0463, Reader(("vid", "0463")).GetHex("vid"));
        Assert.Null(Reader().GetHex("vid"));
        Assert.Throws<DriverConfigurationException>(() => Reader(("vid", "xyz")).GetHex("vid"));
    }
}

public sealed class EventModelTests
{
    [Theory]
    [InlineData(UpsEventType.LowBattery, EventCategory.Power, EventSeverity.Critical)]
    [InlineData(UpsEventType.OnBattery, EventCategory.Power, EventSeverity.Warning)]
    [InlineData(UpsEventType.Online, EventCategory.Power, EventSeverity.Notice)]
    [InlineData(UpsEventType.CommunicationLost, EventCategory.Communication, EventSeverity.Warning)]
    [InlineData(UpsEventType.CommandExecuted, EventCategory.Command, EventSeverity.Notice)]
    [InlineData(UpsEventType.ShutdownPending, EventCategory.Shutdown, EventSeverity.Critical)]
    [InlineData(UpsEventType.UserLoginFailed, EventCategory.Audit, EventSeverity.Warning)]
    [InlineData(UpsEventType.ServerStarted, EventCategory.System, EventSeverity.Notice)]
    [InlineData(UpsEventType.TrimEnded, EventCategory.Device, EventSeverity.Info)]
    public void Create_fills_category_and_default_severity(UpsEventType type, EventCategory category, EventSeverity severity)
    {
        UpsEvent e = UpsEvent.Create(type, TestTime.Start, "ups", "text");
        Assert.Equal(category, e.Category);
        Assert.Equal(severity, e.Severity);
        Assert.Equal(EventSeverity.Info, UpsEvent.Create(type, TestTime.Start, null, "t", severity: EventSeverity.Info).Severity);
    }

    [Fact]
    public void Every_event_type_has_a_category_and_severity()
    {
        foreach (UpsEventType type in Enum.GetValues<UpsEventType>())
        {
            Assert.True(Enum.IsDefined(UpsEventCatalog.CategoryOf(type)));
            Assert.True(Enum.IsDefined(UpsEventCatalog.DefaultSeverity(type)));
        }
    }

    [Theory]
    [InlineData("system", null, null, "system")]
    [InlineData("web", "alice", "10.0.0.2", "web:alice@10.0.0.2")]
    [InlineData("nut", null, "10.0.0.3", "nut:anonymous@10.0.0.3")]
    [InlineData("cli", "root", null, "cli:root")]
    public void CommandOrigin_describes_who_acted(string source, string? user, string? address, string expected) =>
        Assert.Equal(expected, new CommandOrigin(source, user, address).ToString());

    [Fact]
    public void CommandResult_helpers()
    {
        Assert.True(CommandResult.Ok.IsSuccess);
        Assert.Equal(CommandStatus.DriverNotConnected, CommandResult.NotConnected().Status);
        Assert.Equal("Failed: nope", CommandResult.Fail("nope").ToString());
        Assert.Equal("NotSupported", CommandResult.NotSupported().ToString());
    }
}

public sealed class InMemoryEventStoreTests
{
    [Fact]
    public async Task Events_get_increasing_ids_and_are_returned_newest_first_with_filters_and_paging()
    {
        var store = new InMemoryEventStore();
        for (int i = 0; i < 5; i++)
        {
            await store.AppendAsync(UpsEvent.Create(i % 2 == 0 ? UpsEventType.OnBattery : UpsEventType.Online,
                                                    TestTime.Start.AddMinutes(i), i < 3 ? "a" : "b", $"event {i}"));
        }

        EventPage all = await store.QueryAsync(new EventQuery(Limit: 2));
        Assert.Equal([5L, 4L], all.Items.Select(e => e.Id));
        Assert.True(all.HasMore);

        EventPage next = await store.QueryAsync(new EventQuery(BeforeId: 4, Limit: 10));
        Assert.Equal([3L, 2L, 1L], next.Items.Select(e => e.Id));
        Assert.False(next.HasMore);

        Assert.Equal(3, (await store.QueryAsync(new EventQuery(Ups: "A"))).Items.Count);
        Assert.Equal(3, (await store.QueryAsync(new EventQuery(MinSeverity: EventSeverity.Warning))).Items.Count);
        Assert.Single((await store.QueryAsync(new EventQuery(Search: "EVENT 4"))).Items);
        Assert.Equal(2, (await store.QueryAsync(new EventQuery(From: TestTime.Start.AddMinutes(1),
                                                               To: TestTime.Start.AddMinutes(3)))).Items.Count);
    }
}
