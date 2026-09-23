using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Net.Apcupsd;
using NutHub.Drivers.Net.Tests.Fakes;

namespace NutHub.Drivers.Net.Tests.Apcupsd;

public sealed class ApcupsdDriverTests
{
    private static ApcupsdDriver CreateDriver(int port) =>
        new("test", new ApcupsdNisClient("127.0.0.1", port, TimeSpan.FromSeconds(2), TimeProvider.System),
            TimeProvider.System, NullLogger<ApcupsdDriver>.Instance)
        {
            ReconnectDelays = [TimeSpan.FromMilliseconds(100)],
        };

    [Fact]
    public async Task Publishes_the_status_of_apcupsd()
    {
        await using var server = new FakeApcupsd(FakeApcupsd.BackUpsStatus);
        server.Start();

        await using var running = RunningDriver.Start(CreateDriver(server.Port));
        DriverReport report = await running.Context.WaitForPublishAsync();

        Assert.Equal("OL", report.Var("ups.status"));
        Assert.Equal("3138", report.Var("battery.runtime"));
        Assert.Equal($"apcupsd 3.14.6 (16 May 2009) redhat at 127.0.0.1:{server.Port}", report.Var("driver.version.data"));
        Assert.Empty(report.Update!.Commands!);
        Assert.Empty(report.Update.VariableInfo!);

        // Polling goes on: one connection and one request per poll.
        int mark = running.Context.Mark();
        await running.Context.WaitForPublishAsync(from: mark);
        Assert.True(server.Requests >= 2);
    }

    [Fact]
    public async Task Status_changes_are_followed_and_commlost_is_reported()
    {
        await using var server = new FakeApcupsd(FakeApcupsd.BackUpsStatus);
        server.Start();

        await using var running = RunningDriver.Start(CreateDriver(server.Port));
        await running.Context.WaitForPublishAsync();

        int mark = running.Context.Mark();
        server.Status = FakeApcupsd.SmartUpsOnBattery;
        await running.Context.WaitForPublishAsync(r => r.Var("ups.status") == "OB LB", mark);

        mark = running.Context.Mark();
        server.Status = FakeApcupsd.BackUpsStatus.Replace("STATUS   : ONLINE", "STATUS   : COMMLOST", StringComparison.Ordinal);
        DriverReport lost = await running.Context.WaitForDisconnectAsync("lost communication with the UPS", mark);
        Assert.Contains("COMMLOST", lost.Text);

        mark = running.Context.Mark();
        server.Status = FakeApcupsd.BackUpsStatus;
        await running.Context.WaitForPublishAsync(r => r.Var("ups.status") == "OL", mark);
    }

    [Fact]
    public async Task Reconnects_after_apcupsd_restarts()
    {
        await using var server = new FakeApcupsd(FakeApcupsd.BackUpsStatus);
        server.Start();
        int port = server.Port;

        await using var running = RunningDriver.Start(CreateDriver(port));
        await running.Context.WaitForPublishAsync();

        int mark = running.Context.Mark();
        server.Stop();
        DriverReport lost = await running.Context.WaitForDisconnectAsync("Cannot read the status", mark);
        Assert.Contains($"127.0.0.1:{port}", lost.Text);

        mark = running.Context.Mark();
        server.Start(port);
        await running.Context.WaitForPublishAsync(from: mark);
    }

    [Fact]
    public async Task Oversized_records_are_rejected()
    {
        await using var server = new FakeApcupsd(FakeApcupsd.BackUpsStatus) { SendOversizedRecord = true };
        server.Start();

        await using var running = RunningDriver.Start(CreateDriver(server.Port));
        DriverReport report = await running.Context.WaitForDisconnectAsync("record of 65535 bytes");

        Assert.DoesNotContain(running.Context.Reports, r => r.IsPublish);
    }

    [Fact]
    public async Task A_server_that_never_answers_times_out()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var driver = new ApcupsdDriver(
            "test", new ApcupsdNisClient("127.0.0.1", port, TimeSpan.FromMilliseconds(300), TimeProvider.System),
            TimeProvider.System, NullLogger<ApcupsdDriver>.Instance);

        await using var running = RunningDriver.Start(driver);
        DriverReport report = await running.Context.WaitForDisconnectAsync("no complete answer");

        Assert.Contains("0.3 s", report.Text);
    }

    [Fact]
    public async Task Commands_and_writes_are_refused()
    {
        ApcupsdDriver driver = CreateDriver(1);

        Assert.Equal(CommandStatus.NotSupported, (await driver.InstantCommandAsync("load.off", null, CancellationToken.None)).Status);
        Assert.Equal(CommandStatus.ReadOnly, (await driver.SetVariableAsync("ups.id", "x", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Discovery_lists_a_local_apcupsd()
    {
        await using var server = new FakeApcupsd(FakeApcupsd.SmartUpsOnBattery);
        server.Start();
        var factory = new ApcupsdDriverFactory { DiscoveryPort = server.Port };

        IReadOnlyList<DiscoveredDevice> devices = await factory.DiscoverAsync(CancellationToken.None);

        DiscoveredDevice device = Assert.Single(devices);
        Assert.Equal($"Smart-UPS 1500 (apcupsd on 127.0.0.1:{server.Port})", device.Title);
        Assert.Equal("Serial AS1234567890, apcupsd 3.14.14 (31 May 2016) debian", device.Detail);
        Assert.Equal("127.0.0.1", device.Options["host"]);
        Assert.Equal(server.Port.ToString(CultureInfo.InvariantCulture), device.Options["port"]);
        Assert.Equal("rack-ups", device.SuggestedName);

        // The discovered options create a working driver.
        IUpsDriver driver = factory.Create(new DriverCreateContext
        {
            UpsName = "rack-ups",
            Options = device.Options,
            PollInterval = TimeSpan.FromMilliseconds(100),
            LoggerFactory = NullLoggerFactory.Instance,
            TimeProvider = TimeProvider.System,
            Services = new ServiceCollection().BuildServiceProvider(),
        });
        await using var running = RunningDriver.Start(driver);
        await running.Context.WaitForPublishAsync(r => r.Var("ups.status") == "OB LB");
    }

    [Fact]
    public async Task Discovery_finds_nothing_quickly_when_apcupsd_is_absent()
    {
        await using var server = new FakeApcupsd(FakeApcupsd.BackUpsStatus);
        server.Start();
        int port = server.Port;
        server.Stop();
        var factory = new ApcupsdDriverFactory { DiscoveryPort = port };

        var watch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<DiscoveredDevice> devices = await factory.DiscoverAsync(CancellationToken.None);

        Assert.Empty(devices);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Discovery took {watch.Elapsed}.");
    }

    [Theory]
    [InlineData("rack ups", "rack-ups")]
    [InlineData("  Server Room #2 ", "server-room-2")]
    [InlineData("___", "apcupsd")]
    [InlineData(null, "apcupsd")]
    [InlineData("ups.main_1", "ups.main_1")]
    public void Suggested_names_are_valid_ups_names(string? upsName, string expected)
    {
        string name = ApcupsdDriverFactory.SuggestName(upsName);

        Assert.Equal(expected, name);
        Assert.True(NutFormat.IsValidUpsName(name));
    }
}
