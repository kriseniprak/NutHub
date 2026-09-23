using NutHub.Core.Model;
using NutHub.Drivers.Net.Tests.Fakes;
using static NutHub.Drivers.Net.Tests.Fakes.TestSupport;

namespace NutHub.Drivers.Net.Tests.Nut;

public sealed class NutUpstreamCommandTests
{
    private static async Task<(FakeUpsd Server, RunningDriver Running, NutHub.Drivers.Net.Nut.NutUpstreamDriver Driver)> StartAsync(
        Action<FakeUpsd>? configure = null, params (string Key, string Value)[] options)
    {
        var server = new FakeUpsd();
        server.SetDefaultUps();
        configure?.Invoke(server);
        server.Start();
        var driver = CreateNutDriver(server, options);
        var running = RunningDriver.Start(driver);
        await running.Context.WaitForPublishAsync();
        return (server, running, driver);
    }

    [Fact]
    public async Task Instant_command_with_parameter_is_forwarded_after_authentication_and_tracked()
    {
        var (server, running, driver) = await StartAsync(null, Credentials);
        await using (server)
        await using (running)
        {
            CommandResult result = await driver.InstantCommandAsync("load.off.delay", "120", CancellationToken.None);

            Assert.Equal(CommandStatus.Success, result.Status);
            Assert.Contains("INSTCMD myups load.off.delay 120", server.Executed);
            string[] received = server.Received.ToArray();
            Assert.True(Array.IndexOf(received, "USERNAME admin") < Array.IndexOf(received, "INSTCMD myups load.off.delay 120"));
            Assert.Contains("PASSWORD secret", received);
            Assert.Contains(received, l => l.StartsWith("GET TRACKING ", StringComparison.Ordinal));
            Assert.Equal(1, server.ConnectionCount);
        }
    }

    [Fact]
    public async Task Tracked_failure_is_reported_as_failed()
    {
        var (server, running, driver) = await StartAsync(s => s.TrackingOutcome = "ERR FAILED", Credentials);
        await using (server)
        await using (running)
        {
            CommandResult result = await driver.InstantCommandAsync("test.battery.start.quick", null, CancellationToken.None);

            Assert.Equal(CommandStatus.Failed, result.Status);
            Assert.Contains("ERR FAILED", result.Message);
        }
    }

    [Fact]
    public async Task Servers_without_tracking_answer_ok_directly()
    {
        var (server, running, driver) = await StartAsync(s => s.TrackingSupported = false, Credentials);
        await using (server)
        await using (running)
        {
            CommandResult result = await driver.InstantCommandAsync("beeper.disable", null, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Contains("INSTCMD myups beeper.disable", server.Executed);
            Assert.DoesNotContain(server.Received, l => l.StartsWith("GET TRACKING", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("CMD-NOT-SUPPORTED", CommandStatus.NotSupported)]
    [InlineData("INSTCMD-FAILED", CommandStatus.Failed)]
    [InlineData("INVALID-ARGUMENT", CommandStatus.InvalidArgument)]
    [InlineData("DRIVER-NOT-CONNECTED", CommandStatus.DriverNotConnected)]
    [InlineData("ACCESS-DENIED", CommandStatus.AccessDenied)]
    public async Task Remote_errors_map_to_command_statuses(string error, CommandStatus expected)
    {
        var (server, running, driver) = await StartAsync(s => s.CommandErrors["load.off.delay"] = error, Credentials);
        await using (server)
        await using (running)
        {
            CommandResult result = await driver.InstantCommandAsync("load.off.delay", "30", CancellationToken.None);

            Assert.Equal(expected, result.Status);
            Assert.Contains("ERR " + error, result.Message);
        }
    }

    [Fact]
    public async Task Unknown_command_is_not_supported()
    {
        var (server, running, driver) = await StartAsync(null, Credentials);
        await using (server)
        await using (running)
        {
            CommandResult result = await driver.InstantCommandAsync("calibrate.start", null, CancellationToken.None);
            Assert.Equal(CommandStatus.NotSupported, result.Status);
        }
    }

    [Fact]
    public async Task Wrong_password_is_access_denied()
    {
        var (server, running, driver) = await StartAsync(null, ("username", "admin"), ("password", "wrong"));
        await using (server)
        await using (running)
        {
            CommandResult result = await driver.InstantCommandAsync("beeper.disable", null, CancellationToken.None);

            Assert.Equal(CommandStatus.AccessDenied, result.Status);
            Assert.Empty(server.Executed);
        }
    }

    [Fact]
    public async Task Without_credentials_commands_are_refused_without_asking_the_server()
    {
        var (server, running, driver) = await StartAsync();
        await using (server)
        await using (running)
        {
            CommandResult result = await driver.InstantCommandAsync("beeper.disable", null, CancellationToken.None);

            Assert.Equal(CommandStatus.AccessDenied, result.Status);
            Assert.Contains("username and a password", result.Message);
            Assert.DoesNotContain(server.Received, l => l.StartsWith("INSTCMD", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Set_variable_quotes_and_escapes_the_value()
    {
        var (server, running, driver) = await StartAsync(null, Credentials);
        await using (server)
        await using (running)
        {
            const string value = "My \"UPS\" \\ #2";
            CommandResult result = await driver.SetVariableAsync("ups.id", value, CancellationToken.None);

            Assert.True(result.IsSuccess, result.ToString());
            Assert.Contains("SET VAR myups ups.id \"My \\\"UPS\\\" \\\\ \\#2\"", server.Received);
            Assert.Equal(value, server.Vars["ups.id"]);

            // The next poll shows the new value, decoded.
            int mark = running.Context.Mark();
            DriverReport report = await running.Context.WaitForPublishAsync(from: mark);
            Assert.Equal(value, report.Var("ups.id"));
        }
    }

    [Theory]
    [InlineData("ups.model", CommandStatus.ReadOnly)]
    [InlineData("ups.nothing", CommandStatus.NotSupported)]
    public async Task Set_variable_errors_map_to_command_statuses(string name, CommandStatus expected)
    {
        var (server, running, driver) = await StartAsync(null, Credentials);
        await using (server)
        await using (running)
        {
            CommandResult result = await driver.SetVariableAsync(name, "x", CancellationToken.None);
            Assert.Equal(expected, result.Status);
        }
    }

    [Fact]
    public async Task Values_with_line_breaks_are_rejected_before_sending()
    {
        var (server, running, driver) = await StartAsync(null, Credentials);
        await using (server)
        await using (running)
        {
            CommandResult set = await driver.SetVariableAsync("ups.id", "a\nINSTCMD myups load.off", CancellationToken.None);
            CommandResult cmd = await driver.InstantCommandAsync("load.off.delay", "1\r\nLOGOUT", CancellationToken.None);
            CommandResult name = await driver.InstantCommandAsync("load off", null, CancellationToken.None);

            Assert.Equal(CommandStatus.InvalidValue, set.Status);
            Assert.Equal(CommandStatus.InvalidArgument, cmd.Status);
            Assert.Equal(CommandStatus.InvalidArgument, name.Status);
            Assert.Empty(server.Executed);
        }
    }

    [Fact]
    public async Task Commands_while_disconnected_report_not_connected()
    {
        var (server, running, driver) = await StartAsync(null, Credentials);
        await using (running)
        {
            int mark = running.Context.Mark();
            await server.DisposeAsync();
            await running.Context.WaitForDisconnectAsync(from: mark);

            CommandResult result = await driver.InstantCommandAsync("beeper.disable", null, CancellationToken.None);
            Assert.Equal(CommandStatus.DriverNotConnected, result.Status);
        }
    }

    [Fact]
    public async Task Commands_run_concurrently_with_polling()
    {
        var (server, running, driver) = await StartAsync(null, Credentials);
        await using (server)
        await using (running)
        {
            Task<CommandResult>[] commands = Enumerable.Range(0, 10)
                .Select(i => driver.InstantCommandAsync("load.off.delay", i.ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None))
                .ToArray();
            CommandResult[] results = await Task.WhenAll(commands);

            Assert.All(results, r => Assert.True(r.IsSuccess, r.ToString()));
            Assert.Equal(10, server.Executed.Count);

            int mark = running.Context.Mark();
            await running.Context.WaitForPublishAsync(from: mark);
            Assert.Equal(1, server.ConnectionCount);
        }
    }
}
