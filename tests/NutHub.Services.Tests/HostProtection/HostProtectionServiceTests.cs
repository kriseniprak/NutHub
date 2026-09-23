using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Services.HostProtection;
using NutHub.Services.Tests.Support;

namespace NutHub.Services.Tests.HostProtection;

public sealed class HostProtectionServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero));
    private readonly JumpingTimeProvider _clock;
    private readonly TestConfigStore _config = new();
    private readonly FakeUpsAccess _ups = new();
    private readonly EventHub _hub = new(NullLogger<EventHub>.Instance);
    private readonly NutSessionRegistry _sessions;
    private readonly RecordingPowerController _power = new();
    private readonly List<UpsEvent> _events = [];
    private int _changes;

    public HostProtectionServiceTests()
    {
        _clock = new JumpingTimeProvider(_time);
        _sessions = new NutSessionRegistry(_hub, _time);
        _hub.Subscribe(m =>
        {
            if (m is UpsEventMessage e)
            {
                lock (_events)
                {
                    _events.Add(e.Event);
                }
            }
            else if (m is HostProtectionChangedMessage)
            {
                Interlocked.Increment(ref _changes);
            }
        });
    }

    [Fact]
    public async Task Healthy_ups_on_line_power_keeps_monitoring()
    {
        var service = Create();
        _ups.Set(Snapshots.Create("ups1", "OL CHRG"));

        await Step(service);
        await Advance(service, 30);

        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);
        Assert.Empty(_power.Requests);
        Assert.Empty(Events(UpsEventType.ShutdownPending));
    }

    [Fact]
    public async Task On_battery_with_low_battery_shuts_down_immediately_without_grace_period()
    {
        var service = Create(s => s.NotifySecondaries = false);
        _ups.Set(Snapshots.Create("ups1", "OB DISCHRG LB", charge: 8));

        await Step(service);

        HostProtectionStatus status = service.GetStatus();
        Assert.Equal(HostProtectionState.ShuttingDown, status.State);
        Assert.Equal(new[] { "ups1" }, status.CriticalUps);
        Assert.Contains("low", status.Reason);
        Assert.Single(Events(UpsEventType.ShutdownPending));
        UpsEvent started = Assert.Single(Events(UpsEventType.ShutdownStarted));
        Assert.DoesNotContain("Dry run", started.Message);
        var request = Assert.Single(_power.Requests);
        Assert.Equal(HostShutdown.DefaultInvocations()[0].ToString(), request[0].ToString());
        Assert.Empty(_ups.Operations); // no FSD, no power-off by default
        Assert.True(_changes > 0);
    }

    [Fact]
    public async Task Shutdown_is_never_triggered_twice()
    {
        var service = Create(s => s.NotifySecondaries = false);
        _ups.Set(Snapshots.Create("ups1", "OB LB"));

        await Step(service);
        await Advance(service, 10);
        _ups.Set(Snapshots.Create("ups1", "OL"));
        await Advance(service, 5);
        _ups.Set(Snapshots.Create("ups1", "OB LB"));
        await Advance(service, 5);

        Assert.Single(_power.Requests);
        Assert.Single(Events(UpsEventType.ShutdownStarted));
        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
    }

    [Theory]
    [InlineData("charge")]
    [InlineData("runtime")]
    [InlineData("fsd")]
    public async Task Each_threshold_rule_triggers(string rule)
    {
        var service = Create(s =>
        {
            s.NotifySecondaries = false;
            s.BatteryChargeBelow = 30;
            s.RuntimeBelowSeconds = 300;
        });
        _ups.Set(rule switch
        {
            "charge" => Snapshots.Create("ups1", "OB DISCHRG", charge: 30, runtime: 1200),
            "runtime" => Snapshots.Create("ups1", "OB DISCHRG", charge: 80, runtime: 240),
            _ => Snapshots.Create("ups1", "OL", forcedShutdown: true),
        });

        await Step(service);

        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
        Assert.Contains(rule switch { "charge" => "charge", "runtime" => "runtime", _ => "FSD" }, service.GetStatus().Reason);
    }

    [Fact]
    public async Task Thresholds_do_not_apply_on_line_power()
    {
        var service = Create(s =>
        {
            s.BatteryChargeBelow = 50;
            s.RuntimeBelowSeconds = 600;
        });
        _ups.Set(Snapshots.Create("ups1", "OL CHRG", charge: 20, runtime: 100));

        await Advance(service, 5);

        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);
    }

    [Fact]
    public async Task Disabled_rules_do_not_trigger()
    {
        var service = Create(s =>
        {
            s.OnLowBattery = false;
            s.OnForcedShutdown = false;
        });
        _ups.Set(Snapshots.Create("ups1", "OB LB", forcedShutdown: true));

        await Advance(service, 5);

        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);
    }

    [Fact]
    public async Task On_battery_longer_than_the_limit_triggers()
    {
        var service = Create(s =>
        {
            s.NotifySecondaries = false;
            s.OnBatteryLongerThanSeconds = 60;
        });
        _ups.Set(Snapshots.Create("ups1", "OB DISCHRG"));

        await Step(service);
        await Advance(service, 60);
        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);

        await Advance(service, 1);
        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
        Assert.Contains("on battery for 61 s", service.GetStatus().Reason);
    }

    [Fact]
    public async Task Returning_to_line_power_resets_the_time_on_battery()
    {
        var service = Create(s => s.OnBatteryLongerThanSeconds = 60);
        _ups.Set(Snapshots.Create("ups1", "OB"));
        await Advance(service, 50);
        _ups.Set(Snapshots.Create("ups1", "OL"));
        await Advance(service, 1);
        _ups.Set(Snapshots.Create("ups1", "OB"));
        await Advance(service, 50);

        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);
    }

    [Fact]
    public async Task Communication_lost_while_on_battery_triggers_after_the_dead_time()
    {
        var service = Create(s =>
        {
            s.NotifySecondaries = false;
            s.CommunicationLostOnBatterySeconds = 15;
        });
        _ups.Set(Snapshots.Create("ups1", "OB DISCHRG"));
        await Step(service);

        _ups.Set(Snapshots.With(_ups.GetSnapshot("ups1")!, available: false));
        await Advance(service, 15);
        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);

        await Advance(service, 1);
        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
        Assert.Contains("communication", service.GetStatus().Reason);
    }

    [Fact]
    public async Task Communication_lost_on_line_power_or_never_seen_is_not_critical()
    {
        var service = Create(s => s.Ups = ["ups1", "ghost"]);
        _ups.Set(Snapshots.Create("ups1", "OL"));
        await Step(service);

        _ups.Set(Snapshots.With(_ups.GetSnapshot("ups1")!, available: false));
        await Advance(service, 120);

        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);
        Assert.Empty(service.GetStatus().CriticalUps);
    }

    [Fact]
    public async Task Ups_removed_from_the_registry_while_on_battery_counts_as_lost()
    {
        var service = Create(s => s.NotifySecondaries = false);
        _ups.Set(Snapshots.Create("ups1", "OB"));
        await Step(service);

        _ups.Remove("ups1");
        await Advance(service, 16);

        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
    }

    [Fact]
    public async Task Minimum_supplies_with_two_ups()
    {
        var service = Create(s =>
        {
            s.Ups = ["a", "b"];
            s.MinimumSupplies = 1;
            s.NotifySecondaries = false;
        });
        _ups.Set(Snapshots.Create("a", "OB LB"));
        _ups.Set(Snapshots.Create("b", "OL"));

        await Advance(service, 3);
        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);
        Assert.Equal(new[] { "a" }, service.GetStatus().CriticalUps);

        _ups.Set(Snapshots.Create("b", "OB LB"));
        await Step(service);
        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
        Assert.Contains("0 of 2 UPSes healthy, 1 needed", service.GetStatus().Reason);
    }

    [Fact]
    public async Task Minimum_supplies_two_of_two_triggers_on_the_first_critical_ups()
    {
        var service = Create(s =>
        {
            s.Ups = ["a", "b"];
            s.MinimumSupplies = 2;
            s.NotifySecondaries = false;
        });
        _ups.Set(Snapshots.Create("a", "OL"));
        _ups.Set(Snapshots.Create("b", "OB LB"));

        await Step(service);

        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
        Assert.Equal(new[] { "b" }, service.GetStatus().CriticalUps);
    }

    [Fact]
    public async Task Power_returning_during_the_grace_period_cancels_the_shutdown()
    {
        var service = Create(s => s.ShutdownDelaySeconds = 30);
        _ups.Set(Snapshots.Create("ups1", "OB LB"));

        await Step(service);
        HostProtectionStatus pending = service.GetStatus();
        Assert.Equal(HostProtectionState.Pending, pending.State);
        Assert.Equal(_time.GetUtcNow().AddSeconds(30), pending.ShutdownAt);
        UpsEvent announced = Assert.Single(Events(UpsEventType.ShutdownPending));
        Assert.Equal(EventSeverity.Critical, announced.Severity);
        Assert.Contains("in 30 s", announced.Message);

        await Advance(service, 10);
        _ups.Set(Snapshots.Create("ups1", "OL CHRG"));
        await Step(service);

        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);
        Assert.Null(service.GetStatus().ShutdownAt);
        Assert.Single(Events(UpsEventType.ShutdownCancelled));
        await Advance(service, 60);
        Assert.Empty(_power.Requests);

        // A new outage schedules a new shutdown.
        _ups.Set(Snapshots.Create("ups1", "OB LB"));
        await Step(service);
        Assert.Equal(HostProtectionState.Pending, service.GetStatus().State);
    }

    [Fact]
    public async Task Grace_period_ending_starts_the_shutdown()
    {
        var service = Create(s =>
        {
            s.ShutdownDelaySeconds = 30;
            s.NotifySecondaries = false;
        });
        _ups.Set(Snapshots.Create("ups1", "OB LB"));

        await Step(service);
        await Advance(service, 29);
        Assert.Equal(HostProtectionState.Pending, service.GetStatus().State);
        Assert.Empty(_power.Requests);

        await Advance(service, 1);
        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
        Assert.Single(_power.Requests);
    }

    [Fact]
    public async Task A_runtime_calibration_is_not_a_power_failure()
    {
        var service = Create(s =>
        {
            s.NotifySecondaries = false;
            s.BatteryChargeBelow = 50;
        });
        // calibrate.start from the web panel: the UPS runs on battery down to low battery on purpose.
        _ups.Set(Snapshots.Create("ups1", "OB DISCHRG CAL LB", charge: 20));
        await Advance(service, 5);
        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);
        Assert.Empty(_power.Requests);

        // Still on battery once the calibration is over: a real outage.
        _ups.Set(Snapshots.Create("ups1", "OB DISCHRG LB", charge: 20));
        await Step(service);
        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
    }

    [Fact]
    public async Task A_driver_restart_during_the_grace_period_keeps_the_countdown()
    {
        var service = Create(s =>
        {
            s.ShutdownDelaySeconds = 30;
            s.NotifySecondaries = false;
        });
        _ups.Set(Snapshots.Create("ups1", "OB LB"));
        await Step(service);
        DateTimeOffset? shutdownAt = service.GetStatus().ShutdownAt;
        await Advance(service, 10);

        // A configuration save restarts the driver: no fresh data for a few seconds (less than the dead time).
        _ups.Set(Snapshots.With(_ups.GetSnapshot("ups1")!, available: false));
        await Advance(service, 5);
        Assert.Equal(HostProtectionState.Pending, service.GetStatus().State);
        Assert.Equal(shutdownAt, service.GetStatus().ShutdownAt);
        Assert.Empty(Events(UpsEventType.ShutdownCancelled));

        _ups.Set(Snapshots.Create("ups1", "OB LB"));
        await Advance(service, 14);
        Assert.Equal(HostProtectionState.Pending, service.GetStatus().State);
        await Advance(service, 1);
        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
        Assert.Single(Events(UpsEventType.ShutdownPending));
    }

    [Fact]
    public async Task Power_returning_while_the_data_is_stale_cancels_once_fresh_data_shows_it()
    {
        var service = Create(s => s.ShutdownDelaySeconds = 60);
        _ups.Set(Snapshots.Create("ups1", "OB LB"));
        await Step(service);

        _ups.Set(Snapshots.With(_ups.GetSnapshot("ups1")!, available: false));
        await Advance(service, 5);
        _ups.Set(Snapshots.Create("ups1", "OL CHRG"));
        await Step(service);

        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);
        Assert.Single(Events(UpsEventType.ShutdownCancelled));
        await Advance(service, 120);
        Assert.Empty(_power.Requests);
    }

    [Fact]
    public async Task A_wall_clock_jump_forward_is_not_lost_communication()
    {
        var service = Create(s => s.NotifySecondaries = false);
        _ups.Set(Snapshots.Create("ups1", "OB DISCHRG", charge: 90));
        await Step(service);

        // NTP moves the clock one hour ahead: the last poll suddenly looks old until the next one arrives.
        _clock.Offset = TimeSpan.FromHours(1);
        _ups.Set(Snapshots.With(_ups.GetSnapshot("ups1")!, available: false));
        await Advance(service, 2);
        _ups.Set(Snapshots.Create("ups1", "OB DISCHRG", charge: 90));
        await Advance(service, 2);

        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);
        Assert.Empty(Events(UpsEventType.ShutdownPending));
        Assert.Empty(_power.Requests);
    }

    [Fact]
    public async Task A_wall_clock_jump_forward_is_not_time_on_battery()
    {
        var service = Create(s =>
        {
            s.NotifySecondaries = false;
            s.OnBatteryLongerThanSeconds = 300;
        });
        _ups.Set(Snapshots.Create("ups1", "OB DISCHRG", charge: 90));
        await Step(service);

        _clock.Offset = TimeSpan.FromHours(1);
        await Advance(service, 10);

        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);
        Assert.Empty(_power.Requests);
        await Advance(service, 291);
        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
    }

    [Fact]
    public async Task A_wall_clock_jump_backward_does_not_delay_the_shutdown()
    {
        var service = Create(s =>
        {
            s.ShutdownDelaySeconds = 30;
            s.SecondariesTimeoutSeconds = 15;
        });
        Login("ups1", 1);
        _ups.Set(Snapshots.Create("ups1", "OB LB"));
        await Step(service);
        await Advance(service, 10);

        _clock.Offset = TimeSpan.FromHours(-1);
        await Advance(service, 20);
        Assert.Equal(HostProtectionState.WaitingForSecondaries, service.GetStatus().State);
        await Advance(service, 15);

        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
        Assert.Single(_power.Requests);
    }

    [Fact]
    public async Task Fsd_is_set_and_the_shutdown_waits_for_the_secondaries_to_log_out()
    {
        var service = Create(s => s.SecondariesTimeoutSeconds = 30);
        NutSession nas = Login("ups1", 1);
        NutSession pc = Login("UPS1", 2);
        _ups.Set(Snapshots.Create("ups1", "OB LB"));

        await Step(service);

        HostProtectionStatus waiting = service.GetStatus();
        Assert.Equal(HostProtectionState.WaitingForSecondaries, waiting.State);
        Assert.Equal(_time.GetUtcNow().AddSeconds(30), waiting.ShutdownAt);
        Assert.Contains("fsd ups1 by host-protection", _ups.Operations);
        Assert.True(_ups.GetSnapshot("ups1")!.ForcedShutdown);
        Assert.Empty(Events(UpsEventType.ShutdownStarted));

        await Advance(service, 5);
        _sessions.Close(nas);
        await Step(service);
        Assert.Equal(HostProtectionState.WaitingForSecondaries, service.GetStatus().State);
        Assert.Empty(_power.Requests);

        _sessions.Close(pc);
        await Step(service);
        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
        Assert.Single(_power.Requests);
        Assert.Single(Events(UpsEventType.ShutdownStarted));
    }

    [Fact]
    public async Task Secondaries_that_never_log_out_delay_the_shutdown_by_the_timeout_only()
    {
        var service = Create(s => s.SecondariesTimeoutSeconds = 15);
        Login("ups1", 1);
        _ups.Set(Snapshots.Create("ups1", "OB LB"));

        await Step(service);
        await Advance(service, 14);
        Assert.Equal(HostProtectionState.WaitingForSecondaries, service.GetStatus().State);

        await Advance(service, 1);
        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
        Assert.Single(_power.Requests);
    }

    [Fact]
    public async Task Power_off_sends_the_delay_and_the_command_before_the_shutdown()
    {
        string[]? operationsAtShutdown = null;
        _power.OnRequest = () => operationsAtShutdown = _ups.Operations.ToArray();
        var service = Create(s =>
        {
            s.Ups = ["a", "b", "c"];
            s.MinimumSupplies = 3;
            s.NotifySecondaries = false;
            s.PowerOffUps = true;
            s.PowerOffCommand = "load.off.delay";
            s.PowerOffDelaySeconds = 90;
        });
        _ups.Set(Snapshots.Create("a", "OB LB"));
        _ups.Set(Snapshots.Create("b", "OB LB", writableDelay: false));
        _ups.Set(Snapshots.Create("c", "OL", forcedShutdown: true)); // critical, but on line power: never cut

        await Step(service);

        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
        Assert.Equal(new[] { "set a ups.delay.shutdown=90", "cmd a load.off.delay 90", "cmd b load.off.delay 90" },
                     operationsAtShutdown);
    }

    [Fact]
    public async Task Power_off_with_shutdown_return_has_no_parameter()
    {
        var service = Create(s =>
        {
            s.NotifySecondaries = false;
            s.PowerOffUps = true;
            s.PowerOffDelaySeconds = 120;
        });
        _ups.Set(Snapshots.Create("ups1", "OB LB"));

        await Step(service);

        Assert.Equal(new[] { "set ups1 ups.delay.shutdown=120", "cmd ups1 shutdown.return" }, _ups.Operations.ToArray());
    }

    [Fact]
    public async Task Cancel_pending_suppresses_until_the_condition_clears_once()
    {
        var service = Create(s => s.ShutdownDelaySeconds = 60);
        _ups.Set(Snapshots.Create("ups1", "OB LB"));
        await Step(service);
        Assert.Equal(HostProtectionState.Pending, service.GetStatus().State);

        Assert.True(service.CancelPending(CommandOrigin.Web("admin", "192.168.1.10")));

        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);
        UpsEvent cancelled = Assert.Single(Events(UpsEventType.ShutdownCancelled));
        Assert.Equal("web:admin@192.168.1.10", cancelled.Actor);
        Assert.False(service.CancelPending(CommandOrigin.System));

        await Advance(service, 120);
        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);
        Assert.Single(Events(UpsEventType.ShutdownPending));
        Assert.Empty(_power.Requests);

        _ups.Set(Snapshots.Create("ups1", "OL"));
        await Step(service);
        _ups.Set(Snapshots.Create("ups1", "OB LB"));
        await Step(service);
        Assert.Equal(HostProtectionState.Pending, service.GetStatus().State);
        Assert.Equal(2, Events(UpsEventType.ShutdownPending).Count);
    }

    [Fact]
    public async Task Dry_run_does_everything_but_the_shutdown_and_re_arms()
    {
        var service = Create(s =>
        {
            s.DryRun = true;
            s.PowerOffUps = true;
        });
        _ups.Set(Snapshots.Create("ups1", "OB LB"));

        await Step(service);

        HostProtectionStatus status = service.GetStatus();
        Assert.Equal(HostProtectionState.DryRunCompleted, status.State);
        Assert.True(status.DryRun);
        Assert.Empty(_power.Requests);
        // Neither FSD (the NUT clients would really shut down) nor the power-off: a dry run turns nothing off.
        Assert.Empty(_ups.Operations);
        Assert.Contains("Dry run", Assert.Single(Events(UpsEventType.ShutdownStarted)).Message);

        // Still on battery with a low battery: the dry run stays completed, it does not repeat.
        await Advance(service, 5);
        Assert.Equal(HostProtectionState.DryRunCompleted, service.GetStatus().State);

        _ups.Set(Snapshots.Create("ups1", "OL"));
        await Step(service);
        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);

        _ups.Set(Snapshots.Create("ups1", "OB LB"));
        await Step(service);
        Assert.Equal(HostProtectionState.DryRunCompleted, service.GetStatus().State);
        Assert.Equal(2, Events(UpsEventType.ShutdownStarted).Count);
    }

    [Fact]
    public async Task Safety_switch_skips_the_shutdown_even_without_dry_run()
    {
        var service = Create(s =>
        {
            s.NotifySecondaries = false;
            s.PowerOffUps = true;
        }, armed: false);
        _ups.Set(Snapshots.Create("ups1", "OB LB"));

        await Step(service);

        Assert.Equal(HostProtectionState.DryRunCompleted, service.GetStatus().State);
        Assert.True(service.GetStatus().DryRun);
        Assert.Empty(_power.Requests);
        Assert.Empty(_ups.Operations);
    }

    [Fact]
    public void Debug_builds_never_arm_the_real_shutdown()
    {
#if DEBUG
        Assert.NotNull(ShutdownSafety.FromEnvironment().DisabledReason);
#else
        Assert.True(true);
#endif
    }

    [Fact]
    public async Task Disabling_cancels_a_pending_shutdown()
    {
        var service = Create(s => s.ShutdownDelaySeconds = 60);
        _ups.Set(Snapshots.Create("ups1", "OB LB"));
        await Step(service);
        Assert.Equal(HostProtectionState.Pending, service.GetStatus().State);

        _config.Update(c => c.HostProtection.Enabled = false);
        await Step(service);

        Assert.Equal(HostProtectionState.Disabled, service.GetStatus().State);
        Assert.False(service.GetStatus().Enabled);
        Assert.Single(Events(UpsEventType.ShutdownCancelled));
        await Advance(service, 120);
        Assert.Empty(_power.Requests);

        _config.Update(c => c.HostProtection.Enabled = true);
        await Step(service);
        Assert.Equal(HostProtectionState.Pending, service.GetStatus().State);
    }

    [Fact]
    public async Task Disabling_after_the_shutdown_started_changes_nothing()
    {
        var service = Create(s => s.NotifySecondaries = false);
        _ups.Set(Snapshots.Create("ups1", "OB LB"));
        await Step(service);

        _config.Update(c => c.HostProtection.Enabled = false);
        await Step(service);

        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
    }

    [Fact]
    public async Task A_configured_shutdown_command_is_parsed_and_falls_back_to_the_default()
    {
        var service = Create(s =>
        {
            s.NotifySecondaries = false;
            s.ShutdownCommand = "\"C:\\Program Files\\Tool\\off.exe\" /now \"a b\"";
        });
        _ups.Set(Snapshots.Create("ups1", "OB LB"));

        await Step(service);

        IReadOnlyList<ShutdownInvocation> request = Assert.Single(_power.Requests);
        Assert.Equal("C:\\Program Files\\Tool\\off.exe", request[0].FileName);
        Assert.Equal(new[] { "/now", "a b" }, request[0].Arguments);
        Assert.Equal(HostShutdown.DefaultInvocations()[0].ToString(), request[1].ToString());
    }

    [Fact]
    public async Task No_monitored_ups_never_triggers()
    {
        var service = Create(s => s.Ups = []);

        await Advance(service, 5);

        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);
    }

    [Fact]
    public async Task Background_loop_reacts_to_snapshot_changes()
    {
        var service = Create(s => s.NotifySecondaries = false);
        _ups.Set(Snapshots.Create("ups1", "OL"));
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Wait.UntilAsync(() => service.GetStatus().State == HostProtectionState.Monitoring, "monitoring");

            UpsSnapshot critical = Snapshots.Create("ups1", "OB LB");
            _ups.Set(critical);
            _hub.Publish(new SnapshotChangedMessage(null, critical));

            await Wait.UntilAsync(() => !_power.Requests.IsEmpty, "the shutdown request");
            Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Default_command_matches_the_operating_system()
    {
        string command = HostShutdown.DefaultCommand;
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("shutdown.exe /s /f /t 0 /d 6:12 /c \"NutHub: UPS power critical\"", command);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.True(command is "systemctl poweroff" or "shutdown -h now", command);
        }
    }

    private HostProtectionService Create(Action<HostProtectionSettings>? configure = null, bool armed = true)
    {
        _config.Update(c =>
        {
            c.HostProtection = new HostProtectionSettings { Enabled = true, Ups = ["ups1"] };
            configure?.Invoke(c.HostProtection);
        });
        var safety = new ShutdownSafety(() => armed ? null : "test safety switch");
        return new HostProtectionService(_config, _ups, _sessions, _hub, _clock, _power, safety, NullLogger.Instance);
    }

    private static Task Step(HostProtectionService service) => service.EvaluateAsync(CancellationToken.None);

    private async Task Advance(HostProtectionService service, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Step(service);
        }
    }

    private NutSession Login(string ups, int n)
    {
        NutSession session = _sessions.Open(new IPEndPoint(IPAddress.Parse("192.168.1." + n), 40000 + n), () => { });
        _sessions.SetLogin(session, ups);
        return session;
    }

    private List<UpsEvent> Events(UpsEventType type)
    {
        lock (_events)
        {
            return _events.Where(e => e.Type == type).ToList();
        }
    }
}
