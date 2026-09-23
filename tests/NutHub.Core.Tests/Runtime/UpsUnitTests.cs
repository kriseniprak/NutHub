using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Runtime;

namespace NutHub.Core.Tests.Runtime;

public sealed class UpsUnitSnapshotTests
{
    [Fact]
    public void Before_any_data_the_snapshot_is_empty_and_not_available()
    {
        using var h = new UpsUnitHarness();
        Assert.Empty(h.Snapshot.Variables);
        Assert.Equal(DriverState.Starting, h.Snapshot.DriverState);
        Assert.Equal(DataAvailability.DriverNotConnected, h.Snapshot.Availability);
        Assert.Null(h.Snapshot.LastUpdate);

        var disabled = new UpsUnitHarness(new UpsConfig { Name = "off", Driver = "fake", Enabled = false });
        Assert.Equal(DriverState.Disabled, disabled.Snapshot.DriverState);
    }

    [Fact]
    public void Device_and_driver_variables_are_added_and_driver_values_sanitised()
    {
        using var h = new UpsUnitHarness(new UpsConfig
        {
            Name = "ups1",
            Driver = "fake",
            PollIntervalSeconds = 2.5,
            Options = { ["Port"] = "COM3", ["community"] = "enc:v1:AAAA", ["bad key"] = "x", ["empty"] = " " },
        });

        h.Connect("OL", new()
        {
            ["ups.mfr"] = "APC",
            ["device.model"] = "Back-UPS 700",
            ["driver.name"] = "impostor",
            ["driver.version.internal"] = "0.42",
            ["bad name"] = "dropped",
            ["ups.id"] = "line1\r\n  ",
        });

        UpsSnapshot s = h.Snapshot;
        Assert.Equal("APC", s.Get("device.mfr"));
        Assert.Equal("Back-UPS 700", s.Get("ups.model"));
        Assert.Equal("ups", s.Get("device.type"));
        Assert.Equal("fake", s.Get("driver.name"));
        Assert.Equal(NutHubInfo.Version, s.Get("driver.version"));
        Assert.Equal("0.42", s.Get("driver.version.internal"));
        Assert.Equal("2.5", s.Get("driver.parameter.pollinterval"));
        Assert.Equal("COM3", s.Get("driver.parameter.port"));
        Assert.Null(s.Get("driver.parameter.community")); // secrets are never shown
        Assert.Null(s.Get("driver.parameter.empty"));
        Assert.DoesNotContain(s.Variables.Keys, k => k.Contains(' ', StringComparison.Ordinal));
        Assert.Equal("line1", s.Get("ups.id"));
        Assert.Equal(DriverState.Connected, s.DriverState);
        Assert.True(s.IsAvailable);
    }

    [Fact]
    public void Overrides_replace_driver_values_and_make_them_read_only()
    {
        using var h = new UpsUnitHarness(new UpsConfig
        {
            Name = "ups1",
            Driver = "fake",
            Overrides = { ["battery.charge"] = "55", ["ups.delay.shutdown"] = "30", ["ups.location"] = "Rack 2" },
        });

        h.Unit.BeginRun(clearData: true);
        h.Publish("OL", new() { ["battery.charge"] = "100", ["ups.delay.shutdown"] = "20" },
                  new() { ["ups.delay.shutdown"] = VariableInfo.WritableRange(0, 600) });

        Assert.Equal("55", h.Snapshot.Get("battery.charge"));
        Assert.Equal("30", h.Snapshot.Get("ups.delay.shutdown"));
        Assert.Equal("Rack 2", h.Snapshot.Get("ups.location"));
        Assert.False(h.Snapshot.GetInfo("ups.delay.shutdown").Writable);
        Assert.Equal(CommandStatus.ReadOnly, UpsUnit.CheckWritable(h.Snapshot, "ups.delay.shutdown", "10")!.Status);
    }

    [Fact]
    public void Identical_publishes_keep_the_sequence_and_changes_advance_it()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL", new() { ["battery.charge"] = "100" });
        long sequence = h.Snapshot.Sequence;
        int changes = h.Recorder.Messages.OfType<SnapshotChangedMessage>().Count();

        h.Time.Advance(TimeSpan.FromSeconds(2));
        h.Publish("OL", new() { ["battery.charge"] = "100" });
        Assert.Equal(sequence, h.Snapshot.Sequence);
        Assert.Equal(changes, h.Recorder.Messages.OfType<SnapshotChangedMessage>().Count());
        Assert.Equal(h.Time.GetUtcNow(), h.Snapshot.LastUpdate);

        h.Publish("OL", new() { ["battery.charge"] = "99" });
        Assert.Equal(sequence + 1, h.Snapshot.Sequence);
        SnapshotChangedMessage last = h.Recorder.Messages.OfType<SnapshotChangedMessage>().Last();
        Assert.Equal("100", last.Previous!.Get("battery.charge"));
        Assert.Equal("99", last.Current.Get("battery.charge"));
    }

    [Fact]
    public void Disabling_clears_the_data_without_a_communication_event()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL");
        h.Recorder.Clear();

        h.Unit.UpdateConfig(new UpsConfig { Name = "ups1", Driver = "fake", Enabled = false });

        Assert.Equal(DriverState.Disabled, h.Snapshot.DriverState);
        Assert.Empty(h.Snapshot.Variables);
        Assert.Equal(DataAvailability.DriverNotConnected, h.Snapshot.Availability);
        Assert.Empty(h.Events);
    }

    [Fact]
    public void Description_changes_apply_in_place()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL");
        h.Unit.UpdateConfig(new UpsConfig { Name = "ups1", Driver = "fake", Description = "Server room" });
        Assert.Equal("Server room", h.Snapshot.Description);
        Assert.True(h.Snapshot.IsAvailable);
    }
}

public sealed class UpsUnitLowBatteryTests
{
    private static UpsConfig Config(double? charge = null, int? runtime = null, bool ignoreDevice = false) => new()
    {
        Name = "ups1",
        Driver = "fake",
        LowBattery = new LowBatteryPolicy { ChargePercent = charge, RuntimeSeconds = runtime, IgnoreDeviceFlag = ignoreDevice },
    };

    [Fact]
    public void The_charge_threshold_adds_LB_only_on_battery()
    {
        using var h = new UpsUnitHarness(Config(charge: 30));
        h.Connect("OL CHRG", new() { ["battery.charge"] = "20" });
        Assert.False(h.Snapshot.Has(UpsStatusFlags.LowBattery));

        h.Publish("OB DISCHRG", new() { ["battery.charge"] = "31" });
        Assert.Equal("OB DISCHRG", h.Snapshot.StatusText);

        h.Publish("OB DISCHRG", new() { ["battery.charge"] = "30" });
        Assert.Equal("OB DISCHRG LB", h.Snapshot.StatusText);
        Assert.True(h.Snapshot.Has(UpsStatusFlags.LowBattery));
        Assert.Contains(UpsEventType.LowBattery, h.Events);
    }

    [Fact]
    public void The_runtime_threshold_adds_LB()
    {
        using var h = new UpsUnitHarness(Config(runtime: 300));
        h.Connect("OB DISCHRG", new() { ["battery.runtime"] = "301" });
        Assert.False(h.Snapshot.Has(UpsStatusFlags.LowBattery));
        h.Publish("OB DISCHRG", new() { ["battery.runtime"] = "299" });
        Assert.True(h.Snapshot.Has(UpsStatusFlags.LowBattery));
    }

    [Fact]
    public void Missing_or_unparsable_values_do_not_trigger_the_thresholds()
    {
        using var h = new UpsUnitHarness(Config(charge: 30, runtime: 300));
        h.Connect("OB DISCHRG", new() { ["battery.charge"] = "n/a" });
        Assert.Equal("OB DISCHRG", h.Snapshot.StatusText);
    }

    [Fact]
    public void The_device_flag_can_be_ignored()
    {
        using var h = new UpsUnitHarness(Config(ignoreDevice: true));
        h.Connect("OB LB DISCHRG", new() { ["battery.charge"] = "90" });
        Assert.Equal("OB DISCHRG", h.Snapshot.StatusText);
        Assert.Equal([UpsEventType.OnBattery], h.Events);
    }

    [Fact]
    public void Thresholds_still_apply_when_the_device_flag_is_ignored()
    {
        using var h = new UpsUnitHarness(Config(charge: 30, ignoreDevice: true));
        h.Connect("OB LB DISCHRG", new() { ["battery.charge"] = "25" });
        Assert.Equal("OB LB DISCHRG", h.Snapshot.StatusText); // the device's token stays where it was
        Assert.True(h.Snapshot.Has(UpsStatusFlags.LowBattery));
    }

    [Fact]
    public void Without_thresholds_the_device_flag_is_kept()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OB LB", new() { ["battery.charge"] = "90" });
        Assert.Equal("OB LB", h.Snapshot.StatusText);
    }
}

public sealed class UpsUnitForcedShutdownTests
{
    private static readonly CommandOrigin Operator = CommandOrigin.Nut("upsmon", "10.0.0.9");

    [Fact]
    public void FSD_leads_the_status_and_is_reported_once_with_its_origin()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OB DISCHRG");
        h.Recorder.Clear();

        h.Unit.SetForcedShutdown(Operator);
        h.Unit.SetForcedShutdown(Operator);

        Assert.Equal("FSD OB DISCHRG", h.Snapshot.StatusText);
        Assert.True(h.Snapshot.ForcedShutdown);
        Assert.True(h.Snapshot.Has(UpsStatusFlags.ForcedShutdown));
        UpsEvent e = Assert.Single(h.Recorder.Events);
        Assert.Equal(UpsEventType.ForcedShutdown, e.Type);
        Assert.Equal("nut:upsmon@10.0.0.9", e.Actor);
    }

    [Fact]
    public void FSD_can_be_set_before_any_data()
    {
        using var h = new UpsUnitHarness();
        h.Unit.SetForcedShutdown(Operator);
        Assert.Equal("FSD", h.Snapshot.StatusText);
    }

    [Fact]
    public void Clearing_reports_whether_it_was_set()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL");
        Assert.False(h.Unit.ClearForcedShutdown(Operator));
        h.Unit.SetForcedShutdown(Operator);
        h.Recorder.Clear();

        Assert.True(h.Unit.ClearForcedShutdown(Operator));

        Assert.Equal("OL", h.Snapshot.StatusText);
        Assert.False(h.Snapshot.ForcedShutdown);
        Assert.Equal([UpsEventType.ForcedShutdownCleared], h.Events);
    }

    [Fact]
    public void FSD_clears_itself_after_the_delay_on_line_power()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL");
        h.Unit.SetForcedShutdown(Operator);
        h.Unit.Tick(); // the time on line power starts counting
        h.Recorder.Clear();

        for (int i = 0; i < 5; i++)
        {
            h.Publish("OL");
            h.Advance(10);
        }

        h.Publish("OL");
        h.Advance(9);
        Assert.True(h.Snapshot.ForcedShutdown);

        h.Publish("OL");
        h.Advance(1);
        Assert.False(h.Snapshot.ForcedShutdown);
        UpsEvent cleared = Assert.Single(h.Recorder.Events);
        Assert.Equal(UpsEventType.ForcedShutdownCleared, cleared.Type);
        Assert.Equal("system", cleared.Actor);
    }

    [Fact]
    public void Going_back_on_battery_restarts_the_delay()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL");
        h.Unit.SetForcedShutdown(Operator);
        h.Unit.Tick();
        h.Publish("OL");
        h.Advance(10);
        h.Publish("OL");
        h.Advance(10);

        h.Publish("OB DISCHRG");
        h.Advance(10);
        h.Publish("OL");
        h.Advance(10); // counting again from here
        for (int i = 0; i < 5; i++)
        {
            h.Publish("OL");
            h.Advance(10);
        }

        Assert.True(h.Snapshot.ForcedShutdown);
        h.Publish("OL");
        h.Advance(10);
        Assert.False(h.Snapshot.ForcedShutdown);
    }

    [Fact]
    public void A_delay_of_zero_keeps_the_upsd_latch()
    {
        using var h = new UpsUnitHarness();
        h.Server.FsdClearDelaySeconds = 0;
        h.Connect("OL");
        h.Unit.SetForcedShutdown(Operator);
        for (int i = 0; i < 20; i++)
        {
            h.Publish("OL");
            h.Advance(10);
        }

        Assert.True(h.Snapshot.ForcedShutdown);
    }

    [Fact]
    public void FSD_is_not_cleared_while_the_data_is_stale()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL");
        h.Unit.SetForcedShutdown(Operator);
        for (int i = 0; i < 20; i++)
        {
            h.Advance(10);
        }

        Assert.False(h.Snapshot.IsAvailable);
        Assert.True(h.Snapshot.ForcedShutdown);
    }
}

public sealed class UpsUnitAvailabilityTests
{
    // The housekeeping timer and the driver change the unit from two threads. Their messages must leave in the order
    // the changes were made: a "communication restored" published before the "lost" it follows would leave the event
    // log and the notifications saying the opposite of the truth.
    [Fact]
    public async Task Changes_made_by_two_threads_are_published_in_the_order_they_were_made()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL");
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using IDisposable slowSubscriber = h.Hub.Subscribe(m =>
        {
            if (m is UpsEventMessage { Event.Type: UpsEventType.CommunicationLost })
            {
                held.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });
        using var ordered = new Support.HubRecorder(h.Hub);

        h.Time.Advance(TimeSpan.FromSeconds(16));
        Task tick = Task.Run(h.Unit.Tick);
        Assert.True(held.Wait(TimeSpan.FromSeconds(30)));
        Task publish = Task.Run(() => h.Publish("OL"));
        await Task.Delay(200);
        release.Set();
        await Task.WhenAll(tick, publish).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([UpsEventType.CommunicationLost, UpsEventType.CommunicationRestored], ordered.EventTypes);
        Assert.Equal(h.Snapshot.Sequence, ordered.Messages.OfType<SnapshotChangedMessage>().Last().Current.Sequence);
    }

    [Fact]
    public void Data_turns_stale_after_MaxAge_and_comes_back()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL");
        h.Recorder.Clear();

        h.Advance(15);
        Assert.Equal(DataAvailability.Available, h.Snapshot.Availability);

        h.Advance(1);
        Assert.Equal(DataAvailability.Stale, h.Snapshot.Availability);
        Assert.Equal(DriverState.Connected, h.Snapshot.DriverState);
        UpsEvent lost = Assert.Single(h.Recorder.Events);
        Assert.Equal(UpsEventType.CommunicationLost, lost.Type);
        Assert.Contains("no data for more than 15 s", lost.Message, StringComparison.Ordinal);

        h.Advance(30);
        Assert.Single(h.Recorder.Events); // reported once

        h.Publish("OL");
        Assert.True(h.Snapshot.IsAvailable);
        Assert.Equal([UpsEventType.CommunicationLost, UpsEventType.CommunicationRestored], h.Events);
    }

    // A driver publishes once per poll. With a poll interval longer than MAXAGE, healthy data would turn stale between
    // two polls: a communication loss every poll in the event log, and DATA-STALE for NUT clients (a UPS that upsmon
    // declares dead when it is on battery).
    [Fact]
    public void A_poll_interval_longer_than_MaxAge_does_not_make_the_data_stale_between_polls()
    {
        using var h = new UpsUnitHarness(new UpsConfig { Name = "ups1", Driver = "fake", PollIntervalSeconds = 30 });
        h.Connect("OL");
        h.Recorder.Clear();
        for (int poll = 0; poll < 5; poll++)
        {
            for (int second = 0; second < 30; second++)
            {
                h.Advance(1);
                Assert.True(h.Snapshot.IsAvailable, $"stale {second + 1} s after poll {poll}");
            }

            h.Publish("OL");
        }

        Assert.Empty(h.Events);

        // A driver that stops publishing is still noticed, once two polls are missing.
        h.Advance(60);
        Assert.True(h.Snapshot.IsAvailable);
        h.Advance(1);
        Assert.Equal(DataAvailability.Stale, h.Snapshot.Availability);
        Assert.Contains("no data for more than 60 s", Assert.Single(h.Recorder.Events).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxAge_follows_the_server_settings()
    {
        using var h = new UpsUnitHarness();
        h.Server.MaxAgeSeconds = 5;
        h.Connect("OL");
        h.Advance(6);
        Assert.Equal(DataAvailability.Stale, h.Snapshot.Availability);
    }

    [Fact]
    public void A_disconnection_reported_by_the_driver_is_a_communication_loss()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL");
        h.Recorder.Clear();

        h.Unit.OnDisconnected("USB cable unplugged");
        Assert.Equal(DataAvailability.Stale, h.Snapshot.Availability);
        Assert.Equal("USB cable unplugged", h.Snapshot.DriverMessage);
        Assert.Contains("USB cable unplugged", Assert.Single(h.Recorder.Events).Message, StringComparison.Ordinal);

        h.Unit.OnConnecting("Opening the device");
        Assert.Equal(DriverState.Connecting, h.Snapshot.DriverState);
        Assert.Single(h.Recorder.Events);

        h.Publish("OL");
        Assert.Equal([UpsEventType.CommunicationLost, UpsEventType.CommunicationRestored], h.Events);
        Assert.Null(h.Snapshot.DriverMessage);
    }

    [Fact]
    public void A_failed_driver_reports_both_the_loss_and_the_failure()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL");
        h.Recorder.Clear();

        h.Unit.SetDriverState(DriverState.Failed, "boom");

        Assert.Equal(DataAvailability.DriverNotConnected, h.Snapshot.Availability);
        Assert.Equal([UpsEventType.CommunicationLost, UpsEventType.DriverFailed], h.Events);
        Assert.Contains("boom", h.Recorder.Events[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCommunication_is_reported_once_60_seconds_after_a_start_without_data()
    {
        using var h = new UpsUnitHarness();
        h.Unit.BeginRun(clearData: true);
        h.Unit.OnConnecting("Looking for the device");

        h.Advance(59);
        Assert.Empty(h.Events);
        h.Advance(1);
        UpsEvent e = Assert.Single(h.Recorder.Events);
        Assert.Equal(UpsEventType.NoCommunication, e.Type);
        Assert.Contains("Looking for the device", e.Message, StringComparison.Ordinal);
        h.Advance(120);
        Assert.Single(h.Recorder.Events);

        h.Publish("OL");
        Assert.Equal([UpsEventType.NoCommunication, UpsEventType.CommunicationRestored], h.Events);
    }

    [Fact]
    public void NoCommunication_is_not_reported_for_a_disabled_ups()
    {
        using var h = new UpsUnitHarness(new UpsConfig { Name = "ups1", Driver = "fake", Enabled = false });
        h.Advance(120);
        Assert.Empty(h.Events);
    }

    [Fact]
    public void A_planned_restart_raises_no_communication_events()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL");
        h.Advance(1);
        h.Recorder.Clear();

        h.Unit.BeginRun(clearData: true);
        Assert.Equal(DriverState.Starting, h.Snapshot.DriverState);
        h.Advance(5);
        h.Publish("OL");

        Assert.True(h.Snapshot.IsAvailable);
        Assert.Empty(h.Events);
    }
}

public sealed class UpsUnitStatusEventTests
{
    [Theory]
    [InlineData("OL", new UpsEventType[0])]
    [InlineData("OL CHRG TRIM", new UpsEventType[0])]
    [InlineData("OB DISCHRG", new[] { UpsEventType.OnBattery })]
    [InlineData("OB DISCHRG LB", new[] { UpsEventType.OnBattery, UpsEventType.LowBattery })]
    [InlineData("OL RB", new[] { UpsEventType.ReplaceBattery })]
    [InlineData("OL BYPASS OVER", new[] { UpsEventType.Overload, UpsEventType.Bypass })]
    public void The_first_data_reports_adverse_conditions_only(string status, UpsEventType[] expected)
    {
        using var h = new UpsUnitHarness();
        h.Connect(status);
        Assert.Equal(expected, h.Events);
    }

    [Fact]
    public void Power_transitions_are_reported_in_order()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL", new() { ["battery.charge"] = "100", ["battery.runtime"] = "1500" });

        h.Publish("OB DISCHRG", new() { ["battery.charge"] = "90", ["battery.runtime"] = "1200" });
        h.Publish("OB DISCHRG LB", new() { ["battery.charge"] = "20" });
        h.Publish("OL CHRG", new() { ["battery.charge"] = "21" });

        Assert.Equal([UpsEventType.OnBattery, UpsEventType.LowBattery, UpsEventType.Online, UpsEventType.LowBatteryCleared],
                     h.Events);
        UpsEvent onBattery = h.Recorder.Events[0];
        Assert.Equal("ups1 is on battery (battery 90%, runtime 20 min).", onBattery.Message);
        Assert.Equal("OB DISCHRG", onBattery.Data!["ups.status"]);
        Assert.Equal(EventSeverity.Warning, onBattery.Severity);
    }

    [Fact]
    public void Leaving_battery_without_line_power_is_not_Online()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OB DISCHRG");
        h.Recorder.Clear();
        h.Publish("OFF");
        Assert.Equal([UpsEventType.Off], h.Events);
    }

    [Fact]
    public void Alarms_carry_the_alarm_text()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL");
        h.Publish("OL ALARM", new() { ["ups.alarm"] = "Fan failure" });
        h.Publish("OL");

        Assert.Equal([UpsEventType.Alarm, UpsEventType.AlarmCleared], h.Events);
        Assert.Contains("Fan failure", h.Recorder.Events[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_change_during_a_communication_loss_is_reported_when_data_is_back()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL");
        h.Unit.OnDisconnected("timeout");
        h.Publish("OB DISCHRG");

        Assert.Equal([UpsEventType.CommunicationLost, UpsEventType.CommunicationRestored, UpsEventType.OnBattery],
                     h.Events);
    }

    [Fact]
    public void FSD_in_the_device_status_is_not_reported_as_a_flag_event()
    {
        using var h = new UpsUnitHarness();
        h.Connect("OL");
        h.Publish("FSD OL");
        Assert.Empty(h.Events);
    }

    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(125, "2 min")]
    [InlineData(3725, "1 h 2 min")]
    [InlineData(-5, "0 s")]
    public void Durations_in_messages(double seconds, string expected) =>
        Assert.Equal(expected, UpsUnit.FormatDuration(seconds));
}
