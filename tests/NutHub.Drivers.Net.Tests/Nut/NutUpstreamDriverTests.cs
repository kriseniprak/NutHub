using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Net.Nut;
using NutHub.Drivers.Net.Tests.Fakes;
using static NutHub.Drivers.Net.Tests.Fakes.TestSupport;

namespace NutHub.Drivers.Net.Tests.Nut;

public sealed class NutUpstreamDriverTests
{
    [Fact]
    public async Task Publishes_the_remote_variables_verbatim_with_the_upstream_version()
    {
        await using var server = new FakeUpsd();
        server.SetDefaultUps();
        server.Vars["ups.status"] = "FSD OB LB";
        server.Start();

        await using var running = RunningDriver.Start(CreateNutDriver(server));
        DriverReport report = await running.Context.WaitForPublishAsync();

        Assert.Equal("FSD OB LB", report.Var("ups.status"));
        Assert.Equal("100", report.Var("battery.charge"));
        Assert.Equal("Rack \"A\" \\ #1", report.Var("ups.id"));
        Assert.Equal($"upstream 127.0.0.1:{server.Port} Network UPS Tools upsd 2.8.1", report.Var("driver.version.data"));
        Assert.DoesNotContain("driver.name", report.Update!.Variables.Keys);
        Assert.DoesNotContain("driver.version.internal", report.Update.Variables.Keys);
        Assert.Contains(running.Context.Reports, r => r.Kind == "connecting");
    }

    [Fact]
    public async Task Describes_writable_variables_from_type_enum_and_range()
    {
        await using var server = new FakeUpsd();
        server.SetDefaultUps();
        server.Start();

        await using var running = RunningDriver.Start(CreateNutDriver(server));
        DriverReport report = await running.Context.WaitForPublishAsync();
        IReadOnlyDictionary<string, VariableInfo> info = report.Update!.VariableInfo!;

        VariableInfo transfer = info["input.transfer.low"];
        Assert.True(transfer.Writable);
        Assert.Equal(VariableType.Number, transfer.Type);
        Assert.Equal([new ValueRange("90", "100"), new ValueRange("102", "105")], transfer.Ranges);

        VariableInfo beeper = info["ups.beeper.status"];
        Assert.Equal(["enabled", "disabled", "muted"], beeper.EnumValues);
        Assert.Equal(VariableType.String, beeper.Type);

        VariableInfo id = info["ups.id"];
        Assert.Equal(VariableType.String, id.Type);
        Assert.Equal(32, id.MaxLength);
        Assert.Equal("RW STRING:32", id.ToNutTypeWords());

        Assert.Equal(["beeper.disable", "load.off.delay", "test.battery.start.quick"], report.Update.Commands!.ToArray());
    }

    [Fact]
    public async Task Metadata_is_cached_and_only_new_writable_variables_are_described()
    {
        await using var server = new FakeUpsd();
        server.SetDefaultUps();
        server.Start();
        var driver = new NutUpstreamDriver(
            "test", NutUpstreamDriverFactory.ReadSettings(new DriverOptionReader(NutOptions(server))),
            TimeProvider.System, NullLogger<NutUpstreamDriver>.Instance)
        {
            MetadataRefreshInterval = TimeSpan.FromMilliseconds(150),
        };

        await using var running = RunningDriver.Start(driver);
        await running.Context.WaitForPublishAsync();
        server.Vars["ups.delay.shutdown"] = "20";
        server.Rw["ups.delay.shutdown"] = new FakeRw("RW NUMBER");

        DriverReport report = await running.Context.WaitForPublishAsync(r => r.Update!.VariableInfo!.ContainsKey("ups.delay.shutdown"));

        Assert.Equal(VariableType.Number, report.Update!.VariableInfo!["ups.delay.shutdown"].Type);
        Assert.Single(server.Received, l => l == "GET TYPE myups ups.id");
        Assert.Single(server.Received, l => l == "GET TYPE myups ups.delay.shutdown");
        Assert.True(server.Received.Count(l => l == "LIST RW myups") >= 2);
    }

    [Theory]
    [InlineData("DATA-STALE", "reports stale data")]
    [InlineData("DRIVER-NOT-CONNECTED", "is not connected to the UPS")]
    [InlineData("ACCESS-DENIED", "does not allow this machine")]
    public async Task Upstream_errors_are_reported_and_polling_continues_on_the_same_connection(string error, string expected)
    {
        await using var server = new FakeUpsd();
        server.SetDefaultUps();
        server.Start();

        await using var running = RunningDriver.Start(CreateNutDriver(server));
        await running.Context.WaitForPublishAsync();

        int mark = running.Context.Mark();
        server.ReadError = error;
        DriverReport lost = await running.Context.WaitForDisconnectAsync(expected, mark);
        Assert.Contains("ERR " + error, lost.Text);

        mark = running.Context.Mark();
        server.ReadError = null;
        await running.Context.WaitForPublishAsync(from: mark);

        Assert.Equal(1, server.ConnectionCount);
        // The same reason is reported once, not at every poll.
        Assert.Single(running.Context.Reports, r => r.IsDisconnect);
    }

    [Fact]
    public async Task Unknown_remote_ups_is_reported_clearly()
    {
        await using var server = new FakeUpsd();
        server.SetDefaultUps();
        server.UpsName = "other";
        server.Start();

        await using var running = RunningDriver.Start(CreateNutDriver(server));
        DriverReport report = await running.Context.WaitForDisconnectAsync("ERR UNKNOWN-UPS");

        Assert.Contains("has no UPS named 'myups'", report.Text);
    }

    [Fact]
    public async Task A_connection_dropped_in_the_middle_of_a_list_is_reopened()
    {
        await using var server = new FakeUpsd();
        server.SetDefaultUps();
        server.Start();

        await using var running = RunningDriver.Start(CreateNutDriver(server));
        await running.Context.WaitForPublishAsync();

        int mark = running.Context.Mark();
        server.DropDuringNextListVar = true;
        DriverReport lost = await running.Context.WaitForDisconnectAsync("Lost the connection", mark);
        Assert.Contains("closed the connection", lost.Text);

        // Wait from here, not from the mark: a poll already under way when the flag was set publishes on the old
        // connection, and that report would otherwise pass for the one that proves the driver came back.
        DriverReport back = await running.Context.WaitForPublishAsync(from: running.Context.Mark());
        Assert.Equal("OL CHRG", back.Var("ups.status"));
        Assert.True(server.ConnectionCount >= 2);
    }

    [Fact]
    public async Task An_unexpected_line_inside_a_list_resynchronises_by_reconnecting()
    {
        await using var server = new FakeUpsd();
        server.SetDefaultUps();
        server.Start();

        await using var running = RunningDriver.Start(CreateNutDriver(server));
        await running.Context.WaitForPublishAsync();

        int mark = running.Context.Mark();
        server.GarbageInNextListVar = true;
        DriverReport lost = await running.Context.WaitForDisconnectAsync("unexpected line", mark);
        Assert.Contains("HELLO THERE", lost.Text);
        await running.Context.WaitForPublishAsync(from: running.Context.Mark());
        Assert.True(server.ConnectionCount >= 2);
    }

    [Fact]
    public async Task Reconnects_after_the_upstream_server_restarts()
    {
        await using var server = new FakeUpsd();
        server.SetDefaultUps();
        server.Start();
        int port = server.Port;

        await using var running = RunningDriver.Start(CreateNutDriver(server));
        await running.Context.WaitForPublishAsync();

        int mark = running.Context.Mark();
        server.Stop();
        await running.Context.WaitForDisconnectAsync(from: mark);

        // Let a few connection attempts fail while the server is down.
        await Task.Delay(300);
        mark = running.Context.Mark();
        server.Vars["battery.charge"] = "97";
        server.Start(port);

        DriverReport back = await running.Context.WaitForPublishAsync(r => r.Var("battery.charge") == "97", mark);
        Assert.Equal("OL CHRG", back.Var("ups.status"));
    }

    [Fact]
    public async Task Server_not_running_is_reported_without_throwing()
    {
        await using var server = new FakeUpsd();
        server.Start();
        int port = server.Port;
        server.Stop();

        await using var running = RunningDriver.Start(CreateNutDriver(server));
        DriverReport report = await running.Context.WaitForDisconnectAsync("Cannot connect");

        Assert.Contains($"127.0.0.1:{port}", report.Text);
    }

    [Fact]
    public async Task Starttls_with_a_self_signed_certificate_and_no_verification()
    {
        using var certificate = CreateServerCertificate();
        await using var server = new FakeUpsd(certificate);
        server.SetDefaultUps();
        server.Start();

        await using var running = RunningDriver.Start(CreateNutDriver(server, ("useTls", "true"), ("tlsVerify", "none")));
        DriverReport report = await running.Context.WaitForPublishAsync();

        Assert.Equal("OL CHRG", report.Var("ups.status"));
        Assert.Equal(1, server.TlsSessions);
        Assert.Equal("STARTTLS", server.Received.First());
    }

    [Fact]
    public async Task Starttls_with_chain_verification_rejects_a_self_signed_certificate()
    {
        using var certificate = CreateServerCertificate();
        await using var server = new FakeUpsd(certificate);
        server.SetDefaultUps();
        server.Start();

        await using var running = RunningDriver.Start(
            CreateNutDriver(server, [("useTls", "true"), ("tlsVerify", "chain"), .. Credentials]));
        DriverReport report = await running.Context.WaitForDisconnectAsync("TLS");

        Assert.Contains("Cannot connect", report.Text);
        Assert.DoesNotContain(running.Context.Reports, r => r.IsPublish);
        Assert.DoesNotContain(server.Received, l => l.StartsWith("PASSWORD", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Starttls_refused_by_the_server_never_falls_back_to_clear_text()
    {
        await using var server = new FakeUpsd(certificate: null);
        server.SetDefaultUps();
        server.Start();

        await using var running = RunningDriver.Start(CreateNutDriver(server, [("useTls", "true"), .. Credentials]));
        DriverReport report = await running.Context.WaitForDisconnectAsync("STARTTLS");

        Assert.Contains("FEATURE-NOT-CONFIGURED", report.Text);
        Assert.DoesNotContain(server.Received, l => l.StartsWith("USERNAME", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Received, l => l.StartsWith("LIST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_is_sent_when_enabled()
    {
        await using var server = new FakeUpsd();
        server.SetDefaultUps();
        server.Start();

        await using (var running = RunningDriver.Start(CreateNutDriver(server, [("login", "true"), .. Credentials])))
        {
            await running.Context.WaitForPublishAsync();
            Assert.Equal(1, server.Logins);
            Assert.Contains("LOGIN myups", server.Received);
        }

        // Stopping the driver says goodbye to the upstream server.
        Assert.Contains("LOGOUT", server.Received);
    }
}
