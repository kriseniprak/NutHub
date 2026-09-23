using NutHub.Core.Model;
using NutHub.Protocol.Tests.Infrastructure;
using Xunit.Abstractions;

namespace NutHub.Protocol.Tests.Server;

public sealed class SetAndInstantCommandTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Set_var_validates_in_upsd_order()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsAsync("admin");

        Assert.Equal("ERR UNKNOWN-UPS", await client.CommandAsync("SET VAR nope ups.id x"));
        Assert.Equal("ERR VAR-NOT-SUPPORTED", await client.CommandAsync("SET VAR ups1 no.such.var 1"));
        Assert.Equal("ERR READONLY", await client.CommandAsync("SET VAR ups1 battery.charge 50"));
        Assert.Equal("ERR TOO-LONG", await client.CommandAsync("SET VAR ups1 ups.id 12345678901234567"));
        Assert.Equal("ERR INVALID-VALUE", await client.CommandAsync("SET VAR ups1 ups.beeper.status loud"));
        Assert.Equal("ERR INVALID-VALUE", await client.CommandAsync("SET VAR ups1 ups.beeper.status Enabled"));
        Assert.Equal("ERR INVALID-VALUE", await client.CommandAsync("SET VAR ups1 battery.charge.low 95"));
        Assert.Equal("ERR INVALID-VALUE", await client.CommandAsync("SET VAR ups1 battery.charge.low abc"));
        Assert.Equal("ERR INVALID-VALUE", await client.CommandAsync("SET VAR ups1 ups.delay.shutdown soon"));
        Assert.Equal("ERR INVALID-ARGUMENT", await client.CommandAsync("SET VAR ups1 ups.id"));
        Assert.Equal("ERR INVALID-ARGUMENT", await client.CommandAsync("SET VAR ups1 ups.id My UPS"));
        Assert.Equal("ERR INVALID-ARGUMENT", await client.CommandAsync("SET FOO ups1"));
        Assert.Equal("ERR INVALID-ARGUMENT", await client.CommandAsync("SET VAR"));
        Assert.Empty(host.Device("ups1").Written);

        Assert.Equal("OK", await client.CommandAsync("SET VAR ups1 ups.id 1234567890123456"));
        Assert.Equal("OK", await client.CommandAsync("SET VAR ups1 ups.beeper.status muted"));
        Assert.Equal("OK", await client.CommandAsync("SET VAR ups1 battery.charge.low 30"));
        Assert.Equal("OK", await client.CommandAsync("SET VAR ups1 ups.delay.shutdown 120"));
        Assert.Equal(
        [
            ("ups.id", "1234567890123456"),
            ("ups.beeper.status", "muted"),
            ("battery.charge.low", "30"),
            ("ups.delay.shutdown", "120"),
        ], host.Device("ups1").Written.ToArray());
    }

    [Fact]
    public async Task Set_var_uses_the_real_variable_name_and_decodes_the_value()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsAsync("admin");

        const string value = "a \"b\" \\ #c";
        Assert.Equal("OK", await client.CommandAsync("SET VAR UPS1 UPS.ID " + NutTestClient.Quote(value)));
        Assert.Equal(("ups.id", value), Assert.Single(host.Device("ups1").Written));

        host.Device("ups1").Publish();
        await host.WaitForUpsAsync("ups1", s => s.Get("ups.id") == value, "the new id");
        Assert.Equal("VAR ups1 ups.id \"a \\\"b\\\" \\\\ \\#c\"", await client.CommandAsync("GET VAR ups1 ups.id"));

        UpsEvent changed = Assert.Single(host.Events, e => e.Type == UpsEventType.VariableChanged);
        Assert.Equal("nut:admin@127.0.0.1", changed.Actor);
    }

    [Fact]
    public async Task Driver_failures_map_to_nut_errors()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsAsync("admin");
        FakeDevice device = host.Device("ups1");

        device.OnSet = (_, _, _) => Task.FromResult(CommandResult.Fail("device said no"));
        Assert.Equal("ERR SET-FAILED", await client.CommandAsync("SET VAR ups1 ups.id x"));

        device.OnCommand = (_, _, _) => Task.FromResult(CommandResult.Fail("device said no"));
        Assert.Equal("ERR INSTCMD-FAILED", await client.CommandAsync("INSTCMD ups1 beeper.enable"));

        device.OnCommand = (_, _, _) => Task.FromResult(CommandResult.InvalidArgument("bad delay"));
        Assert.Equal("ERR INVALID-ARGUMENT", await client.CommandAsync("INSTCMD ups1 load.off.delay -5"));

        device.OnCommand = (_, _, _) => Task.FromResult(CommandResult.NotSupported());
        Assert.Equal("ERR CMD-NOT-SUPPORTED", await client.CommandAsync("INSTCMD ups1 beeper.enable"));

        device.OnCommand = (_, _, _) => throw new InvalidOperationException("driver bug");
        Assert.Equal("ERR INSTCMD-FAILED", await client.CommandAsync("INSTCMD ups1 beeper.enable"));

        // The connection is still fine.
        Assert.Equal("VAR ups1 ups.status \"OL\"", await client.CommandAsync("GET VAR ups1 ups.status"));
    }

    [Fact]
    public async Task Overridden_variables_are_read_only()
    {
        await using var host = await NutTestHost.StartAsync(output, c => c.Ups[0].Overrides["ups.id"] = "fixed");
        await using var client = await host.ConnectAsAsync("admin");
        Assert.Equal("VAR ups1 ups.id \"fixed\"", await client.CommandAsync("GET VAR ups1 ups.id"));
        Assert.Equal("ERR READONLY", await client.CommandAsync("SET VAR ups1 ups.id x"));
    }

    [Fact]
    public async Task Instant_commands_run_with_their_parameter()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsAsync("admin");
        FakeDevice device = host.Device("ups1");

        Assert.Equal("OK", await client.CommandAsync("INSTCMD ups1 beeper.enable"));
        Assert.Equal("OK", await client.CommandAsync("INSTCMD ups1 load.off.delay 120"));
        Assert.Equal("OK", await client.CommandAsync("INSTCMD UPS1 LOAD.OFF.DELAY \"30\""));
        Assert.Equal(
        [
            ("beeper.enable", (string?)null),
            ("load.off.delay", "120"),
            ("load.off.delay", "30"),
        ], device.Executed.ToArray());

        Assert.Equal("ERR CMD-NOT-SUPPORTED", await client.CommandAsync("INSTCMD ups1 no.such.command"));
        Assert.Equal("ERR INVALID-ARGUMENT", await client.CommandAsync("INSTCMD ups1"));
        Assert.Equal("ERR INVALID-ARGUMENT", await client.CommandAsync("INSTCMD ups1 load.off.delay 120 extra"));
        Assert.Equal("ERR UNKNOWN-UPS", await client.CommandAsync("INSTCMD nope beeper.enable"));
        Assert.Equal(3, device.Executed.Count);

        UpsEvent executed = host.Events.First(e => e.Type == UpsEventType.CommandExecuted);
        Assert.Equal("nut:admin@127.0.0.1", executed.Actor);
        Assert.Equal("ups1", executed.Ups);
    }

    [Fact]
    public async Task A_client_leaving_does_not_abort_its_command()
    {
        await using var host = await NutTestHost.StartAsync(output);
        FakeDevice device = host.Device("ups1");
        var release = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken seen = default;
        device.OnCommand = (_, _, token) =>
        {
            seen = token;
            return release.Task;
        };

        await using (var client = await host.ConnectAsAsync("admin"))
        {
            await client.SendAsync("INSTCMD ups1 shutdown.return");
            await NutTestHost.WaitUntilAsync(() => !device.Executed.IsEmpty, "the command to reach the driver");
        }

        // The connection only notices the client left once the command is over; the command is not cancelled.
        await Task.Delay(100);
        Assert.False(seen.IsCancellationRequested);
        release.SetResult(CommandResult.Ok);
        await NutTestHost.WaitUntilAsync(() => host.Events.Any(e => e.Type == UpsEventType.CommandExecuted),
                                         "the command to complete");
        await NutTestHost.WaitUntilAsync(() => host.Sessions.Sessions.Count == 0, "the connection to close");
    }

    [Fact]
    public async Task An_administrator_disconnect_does_not_wait_for_a_running_command()
    {
        await using var host = await NutTestHost.StartAsync(output);
        FakeDevice device = host.Device("ups1");
        var release = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.OnCommand = (_, _, _) => release.Task;

        await using var client = await host.ConnectAsAsync("admin");
        await client.SendAsync("INSTCMD ups1 shutdown.return");
        await NutTestHost.WaitUntilAsync(() => !device.Executed.IsEmpty, "the command to reach the driver");

        Assert.True(host.Sessions.Disconnect(host.Sessions.Sessions.Single().Id));
        Assert.True(await client.WaitForCloseAsync());
        release.SetResult(CommandResult.Ok);
        await NutTestHost.WaitUntilAsync(() => host.Events.Any(e => e.Type == UpsEventType.CommandExecuted),
                                         "the command to complete anyway");
    }
}
