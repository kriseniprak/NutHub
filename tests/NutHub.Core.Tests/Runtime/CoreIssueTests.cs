using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Core.Tests.Support;

namespace NutHub.Core.Tests.Runtime;

/// <summary>Behaviour at the edges: restarts after failures, recording at startup, values from the configuration.</summary>
public sealed class CoreIssueTests
{
    // The driver manager retries a failing driver after 2, 5, 10, 30 s... calling BeginRun(clearData: false) each
    // time. "No communication since the driver started" should count from the first start of the run, not from the
    // last retry, or a driver that keeps failing is never reported.
    [Fact]
    public void NoCommunication_counts_from_the_first_start_across_retries()
    {
        using var h = new UpsUnitHarness();
        h.Unit.BeginRun(clearData: true);
        double elapsed = 0;
        foreach (double backoff in new[] { 2.0, 5, 10, 30 })
        {
            h.Unit.SetDriverState(DriverState.Failed, "The port COM9 does not exist.");
            for (int i = 0; i < backoff; i++)
            {
                h.Advance(1);
                elapsed++;
            }

            h.Unit.BeginRun(clearData: false);
        }

        h.Unit.SetDriverState(DriverState.Failed, "The port COM9 does not exist.");
        while (elapsed < 61)
        {
            h.Advance(1);
            elapsed++;
        }

        Assert.Contains(UpsEventType.NoCommunication, h.Events);
    }

    [Fact]
    public void A_retry_after_a_failure_reports_the_restored_communication()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL");
        h.Unit.SetDriverState(DriverState.Failed, "USB error");
        h.Advance(2);
        h.Unit.BeginRun(clearData: false);
        h.Publish("OL");

        Assert.Equal([UpsEventType.CommunicationLost, UpsEventType.DriverFailed, UpsEventType.CommunicationRestored],
                     h.Events);
    }

    // Restarting the driver or changing its settings is often how an operator fixes a lost communication: once a loss
    // was reported, its end must be reported too, or the event log and the notifications keep saying it is lost.
    [Fact]
    public void A_planned_restart_after_a_reported_loss_reports_the_restored_communication()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL");
        h.Unit.OnDisconnected("cable");
        h.Unit.BeginRun(clearData: true);
        h.Publish("OL");

        Assert.Equal([UpsEventType.CommunicationLost, UpsEventType.CommunicationRestored], h.Events);
    }

    [Fact]
    public void A_planned_restart_after_no_communication_reports_the_restored_communication()
    {
        using var h = new UpsUnitHarness();
        h.Unit.BeginRun(clearData: true);
        h.Unit.SetDriverState(DriverState.Failed, "The port COM9 does not exist.");
        for (int i = 0; i < 61; i++)
        {
            h.Advance(1);
        }

        h.Unit.BeginRun(clearData: true);
        h.Publish("OL");

        Assert.Equal([UpsEventType.DriverFailed, UpsEventType.NoCommunication, UpsEventType.CommunicationRestored],
                     h.Events);
    }

    // NUT values are single-line: the unit strips control characters from driver values, but values coming from the
    // configuration (overrides, driver options shown as driver.parameter.*) reach clients as they are.
    [Fact]
    public void Values_from_the_configuration_are_single_line()
    {
        using var h = new UpsUnitHarness(new UpsConfig
        {
            Name = "ups1",
            Driver = "fake",
            Overrides = { ["ups.location"] = "Rack 1\nVAR ups1 ups.status \"OB LB\"" },
            Options = { ["port"] = "COM1\r\n" },
        });
        h.Connect("OL");

        Assert.All(h.Snapshot.Variables.Values, v => Assert.DoesNotContain(v, char.IsControl));
    }

    // With .NET 10, BackgroundService runs all of ExecuteAsync on a background task, so the recorder subscribes a
    // moment after StartAsync returns: events published by services that start right after it can be lost.
    [Fact]
    public async Task Events_published_right_after_the_recorder_starts_are_stored()
    {
        EventHub hub = TestTime.Hub();
        var store = new InMemoryEventStore();
        using var recorder = new EventRecorderService(hub, store, NullLogger<EventRecorderService>.Instance);

        await recorder.StartAsync(CancellationToken.None);
        hub.Publish(new UpsEventMessage(UpsEvent.Create(UpsEventType.ServerStarted, TestTime.Start, null, "started")));
        await Task.Delay(200);
        await recorder.StopAsync(CancellationToken.None);

        Assert.Single((await store.QueryAsync(new EventQuery())).Items);
    }

    [Fact]
    public async Task The_recorder_stores_events_and_announces_them_with_their_id()
    {
        EventHub hub = TestTime.Hub();
        var store = new InMemoryEventStore();
        using var recorded = new HubRecorder(hub);
        using var recorder = new EventRecorderService(hub, store, NullLogger<EventRecorderService>.Instance);
        await recorder.StartAsync(CancellationToken.None);
        await Task.Delay(100); // let it subscribe (see the test above)

        hub.Publish(new UpsEventMessage(UpsEvent.Create(UpsEventType.OnBattery, TestTime.Start, "ups1", "on battery")));
        await Wait.UntilAsync(() => recorded.Messages.OfType<EventRecordedMessage>().Any(), "the recorded event");
        await recorder.StopAsync(CancellationToken.None);

        EventRecordedMessage message = recorded.Messages.OfType<EventRecordedMessage>().Single();
        Assert.Equal(1, message.Event.Id);
        Assert.Equal("on battery", Assert.Single((await store.QueryAsync(new EventQuery())).Items).Message);
    }
}
