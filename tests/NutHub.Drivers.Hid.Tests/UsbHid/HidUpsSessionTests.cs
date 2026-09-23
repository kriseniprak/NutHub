using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Hid.Tests.Support;
using NutHub.Drivers.Hid.Transport;
using NutHub.Drivers.Hid.UsbHid;

namespace NutHub.Drivers.Hid.Tests.UsbHid;

/// <summary>The mapping, conversions, commands and writes of a session, on the synthetic generic UPS.</summary>
public sealed class HidUpsSessionTests
{
    [Fact]
    public void Reads_the_standard_variables()
    {
        var (session, _) = TestSessions.Open(SyntheticUps.Create());

        IReadOnlyDictionary<string, string> vars = TestSessions.Variables(session);

        Assert.Equal("generic", session.Subdriver.Id);
        Assert.Equal("85", vars["battery.charge"]);
        Assert.Equal("1800", vars["battery.runtime"]);
        Assert.Equal("10", vars["battery.charge.low"]);
        Assert.Equal("120", vars["battery.runtime.low"]);
        Assert.Equal("230.0", vars["input.voltage"]);
        Assert.Equal("50.0", vars["input.frequency"]);
        Assert.Equal("OL CHRG", vars["ups.status"]);
        Assert.Equal("enabled", vars["ups.beeper.status"]);
        Assert.Equal("No test initiated", vars["ups.test.result"]);
        Assert.Equal("-1", vars["ups.timer.shutdown"]);
        Assert.Equal("20", vars["ups.delay.shutdown"]);
        Assert.Equal("30", vars["ups.delay.start"]);
        Assert.Equal("Acme", vars["ups.mfr"]);
        Assert.Equal("Acme UPS 1000", vars["ups.model"]);
        Assert.Equal("SN0001", vars["ups.serial"]);
        Assert.Equal("1234", vars["ups.vendorid"]);
        Assert.Equal("5678", vars["ups.productid"]);
        Assert.Equal("Generic HID 1.0", vars["driver.version.data"]);
        Assert.DoesNotContain("ups.alarm", vars.Keys);
    }

    [Fact]
    public void Publishes_commands_and_writable_variables_the_device_supports()
    {
        var (session, _) = TestSessions.Open(SyntheticUps.Create());

        DriverUpdate update = session.BuildUpdate();

        Assert.Equal(
            new[]
            {
                "beeper.disable", "beeper.enable", "beeper.mute", "load.off", "load.off.delay", "load.on", "load.on.delay",
                "shutdown.return", "shutdown.stayoff", "shutdown.stop", "test.battery.start.deep",
                "test.battery.start.quick", "test.battery.stop",
            },
            update.Commands!.Order(StringComparer.Ordinal));
        Assert.True(update.VariableInfo!["battery.charge.low"].Writable);
        Assert.True(update.VariableInfo["ups.delay.shutdown"].Writable);
        Assert.False(update.VariableInfo.ContainsKey("battery.charge"));
    }

    [Fact]
    public void Update_follows_the_device()
    {
        FakeHidDevice device = SyntheticUps.Create();
        var (session, _) = TestSessions.Open(device);

        device.SetFeature(0x03, SyntheticUps.OnBatteryLow);
        device.SetFeature(0x01, 9);
        session.Update();
        IReadOnlyDictionary<string, string> vars = TestSessions.Variables(session);

        Assert.Equal("OB DISCHRG LB", vars["ups.status"]);
        Assert.Equal("9", vars["battery.charge"]);
    }

    [Fact]
    public void Status_alarms_are_published_with_the_alarm_token()
    {
        FakeHidDevice device = SyntheticUps.Create(status: (byte)(SyntheticUps.OnLineCharging | 0b0010_0000)); // NeedReplacement
        var (session, _) = TestSessions.Open(device);

        IReadOnlyDictionary<string, string> vars = TestSessions.Variables(session);

        Assert.Equal("ALARM OL CHRG RB", vars["ups.status"]);
        Assert.Equal("Replace battery!", vars["ups.alarm"]);
    }

    [Theory]
    [InlineData("test.battery.start.quick", null, new byte[] { 0x0A, 0x01 })]
    [InlineData("test.battery.start.deep", null, new byte[] { 0x0A, 0x02 })]
    [InlineData("test.battery.stop", null, new byte[] { 0x0A, 0x03 })]
    [InlineData("beeper.disable", null, new byte[] { 0x08, 0x01 })]
    [InlineData("beeper.off", null, new byte[] { 0x08, 0x01 })] // the old name
    [InlineData("beeper.mute", null, new byte[] { 0x08, 0x03 })]
    [InlineData("load.off.delay", "30", new byte[] { 0x06, 0x1E, 0x00 })]
    [InlineData("load.off.delay", null, new byte[] { 0x06, 0x14, 0x00 })] // the table default, 20 s
    [InlineData("load.off", null, new byte[] { 0x06, 0x00, 0x00 })]
    [InlineData("load.on", null, new byte[] { 0x07, 0x00, 0x00 })]
    [InlineData("shutdown.stop", null, new byte[] { 0x06, 0xFF, 0xFF })]
    public void Commands_write_the_expected_feature_report(string command, string? parameter, byte[] expected)
    {
        FakeHidDevice device = SyntheticUps.Create();
        var (session, _) = TestSessions.Open(device);

        CommandResult result = session.ExecuteCommand(command, parameter);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(expected, Assert.Single(device.Writes));
    }

    [Fact]
    public void Shutdown_return_arms_the_restart_then_the_shutdown()
    {
        FakeHidDevice device = SyntheticUps.Create();
        var (session, _) = TestSessions.Open(device);

        Assert.True(session.ExecuteCommand("shutdown.return", null).IsSuccess);

        Assert.Equal(new byte[][] { [0x07, 0x1E, 0x00], [0x06, 0x14, 0x00] }, device.Writes);
    }

    [Fact]
    public void Shutdown_stayoff_cancels_the_restart()
    {
        FakeHidDevice device = SyntheticUps.Create();
        var (session, _) = TestSessions.Open(device);

        Assert.True(session.ExecuteCommand("shutdown.stayoff", null).IsSuccess);

        Assert.Equal(new byte[][] { [0x07, 0xFF, 0xFF], [0x06, 0x14, 0x00] }, device.Writes);
    }

    [Fact]
    public void Driver_kept_delays_are_written_in_memory_and_used_by_shutdown_commands()
    {
        FakeHidDevice device = SyntheticUps.Create();
        var (session, _) = TestSessions.Open(device, new UsbHidSettings { OnDelay = 90 });

        Assert.True(session.WriteVariable("ups.delay.shutdown", "45").IsSuccess);
        Assert.Empty(device.Writes);
        Assert.True(session.ExecuteCommand("shutdown.return", null).IsSuccess);

        Assert.Equal("45", TestSessions.Variables(session)["ups.delay.shutdown"]);
        Assert.Equal(new byte[][] { [0x07, 90, 0x00], [0x06, 45, 0x00] }, device.Writes);
    }

    [Fact]
    public void Writing_a_variable_keeps_the_other_fields_of_its_report()
    {
        FakeHidDevice device = SyntheticUps.Create();
        var (session, _) = TestSessions.Open(device);

        CommandResult result = session.WriteVariable("battery.charge.low", "25");
        session.Update();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(new byte[] { 0x05, 25, 0x78, 0x00 }, Assert.Single(device.Writes));
        Assert.Equal("25", TestSessions.Variables(session)["battery.charge.low"]);
        Assert.Equal("120", TestSessions.Variables(session)["battery.runtime.low"]);
    }

    [Theory]
    [InlineData("battery.charge", "50", CommandStatus.ReadOnly)]
    [InlineData("ups.id", "x", CommandStatus.NotSupported)]
    [InlineData("battery.charge.low", "many", CommandStatus.InvalidValue)]
    public void Invalid_writes_are_refused_without_touching_the_device(string name, string value, CommandStatus expected)
    {
        FakeHidDevice device = SyntheticUps.Create();
        var (session, _) = TestSessions.Open(device);

        Assert.Equal(expected, session.WriteVariable(name, value).Status);
        Assert.Empty(device.Writes);
    }

    [Theory]
    [InlineData("calibrate.start", null, CommandStatus.NotSupported)]
    [InlineData("load.off.delay", "soon", CommandStatus.InvalidArgument)]
    public void Invalid_commands_are_refused(string command, string? parameter, CommandStatus expected)
    {
        FakeHidDevice device = SyntheticUps.Create();
        var (session, _) = TestSessions.Open(device);

        Assert.Equal(expected, session.ExecuteCommand(command, parameter).Status);
        Assert.Empty(device.Writes);
    }

    [Fact]
    public void A_refused_write_fails_the_command()
    {
        FakeHidDevice device = SyntheticUps.Create();
        var (session, _) = TestSessions.Open(device);
        device.FailFeatures = true;

        CommandResult result = session.ExecuteCommand("test.battery.start.quick", null);

        Assert.Equal(CommandStatus.Failed, result.Status);
    }

    [Fact]
    public void Input_reports_update_the_status_at_once()
    {
        var (session, _) = TestSessions.Open(SyntheticUps.Create());

        Assert.True(session.ProcessInputReport([0x03, SyntheticUps.OnBattery]));
        Assert.Equal("OB DISCHRG", TestSessions.Variables(session)["ups.status"]);
        Assert.False(session.ProcessInputReport([0x03, SyntheticUps.OnBattery]));
        Assert.False(session.ProcessInputReport([0x7F, 0x01])); // a report the descriptor does not declare
    }

    [Fact]
    public void A_device_that_answers_nothing_counts_as_lost()
    {
        FakeHidDevice device = SyntheticUps.Create();
        var (session, _) = TestSessions.Open(device);
        device.FailFeatures = true;

        Assert.Throws<HidDeviceLostException>(session.Update);
    }

    [Fact]
    public void Single_failing_reports_are_skipped()
    {
        FakeHidDevice device = new(SyntheticUps.Info(), SyntheticUps.Descriptor, [[0x01, 50], [0x03, SyntheticUps.OnBattery]]);
        var (session, _) = TestSessions.Open(device);

        session.Update();
        IReadOnlyDictionary<string, string> vars = TestSessions.Variables(session);

        Assert.Equal("50", vars["battery.charge"]);
        Assert.Equal("OB DISCHRG", vars["ups.status"]);
        Assert.DoesNotContain("battery.runtime", vars.Keys);
    }
}
