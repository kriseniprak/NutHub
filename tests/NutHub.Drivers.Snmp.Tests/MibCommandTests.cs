using Lextm.SharpSnmpLib;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Snmp.Engine;
using NutHub.Drivers.Snmp.Tests.Fakes;
using static NutHub.Drivers.Snmp.Tests.Fakes.Profiles;

namespace NutHub.Drivers.Snmp.Tests;

public sealed class MibCommandTests
{
    [Theory]
    [InlineData("load.off", "6.2.1.0", 2)] // upsAdvControlUpsOff: turnUpsOff
    [InlineData("shutdown.stayoff", "6.2.1.0", 3)] // turnUpsOffGracefully
    [InlineData("shutdown.return", "6.1.1.0", 2)] // upsBasicControlConserveBattery: putUpsToSleep
    [InlineData("shutdown.reboot", "6.2.2.0", 2)] // upsAdvControlRebootShutdown
    [InlineData("test.battery.start", "7.2.2.0", 2)] // upsAdvTestDiagnostics
    [InlineData("calibrate.start", "7.2.5.0", 2)]
    [InlineData("calibrate.stop", "7.2.5.0", 3)]
    [InlineData("load.on", "6.2.6.0", 2)]
    public async Task Apc_commands_write_their_control_object(string command, string oid, int value)
    {
        AgentStore s = ApcUps();
        MibSession session = await OpenAsync(s);

        CommandResult result = await session.ExecuteCommandAsync(command, null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.ToString());
        (string Oid, ISnmpData Value) set = Assert.Single(s.Sets);
        Assert.Equal(Apc + oid, set.Oid);
        Assert.Equal(new Integer32(value), set.Value);
    }

    [Fact]
    public async Task Commands_are_offered_only_when_their_object_exists()
    {
        MibSession session = await OpenAsync(ApcUps());
        DriverUpdate u = await session.PollAsync(CancellationToken.None);

        Assert.Contains("load.off", u.Commands!);
        Assert.Contains("test.battery.start", u.Commands!);
        Assert.DoesNotContain("bypass.start", u.Commands!); // upsAdvControlBypassSwitch missing
        Assert.Equal(CommandStatus.NotSupported,
                     (await session.ExecuteCommandAsync("bypass.start", null, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Fixed_commands_refuse_a_parameter()
    {
        AgentStore s = ApcUps();
        MibSession session = await OpenAsync(s);
        CommandResult result = await session.ExecuteCommandAsync("load.off", "7", CancellationToken.None);
        Assert.Equal(CommandStatus.InvalidArgument, result.Status);
        Assert.Empty(s.Sets);
    }

    [Theory]
    [InlineData("test.battery.start.quick", "1.3.6.1.2.1.33.1.7.7.4")]
    [InlineData("test.battery.start.deep", "1.3.6.1.2.1.33.1.7.7.5")]
    [InlineData("test.battery.stop", "1.3.6.1.2.1.33.1.7.7.2")]
    public async Task Ietf_tests_write_the_test_identifier(string command, string testId)
    {
        AgentStore s = IetfUps();
        MibSession session = await OpenAsync(s);
        Assert.True((await session.ExecuteCommandAsync(command, null, CancellationToken.None)).IsSuccess);
        Assert.Equal(new ObjectIdentifier(testId), s.SetValue(Ietf + "7.1.0"));
    }

    [Fact]
    public async Task Ietf_delayed_commands_use_the_parameter_or_the_delay_setting()
    {
        AgentStore s = IetfUps();
        MibSession session = await OpenAsync(s);
        await session.PollAsync(CancellationToken.None);

        Assert.True((await session.ExecuteCommandAsync("load.off.delay", "45", CancellationToken.None)).IsSuccess);
        Assert.Equal(new Integer32(45), s.SetValue(Ietf + "8.2.0"));

        Assert.True((await session.ExecuteCommandAsync("load.off.delay", null, CancellationToken.None)).IsSuccess);
        Assert.Equal(new Integer32(20), s.SetValue(Ietf + "8.2.0")); // ups.delay.shutdown default

        Assert.True((await session.SetVariableAsync("ups.delay.start", "90", CancellationToken.None)).IsSuccess);
        Assert.True((await session.ExecuteCommandAsync("load.on.delay", null, CancellationToken.None)).IsSuccess);
        Assert.Equal(new Integer32(90), s.SetValue(Ietf + "8.3.0"));

        Assert.True((await session.ExecuteCommandAsync("shutdown.stop", null, CancellationToken.None)).IsSuccess);
        Assert.Equal(new Integer32(-1), s.SetValue(Ietf + "8.2.0"));

        Assert.Equal(CommandStatus.InvalidArgument,
                     (await session.ExecuteCommandAsync("load.off.delay", "soon", CancellationToken.None)).Status);
        Assert.Equal(CommandStatus.InvalidValue,
                     (await session.SetVariableAsync("ups.delay.shutdown", "-5", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Ietf_shutdown_return_sets_auto_restart_then_starts_the_countdown()
    {
        AgentStore s = IetfUps();
        MibSession session = await OpenAsync(s);
        DriverUpdate u = await session.PollAsync(CancellationToken.None);
        Assert.Contains("shutdown.return", u.Commands!);
        Assert.Contains("shutdown.stayoff", u.Commands!);

        Assert.True((await session.ExecuteCommandAsync("shutdown.stayoff", null, CancellationToken.None)).IsSuccess);
        Assert.Equal(
            [(Ietf + "8.5.0", (ISnmpData)new Integer32(2)), (Ietf + "8.2.0", new Integer32(20))],
            s.Sets.ToArray());
        s.ClearHistory();

        Assert.True((await session.ExecuteCommandAsync("shutdown.return", "60", CancellationToken.None)).IsSuccess);
        Assert.Equal(
            [(Ietf + "8.5.0", (ISnmpData)new Integer32(1)), (Ietf + "8.2.0", new Integer32(60))],
            s.Sets.ToArray());
    }

    [Fact]
    public async Task A_refused_step_stops_a_composite_command()
    {
        AgentStore s = IetfUps();
        s.MakeReadOnly(Ietf + "8.5.0");
        MibSession session = await OpenAsync(s);

        CommandResult result = await session.ExecuteCommandAsync("shutdown.return", null, CancellationToken.None);

        Assert.Equal(CommandStatus.Failed, result.Status);
        Assert.Contains("notWritable", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(s.Sets); // the countdown was not started
    }

    [Fact]
    public async Task Eaton_commands()
    {
        const string pw = "1.3.6.1.4.1.534.1.";
        AgentStore s = new AgentStore().SystemGroup("1.3.6.1.4.1.534.1");
        s.Set(pw + "1.2.0", "9PX");
        s.Set(pw + "8.1.0", 0);
        s.Set(pw + "9.1.0", -1);
        s.Set(pw + "9.2.0", -1);
        MibSession session = await OpenAsync(s);

        Assert.True((await session.ExecuteCommandAsync("test.battery.start.quick", null, CancellationToken.None)).IsSuccess);
        Assert.Equal(new Integer32(1), s.SetValue(pw + "8.1.0")); // xupsTestBattery: startTest
        Assert.True((await session.ExecuteCommandAsync("load.off.delay", "120", CancellationToken.None)).IsSuccess);
        Assert.Equal(new Integer32(120), s.SetValue(pw + "9.1.0")); // xupsControlOutputOffDelay
    }

    [Fact]
    public async Task Variable_writes_use_the_object_type()
    {
        AgentStore s = ApcUps();
        MibSession session = await OpenAsync(s);
        await session.PollAsync(CancellationToken.None);

        Assert.True((await session.SetVariableAsync("input.transfer.low", "190", CancellationToken.None)).IsSuccess);
        Assert.Equal(new Integer32(190), s.SetValue(Apc + "5.2.3.0"));

        Assert.True((await session.SetVariableAsync("input.sensitivity", "medium", CancellationToken.None)).IsSuccess);
        Assert.Equal(new Integer32(3), s.SetValue(Apc + "5.2.7.0"));

        Assert.True((await session.SetVariableAsync("ups.delay.shutdown", "180", CancellationToken.None)).IsSuccess);
        Assert.Equal(new TimeTicks(18000), s.SetValue(Apc + "5.2.10.0")); // seconds sent in hundredths

        Assert.True((await session.SetVariableAsync("ups.id", "RACK1", CancellationToken.None)).IsSuccess);
        Assert.Equal(new OctetString("RACK1"), s.SetValue(Apc + "1.1.2.0"));

        Assert.Equal(CommandStatus.InvalidValue,
                     (await session.SetVariableAsync("input.sensitivity", "extreme", CancellationToken.None)).Status);
        Assert.Equal(CommandStatus.ReadOnly,
                     (await session.SetVariableAsync("ups.model", "x", CancellationToken.None)).Status);

        DriverUpdate u = await session.PollAsync(CancellationToken.None);
        Assert.Equal("180", u.Var("ups.delay.shutdown"));
        Assert.Equal("medium", u.Var("input.sensitivity"));
    }

    [Fact]
    public async Task Ietf_auto_restart_is_written_through_its_lookup()
    {
        AgentStore s = IetfUps();
        MibSession session = await OpenAsync(s);
        Assert.True((await session.SetVariableAsync("ups.start.auto", "no", CancellationToken.None)).IsSuccess);
        Assert.Equal(new Integer32(2), s.SetValue(Ietf + "8.5.0"));
    }

    [Fact]
    public async Task A_refused_write_explains_the_community()
    {
        AgentStore s = ApcUps();
        s.RefuseSets = true;
        MibSession session = await OpenAsync(s);
        CommandResult result = await session.ExecuteCommandAsync("load.off", null, CancellationToken.None);
        Assert.Equal(CommandStatus.Failed, result.Status);
        Assert.Contains("write community", result.Message);
    }
}
