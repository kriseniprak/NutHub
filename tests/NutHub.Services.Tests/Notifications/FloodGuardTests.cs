using Microsoft.Extensions.Time.Testing;
using NutHub.Core.Model;
using NutHub.Services.Notifications;

namespace NutHub.Services.Tests.Notifications;

public sealed class FloodGuardTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero));
    private readonly List<(string Channel, UpsEvent Summary)> _summaries = [];

    [Fact]
    public void Flapping_power_is_limited_and_summarised_later()
    {
        using var guard = new FloodGuard(_time, (c, e) => _summaries.Add((c, e)), limit: 20, window: TimeSpan.FromMinutes(10));
        int delivered = 0;
        for (int i = 0; i < 50; i++)
        {
            UpsEventType type = i % 2 == 0 ? UpsEventType.OnBattery : UpsEventType.Online;
            delivered += guard.Admit("webhook:a", Event(type)).Count;
            _time.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(20, delivered);
        Assert.Empty(_summaries);

        // The first delivery leaves the window 10 minutes after it was sent: the summary goes out then.
        _time.Advance(TimeSpan.FromMinutes(6));
        (string channel, UpsEvent summary) = Assert.Single(_summaries);
        Assert.Equal("webhook:a", channel);
        Assert.Contains("held back 30 notification(s)", summary.Message);
        Assert.Contains("onBattery x15", summary.Message);
        Assert.Equal("rack1", summary.Ups);
        Assert.Equal("30", summary.Data!["suppressed"]);
    }

    [Fact]
    public void Critical_power_events_are_never_held_back()
    {
        using var guard = new FloodGuard(_time, (c, e) => _summaries.Add((c, e)), limit: 3);
        for (int i = 0; i < 3; i++)
        {
            guard.Admit("email", Event(UpsEventType.OnBattery));
        }

        Assert.Empty(guard.Admit("email", Event(UpsEventType.OnBattery)));
        foreach (UpsEventType type in new[] { UpsEventType.LowBattery, UpsEventType.ForcedShutdown,
                     UpsEventType.ShutdownPending, UpsEventType.ShutdownStarted, UpsEventType.ShutdownCancelled })
        {
            Assert.Single(guard.Admit("email", Event(type)));
        }
    }

    [Fact]
    public void Channels_are_limited_independently()
    {
        using var guard = new FloodGuard(_time, (c, e) => _summaries.Add((c, e)), limit: 2);
        guard.Admit("a", Event(UpsEventType.OnBattery));
        guard.Admit("a", Event(UpsEventType.Online));

        Assert.Empty(guard.Admit("a", Event(UpsEventType.OnBattery)));
        Assert.Single(guard.Admit("b", Event(UpsEventType.OnBattery)));
    }

    [Fact]
    public void The_summary_is_sent_once_and_later_events_go_out_alone()
    {
        using var guard = new FloodGuard(_time, (c, e) => _summaries.Add((c, e)), limit: 3, window: TimeSpan.FromMinutes(1));
        for (int i = 0; i < 5; i++)
        {
            guard.Admit("a", Event(UpsEventType.OnBattery));
        }

        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Single(_summaries);
        IReadOnlyList<UpsEvent> next = guard.Admit("a", Event(UpsEventType.Online));
        Assert.Single(next);
        Assert.Equal(UpsEventType.Online, next[0].Type);
        Assert.Single(_summaries);
    }

    [Fact]
    public void Held_events_are_summarised_ahead_of_an_event_admitted_before_the_timer()
    {
        var sent = new List<UpsEvent>();
        using var guard = new FloodGuard(_time, (c, e) => _summaries.Add((c, e)), limit: 3, window: TimeSpan.FromMinutes(1));
        sent.AddRange(guard.Admit("a", Event(UpsEventType.OnBattery)));
        _time.Advance(TimeSpan.FromSeconds(30));
        sent.AddRange(guard.Admit("a", Event(UpsEventType.Online)));
        sent.AddRange(guard.Admit("a", Event(UpsEventType.OnBattery)));
        Assert.Empty(guard.Admit("a", Event(UpsEventType.Online))); // held; the summary timer is due at t=60 s

        // An exempt event arrives while the window is full: delivered alone. It takes a slot too, so the summary
        // waits until the deliveries of t=30 s leave the window (t=90 s).
        Assert.Single(guard.Admit("a", Event(UpsEventType.LowBattery)));
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Empty(_summaries);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Single(_summaries);
        Assert.Contains("held back 1 notification(s)", _summaries[0].Summary.Message);
    }

    private UpsEvent Event(UpsEventType type) =>
        UpsEvent.Create(type, _time.GetUtcNow(), "rack1", $"rack1: {type}");
}
