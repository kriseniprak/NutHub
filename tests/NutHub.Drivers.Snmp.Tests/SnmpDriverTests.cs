using Lextm.SharpSnmpLib;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Snmp.Tests.Fakes;
using NutHub.Drivers.Snmp.Transport;
using static NutHub.Drivers.Snmp.Tests.Fakes.Profiles;

namespace NutHub.Drivers.Snmp.Tests;

/// <summary>The whole driver against the UDP fake agent: polling, status changes, commands, reconnection.</summary>
public sealed class SnmpDriverTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    private static SnmpSettings FastSettings(FakeSnmpAgent agent) =>
        Settings(agent.Port) with
        {
            Timeout = TimeSpan.FromMilliseconds(150),
            Retries = 1,
            ReconnectDelays = [TimeSpan.FromMilliseconds(50)],
        };

    private sealed class Running : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;

        public Running(SnmpSettings settings)
        {
            Driver = new SnmpDriver(settings, NullLogger.Instance);
            _run = Driver.RunAsync(Context, _cts.Token);
        }

        public SnmpDriver Driver { get; }

        public TestDriverContext Context { get; } = new(Poll);

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _run);
            await Driver.DisposeAsync();
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task Publishes_every_poll_and_follows_a_power_failure()
    {
        await using var agent = new FakeSnmpAgent(ApcUps());
        await using var run = new Running(FastSettings(agent));

        DriverUpdate first = await run.Context.NextUpdateAsync(u => true, "a first update");
        Assert.Equal("OL", first.Var("ups.status"));
        Assert.Equal("apcc", run.Driver.CurrentMib);
        Assert.Contains("load.off", first.Commands!);

        agent.Store.Set(Apc + "4.1.1.0", 3); // onBattery
        agent.Store.Set(Apc + "2.1.1.0", 3); // batteryLow
        DriverUpdate onBattery = await run.Context.NextUpdateAsync(u => u.Var("ups.status") == "OB LB", "on battery");
        Assert.Equal("Smart-UPS 1500", onBattery.Var("ups.model"));

        agent.Store.Set(Apc + "4.1.1.0", 2);
        agent.Store.Set(Apc + "2.1.1.0", 2);
        await run.Context.NextUpdateAsync(u => u.Var("ups.status") == "OL", "back on line");
    }

    [Fact]
    public async Task Commands_and_writes_go_to_the_agent()
    {
        await using var agent = new FakeSnmpAgent(ApcUps());
        await using var run = new Running(FastSettings(agent));
        await run.Context.NextUpdateAsync(u => true, "a first update");

        CommandResult command = await run.Driver.InstantCommandAsync("test.battery.start", null, CancellationToken.None);
        Assert.True(command.IsSuccess, command.ToString());
        Assert.Equal(new Integer32(2), agent.Store.SetValue(Apc + "7.2.2.0"));

        CommandResult write = await run.Driver.SetVariableAsync("input.transfer.high", "260", CancellationToken.None);
        Assert.True(write.IsSuccess, write.ToString());
        await run.Context.NextUpdateAsync(u => u.Var("input.transfer.high") == "260", "the written value");
    }

    [Fact]
    public async Task Lost_agent_is_reported_and_picked_up_again()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        await using var run = new Running(FastSettings(agent));
        await run.Context.NextUpdateAsync(u => true, "a first update");

        agent.Silent = true;
        await run.Context.WaitForAsync(c => c.Disconnections.Count > 0, "the disconnection");
        Assert.Equal(
            $"No answer from 127.0.0.1:{agent.Port} (check address, community / credentials, and that SNMP is enabled on the card)",
            run.Context.Disconnections[0]);
        Assert.Null(run.Driver.CurrentMib);
        Assert.Equal(CommandStatus.DriverNotConnected,
                     (await run.Driver.InstantCommandAsync("load.off", null, CancellationToken.None)).Status);

        agent.Silent = false;
        agent.Store.Set(Ietf + "4.1.0", 5); // it came back on battery
        await run.Context.NextUpdateAsync(u => u.Var("ups.status") == "OB", "the reconnection");
        Assert.Single(run.Context.Disconnections); // reported once, not at every attempt
    }

    [Fact]
    public async Task V1_agent_with_missing_objects_is_read_in_batches()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        await using var run = new Running(FastSettings(agent) with { Version = SnmpProtocolVersion.V1 });

        DriverUpdate first = await run.Context.NextUpdateAsync(u => true, "a first update");
        Assert.Equal("13.6", first.Var("battery.voltage"));
        agent.Store.ClearHistory();
        await run.Context.NextUpdateAsync(u => true, "a regular poll");

        // Objects missing at connection time are not asked again at every poll: whole batches go through instead
        // of shrinking by one noSuchName at a time.
        Assert.True(agent.Store.RequestSizes.Average() > 4, string.Join(",", agent.Store.RequestSizes));
    }

    [Fact]
    public async Task Wrong_v3_password_is_reported_distinctly()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        SnmpSettings settings = FastSettings(agent) with
        {
            Version = SnmpProtocolVersion.V3,
            SecurityName = "nut",
            SecurityLevel = SnmpSecurityLevel.AuthNoPriv,
            AuthProtocol = SnmpAuthProtocol.Sha256,
            AuthPassword = "correct-password",
        };
        agent.AddUser("nut", SnmpSecurity.CreatePrivacyProvider(settings));

        await using var run = new Running(settings with { AuthPassword = "incorrect-password" });
        await run.Context.WaitForAsync(c => c.Disconnections.Count > 0, "the failure");
        Assert.Contains("wrong authentication password", run.Context.Disconnections[0]);
    }

    [Fact]
    public async Task V3_driver_survives_an_agent_reboot()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        SnmpSettings settings = FastSettings(agent) with
        {
            Version = SnmpProtocolVersion.V3,
            SecurityName = "nut",
            SecurityLevel = SnmpSecurityLevel.AuthPriv,
            AuthProtocol = SnmpAuthProtocol.Sha1,
            AuthPassword = "authpass1",
            PrivProtocol = SnmpPrivProtocol.Aes128,
            PrivPassword = "privpass1",
        };
        agent.AddUser("nut", SnmpSecurity.CreatePrivacyProvider(settings));
        await using var run = new Running(settings);
        await run.Context.NextUpdateAsync(u => u.Var("ups.status") == "OL", "a first update");

        agent.Reboot();
        agent.Store.Set(Ietf + "4.1.0", 5);
        await run.Context.NextUpdateAsync(u => u.Var("ups.status") == "OB", "an update after the reboot");
        Assert.Empty(run.Context.Disconnections);
    }

    [Fact]
    public async Task Mib_mismatch_is_reported_and_retried()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        await using var run = new Running(FastSettings(agent) with { Mib = "apcc" });
        await run.Context.WaitForAsync(c => c.Disconnections.Count > 0, "the failure");
        Assert.Contains("does not implement the APC PowerNet", run.Context.Disconnections[0]);

        ApcUps(agent.Store); // the card was reconfigured
        await run.Context.NextUpdateAsync(u => u.Var("ups.model") == "Smart-UPS 1500", "the recovery");
    }
}
