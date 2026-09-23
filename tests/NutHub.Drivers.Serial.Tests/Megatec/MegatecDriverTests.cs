using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Model;
using NutHub.Drivers.Serial.Megatec;
using NutHub.Drivers.Serial.Tests.Fakes;
using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.Tests.Megatec;

/// <summary>The whole driver against an emulated Megatec UPS: detection, polling, commands, failures, reconnection.</summary>
public sealed class MegatecDriverTests
{
    private readonly VirtualTimeProvider _time = new();
    private readonly FakeMegatecUps _ups = new();
    private readonly FakeTransport _transport;

    public MegatecDriverTests()
    {
        _transport = new FakeTransport(_ups);
    }

    private MegatecDriver CreateDriver(string protocol = QxProtocols.Auto) =>
        new("test", TestSettings.Megatec(protocol), _transport, _time, NullLogger.Instance);

    [Fact]
    public async Task Auto_detection_finds_the_megatec_protocol_and_publishes_every_poll()
    {
        MegatecDriver driver = CreateDriver();
        var context = new FakeDriverContext(_time);
        await using (DriverRun.Start(driver, context))
        {
            await context.WaitForAsync(c => c.PublishCount >= 3, "three polls");

            Assert.Equal("megatec", driver.ProtocolName);
            Assert.Equal("OL", context.Status);
            Assert.Equal("MegaTec", context.Last!.Variables["ups.mfr"]);
            Assert.Contains("shutdown.return", context.Last.Commands!);
            Assert.True(context.Last.VariableInfo!["ups.delay.start"].Writable);
        }

        // Every candidate before megatec was tried and refused (the fake echoes what it does not know).
        Assert.Contains("QGS", _ups.Received);
        Assert.Contains("M", _ups.Received);
        Assert.Contains("D", _ups.Received);
    }

    [Fact]
    public async Task Power_failure_and_low_battery_reach_the_status()
    {
        MegatecDriver driver = CreateDriver("megatec");
        var context = new FakeDriverContext(_time);
        await using (DriverRun.Start(driver, context))
        {
            await context.WaitForAsync(c => c.Status == "OL", "on line");

            _ups.SetOnBattery(24.0, lowBattery: false);
            await context.WaitForAsync(c => c.Status == "OB", "on battery");

            _ups.SetOnBattery(21.0, lowBattery: true);
            await context.WaitForAsync(c => c.Status == "OB LB", "low battery");
            Assert.Equal("4", context.Last!.Variables["battery.charge"]);

            _ups.SetOnLine();
            await context.WaitForAsync(c => c.Status == "OL", "back on line");
        }
    }

    [Fact]
    public async Task Commands_and_delays_are_serialised_with_the_polls()
    {
        MegatecDriver driver = CreateDriver("megatec");
        var context = new FakeDriverContext(_time);
        await using (DriverRun.Start(driver, context))
        {
            await context.WaitForAsync(c => c.PublishCount >= 1, "first poll");

            CommandResult written = await driver.SetVariableAsync("ups.delay.shutdown", "60", CancellationToken.None);
            CommandResult shutdown = await driver.InstantCommandAsync("shutdown.return", null, CancellationToken.None);
            CommandResult test = await driver.InstantCommandAsync("test.battery.start.quick", null, CancellationToken.None);
            CommandResult unknown = await driver.InstantCommandAsync("bypass.start", null, CancellationToken.None);

            Assert.True(written.IsSuccess);
            Assert.True(shutdown.IsSuccess, shutdown.ToString());
            Assert.True(test.IsSuccess, test.ToString());
            Assert.Equal(CommandStatus.NotSupported, unknown.Status);
            Assert.Contains("S01R0003", _ups.Received);
            Assert.Contains("T", _ups.Received);
            await context.WaitForAsync(c => c.Last!.Variables["ups.delay.shutdown"] == "60", "the new delay");
        }
    }

    [Fact]
    public async Task Commands_before_the_connection_report_not_connected()
    {
        MegatecDriver driver = CreateDriver("megatec");

        CommandResult result = await driver.InstantCommandAsync("beeper.toggle", null, CancellationToken.None);

        Assert.Equal(CommandStatus.DriverNotConnected, result.Status);
        await driver.DisposeAsync();
    }

    [Fact]
    public async Task A_silent_UPS_goes_stale_after_three_polls_and_is_reconnected()
    {
        MegatecDriver driver = CreateDriver("megatec");
        var context = new FakeDriverContext(_time);
        await using (DriverRun.Start(driver, context))
        {
            await context.WaitForAsync(c => c.PublishCount >= 1, "first poll");
            int published = context.PublishCount;

            _ups.Silent = true;
            await context.WaitForAsync(c => c.Events.Any(e => e.StartsWith("disconnected:", StringComparison.Ordinal)), "stale data");
            Assert.Contains("Q1", context.Events.Last(e => e.StartsWith("disconnected:", StringComparison.Ordinal)));

            // After six silent polls the link is reopened and the UPS identified again.
            await context.WaitForAsync(_ => _transport.OpenCount >= 2, "a reconnection attempt");
            _ups.Silent = false;
            await context.WaitForAsync(c => c.PublishCount > published && c.Events[^1] == "publish", "the UPS back");
            Assert.Equal("OL", context.Status);
        }
    }

    [Fact]
    public async Task A_failing_open_is_reported_and_retried()
    {
        _transport.OpenError = new TransportException(TransportErrorKind.AccessDenied,
            "Permission denied on /dev/ttyUSB0: add the service user to the 'dialout' group.");
        MegatecDriver driver = CreateDriver("megatec");
        var context = new FakeDriverContext(_time);
        await using (DriverRun.Start(driver, context))
        {
            await context.WaitForAsync(c => c.Events.Any(e => e.Contains("dialout", StringComparison.Ordinal)), "the open error");
            Assert.Equal(0, context.PublishCount);

            _transport.OpenError = null;
            await context.WaitForAsync(c => c.PublishCount >= 1, "the port opened");
        }
    }

    [Fact]
    public async Task An_unplugged_adapter_breaks_the_link_and_the_driver_reconnects()
    {
        MegatecDriver driver = CreateDriver("megatec");
        var context = new FakeDriverContext(_time);
        await using (DriverRun.Start(driver, context))
        {
            await context.WaitForAsync(c => c.PublishCount >= 1, "first poll");

            _transport.Broken = true;
            _transport.OpenError = new TransportException(TransportErrorKind.NotFound, "The serial port fake0 does not exist.");
            await context.WaitForAsync(c => c.Events.Any(e => e.Contains("does not exist", StringComparison.Ordinal)), "the port gone");

            int published = context.PublishCount;
            _transport.Broken = false;
            _transport.OpenError = null;
            await context.WaitForAsync(c => c.PublishCount > published, "the port back");
            Assert.True(_transport.OpenCount >= 2);
        }
    }

    [Fact]
    public async Task No_protocol_answers_gives_an_actionable_error()
    {
        _ups.Silent = true;
        MegatecDriver driver = CreateDriver();
        var context = new FakeDriverContext(_time);
        await using (DriverRun.Start(driver, context))
        {
            await context.WaitForAsync(c => c.Events.Any(e => e.Contains("baud rate", StringComparison.Ordinal)), "the error");
        }
    }
}
