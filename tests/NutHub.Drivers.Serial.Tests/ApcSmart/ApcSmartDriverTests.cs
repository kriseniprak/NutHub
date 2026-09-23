using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Serial.ApcSmart;
using NutHub.Drivers.Serial.Tests.Fakes;
using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.Tests.ApcSmart;

/// <summary>The APC Smart driver against an emulated Smart-UPS: start-up, polling, commands, EEPROM writes.</summary>
public sealed class ApcSmartDriverTests
{
    private readonly VirtualTimeProvider _time = new();
    private readonly FakeApcSmartUps _ups = new();
    private readonly FakeTransport _transport;
    private readonly ApcSmartDriver _driver;

    public ApcSmartDriverTests()
    {
        _transport = new FakeTransport(_ups);
        _driver = new ApcSmartDriver("test", new ApcSmartSettings { Transport = new TcpSettings("127.0.0.1", 9) },
                                     _transport, _time, NullLogger.Instance);
    }

    private async Task<DriverUpdate> ConnectAndPollAsync()
    {
        Assert.Null(await _driver.ConnectAsync(CancellationToken.None));
        return await PollAsync();
    }

    private async Task<DriverUpdate> PollAsync()
    {
        var result = await _driver.PollAsync(recovering: false, CancellationToken.None);
        Assert.Null(result.Error);
        return result.Update!;
    }

    [Fact]
    public async Task Start_up_learns_the_variables_from_the_command_set()
    {
        DriverUpdate update = await ConnectAndPollAsync();
        var v = update.Variables;

        Assert.Equal("OL", v["ups.status"]);
        Assert.Equal("APC", v["ups.mfr"]);
        Assert.Equal("Smart-UPS 1000", v["ups.model"]);
        Assert.Equal("AS0123456789", v["ups.serial"]);
        Assert.Equal("652.13.I", v["ups.firmware"]);
        Assert.Equal("UPS_IDEN", v["ups.id"]);
        Assert.Equal("100", v["battery.charge"]);
        Assert.Equal("2700", v["battery.runtime"]); // "0045:" minutes.
        Assert.Equal("120", v["battery.runtime.low"]);
        Assert.Equal("230.4", v["input.voltage"]);
        Assert.Equal("230.4", v["output.voltage"]);
        Assert.Equal("50", v["input.frequency"]);
        Assert.Equal("23.4", v["ups.load"]);
        Assert.Equal("27.36", v["battery.voltage"]);
        Assert.Equal("31.5", v["ups.temperature"]);
        Assert.Equal("OK", v["ups.test.result"]);
        Assert.Equal("1209600", v["ups.test.interval"]);
        Assert.Equal("253", v["input.transfer.high"]);
        Assert.Equal("196", v["input.transfer.low"]);
        Assert.Equal("20", v["ups.delay.shutdown"]);
        Assert.Equal("0", v["ups.delay.start"]);
        Assert.Contains("transfer", v["input.transfer.reason"]);
        Assert.False(v.ContainsKey("ambient.1.temperature")); // 'T' is not in the command set.

        Assert.Equal(["253", "264", "271", "280"], update.VariableInfo!["input.transfer.high"].EnumValues);
        Assert.Equal(["20", "180", "300", "600"], update.VariableInfo["ups.delay.shutdown"].EnumValues);
        Assert.Equal(8, update.VariableInfo["ups.id"].MaxLength);
        Assert.False(update.VariableInfo.ContainsKey("battery.charge"));

        Assert.Equal(
            ["bypass.start", "bypass.stop", "calibrate.start", "calibrate.stop", "load.off", "load.on", "shutdown.return",
             "shutdown.stayoff", "test.battery.start", "test.battery.stop", "test.failure.start", "test.panel.start"],
            update.Commands!.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false, "OB")]
    [InlineData(true, "OB LB")]
    public async Task Power_failure_is_read_from_the_status_register(bool lowBattery, string status)
    {
        await ConnectAndPollAsync();

        _ups.SetOnBattery(lowBattery, charge: "012.0");
        DriverUpdate update = await PollAsync();

        Assert.Equal(status, update.Variables["ups.status"]);
        Assert.Equal("12", update.Variables["battery.charge"]);
    }

    [Fact]
    public async Task A_garbled_status_register_fails_the_poll()
    {
        await ConnectAndPollAsync();
        _ups.StatusReply = "ZZ";

        var result = await _driver.PollAsync(recovering: false, CancellationToken.None);

        Assert.Null(result.Update);
        Assert.Contains("status", result.Error);
    }

    [Fact]
    public async Task An_old_model_is_recognised_by_its_firmware()
    {
        _ups.Values['V'] = "6QD";

        DriverUpdate update = await ConnectAndPollAsync();

        Assert.Equal("Smart-UPS", update.Variables["ups.model"]);
        Assert.Equal("6QD", update.Variables["ups.firmware"]);
        Assert.DoesNotContain("a", _ups.Received);
    }

    [Fact]
    public async Task A_silent_line_gives_an_actionable_error()
    {
        _ups.Silent = true;

        string? error = await _driver.ConnectAsync(CancellationToken.None);

        Assert.NotNull(error);
        Assert.Contains("940-0024", error);
    }

    [Theory]
    [InlineData("test.battery.start", "W")]
    [InlineData("test.panel.start", "A")]
    [InlineData("bypass.start", "^")]
    [InlineData("calibrate.start", "D")]
    public async Task Simple_commands_are_acknowledged(string command, string sent)
    {
        await ConnectAndPollAsync();

        CommandResult result = await _driver.InstantCommandAsync(command, null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(sent, _ups.Received[^1]);
    }

    [Fact]
    public async Task Calibration_cannot_be_started_twice()
    {
        await ConnectAndPollAsync();

        Assert.True((await _driver.InstantCommandAsync("calibrate.start", null, CancellationToken.None)).IsSuccess);
        Assert.Equal(CommandStatus.Failed, (await _driver.InstantCommandAsync("calibrate.start", null, CancellationToken.None)).Status);
        Assert.True((await _driver.InstantCommandAsync("calibrate.stop", null, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Power_commands_are_sent_twice()
    {
        await ConnectAndPollAsync();

        Assert.True((await _driver.InstantCommandAsync("shutdown.stayoff", null, CancellationToken.None)).IsSuccess);
        Assert.Equal(["K", "K"], _ups.Received.TakeLast(2));
        Assert.True((await _driver.InstantCommandAsync("load.off", null, CancellationToken.None)).IsSuccess);
        Assert.Equal(["Z", "Z"], _ups.Received.TakeLast(2));
        Assert.True((await _driver.InstantCommandAsync("load.on", null, CancellationToken.None)).IsSuccess);
        Assert.Equal(["\u000E", "\u000E"], _ups.Received.TakeLast(2));
    }

    [Fact]
    public async Task Shutdown_return_hibernates_on_line_power_and_soft_hibernates_on_battery()
    {
        await ConnectAndPollAsync();

        Assert.True((await _driver.InstantCommandAsync("shutdown.return", null, CancellationToken.None)).IsSuccess);
        Assert.Equal("@000", _ups.Received[^1]);
        Assert.True((await _driver.InstantCommandAsync("shutdown.return", "at:5", CancellationToken.None)).IsSuccess);
        Assert.Equal("@005", _ups.Received[^1]); // Two digits would select the two-digit variant.

        _ups.SetOnBattery(lowBattery: true);
        Assert.True((await _driver.InstantCommandAsync("shutdown.return", null, CancellationToken.None)).IsSuccess);
        Assert.Equal("S", _ups.Received[^1]);
    }

    [Fact]
    public async Task Shutdown_return_cs_simulates_a_power_failure_first()
    {
        await ConnectAndPollAsync();

        CommandResult result = await _driver.InstantCommandAsync("shutdown.return", "cs", CancellationToken.None);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(["U", "S"], _ups.Received.TakeLast(2));
    }

    [Theory]
    [InlineData("shutdown.return", "now")]
    [InlineData("shutdown.return", "at:1234")]
    [InlineData("load.off", "1")]
    public async Task Invalid_parameters_are_refused(string command, string parameter)
    {
        await ConnectAndPollAsync();
        int sent = _ups.Received.Count;

        CommandResult result = await _driver.InstantCommandAsync(command, parameter, CancellationToken.None);

        Assert.Equal(CommandStatus.InvalidArgument, result.Status);
        Assert.Equal(sent, _ups.Received.Count);
    }

    [Fact]
    public async Task Enumerated_EEPROM_values_are_cycled_with_minus()
    {
        await ConnectAndPollAsync();

        CommandResult result = await _driver.SetVariableAsync("input.transfer.high", "271", CancellationToken.None);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal("271", _ups.Values['u']);
        Assert.Equal(2, _ups.Received.Count(r => r == "-"));
        Assert.Equal("271", (await PollAsync()).Variables["input.transfer.high"]);
    }

    [Fact]
    public async Task An_unknown_enumerated_value_is_refused_after_a_full_cycle()
    {
        await ConnectAndPollAsync();

        CommandResult result = await _driver.SetVariableAsync("ups.delay.shutdown", "90", CancellationToken.None);

        Assert.Equal(CommandStatus.InvalidValue, result.Status);
        Assert.Equal("020", _ups.Values['p']); // Back where it started.
    }

    [Fact]
    public async Task Strings_are_written_with_minus_and_padded_to_eight_characters()
    {
        await ConnectAndPollAsync();

        CommandResult result = await _driver.SetVariableAsync("ups.id", "RACK1", CancellationToken.None);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Contains("-RACK1\r\r\r", _ups.Received);
        Assert.Equal("RACK1", (await PollAsync()).Variables["ups.id"]);
        Assert.Equal(CommandStatus.TooLong, (await _driver.SetVariableAsync("ups.id", "NINECHARS", CancellationToken.None)).Status);
        Assert.Equal(CommandStatus.ReadOnly, (await _driver.SetVariableAsync("ups.serial", "X", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task The_poll_loop_follows_the_UPS_and_recovers_from_silence()
    {
        var context = new FakeDriverContext(_time);
        await using (DriverRun.Start(_driver, context))
        {
            await context.WaitForAsync(c => c.Status == "OL", "on line");

            _ups.SetOnBattery(lowBattery: false);
            await context.WaitForAsync(c => c.Status == "OB", "on battery");

            _ups.Silent = true;
            await context.WaitForAsync(c => c.Events.Any(e => e.StartsWith("disconnected:", StringComparison.Ordinal)), "stale data");

            int published = context.PublishCount;
            _ups.Silent = false;
            _ups.SetOnLine();
            await context.WaitForAsync(c => c.PublishCount > published && c.Status == "OL", "the UPS back");
        }
    }
}
