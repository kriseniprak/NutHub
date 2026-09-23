using NutHub.Core.Model;
using NutHub.Protocol.Tests.Infrastructure;
using Xunit.Abstractions;

namespace NutHub.Protocol.Tests.Server;

/// <summary>DATA-STALE and DRIVER-NOT-CONNECTED on exactly the requests upsd checks with ups_available().</summary>
public sealed class DataAvailabilityTests(ITestOutputHelper output)
{
    private static readonly string[] CheckedReads =
    [
        "GET VAR ups1 ups.status",
        "GET TYPE ups1 ups.id",
        "GET DESC ups1 ups.id",
        "GET CMDDESC ups1 beeper.enable",
        "GET NUMLOGINS ups1",
        "LIST VAR ups1",
        "LIST RW ups1",
        "LIST CMD ups1",
        "LIST ENUM ups1 ups.beeper.status",
        "LIST RANGE ups1 battery.charge.low",
    ];

    [Fact]
    public async Task Stale_data_is_refused_where_upsd_refuses_it()
    {
        await using var host = await NutTestHost.StartAsync(output);
        host.Device("ups1").Disconnect();
        await host.WaitForUpsAsync("ups1", s => s.Availability == DataAvailability.Stale, "stale data");

        await using var reader = await host.ConnectAsync();
        foreach (string request in CheckedReads)
        {
            Assert.Equal("ERR DATA-STALE", await reader.CommandAsync(request));
        }

        await using var admin = await host.ConnectAsAsync("admin");
        Assert.Equal("ERR DATA-STALE", await admin.CommandAsync("INSTCMD ups1 beeper.enable"));
        Assert.Equal("ERR DATA-STALE", await admin.CommandAsync("SET VAR ups1 ups.id x"));
        Assert.Empty(host.Device("ups1").Executed);

        // Not checked by upsd: the UPS list, its description, the client list, LOGIN, PRIMARY and FSD.
        Assert.Equal("UPSDESC ups1 \"Rack \\\"A\\\" \\#1 \\\\ main\"", await reader.CommandAsync("GET UPSDESC ups1"));
        Assert.Equal("END LIST UPS", (await reader.ListAsync("LIST UPS"))[^1]);
        Assert.Equal(["BEGIN LIST CLIENT ups1", "END LIST CLIENT ups1"], await reader.ListAsync("LIST CLIENT ups1"));

        await using var primary = await host.ConnectAsAsync("monprimary");
        Assert.Equal("OK", await primary.CommandAsync("LOGIN ups1"));
        Assert.Equal("OK PRIMARY-GRANTED", await primary.CommandAsync("PRIMARY ups1"));
        Assert.Equal("OK FSD-SET", await primary.CommandAsync("FSD ups1"));

        // Other UPSes are not affected.
        Assert.Equal("VAR ups2 ups.status \"OL\"", await reader.CommandAsync("GET VAR ups2 ups.status"));

        // Fresh data again.
        host.Device("ups1").Publish();
        await host.WaitForUpsAsync("ups1", s => s.IsAvailable, "fresh data");
        Assert.Equal("VAR ups1 ups.status \"FSD OL\"", await reader.CommandAsync("GET VAR ups1 ups.status"));
    }

    [Fact]
    public async Task Data_older_than_maxage_is_stale()
    {
        await using var host = await NutTestHost.StartAsync(output, c => c.Nut.MaxAgeSeconds = 15);
        await using var client = await host.ConnectAsync();
        Assert.Equal("VAR ups1 ups.status \"OL\"", await client.CommandAsync("GET VAR ups1 ups.status"));

        host.Time.Advance(TimeSpan.FromSeconds(10));
        host.Device("ups2").Publish(); // ups2 keeps publishing, ups1 goes silent
        await host.WaitForUpsAsync("ups2", s => s.LastUpdate == host.Time.GetUtcNow(), "ups2 to publish");
        host.Time.Advance(TimeSpan.FromSeconds(6));
        await host.WaitForUpsAsync("ups1", s => s.Availability == DataAvailability.Stale, "ups1 to age");

        Assert.Equal("ERR DATA-STALE", await client.CommandAsync("GET VAR ups1 ups.status"));
        Assert.Equal("VAR ups2 ups.status \"OL\"", await client.CommandAsync("GET VAR ups2 ups.status"));

        host.Device("ups1").Publish();
        await host.WaitForUpsAsync("ups1", s => s.IsAvailable, "fresh data");
        Assert.Equal("VAR ups1 ups.status \"OL\"", await client.CommandAsync("GET VAR ups1 ups.status"));
    }

    [Fact]
    public async Task A_ups_without_a_running_driver_is_not_connected()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();

        Assert.Equal("ERR DRIVER-NOT-CONNECTED", await client.CommandAsync("GET VAR off ups.status"));
        Assert.Equal(["ERR DRIVER-NOT-CONNECTED"], await client.ListAsync("LIST VAR off"));
        Assert.Equal("ERR DRIVER-NOT-CONNECTED", await client.CommandAsync("GET NUMLOGINS off"));
        Assert.Equal("UPSDESC off \"Unavailable\"", await client.CommandAsync("GET UPSDESC off"));

        await using var admin = await host.ConnectAsAsync("admin");
        Assert.Equal("ERR DRIVER-NOT-CONNECTED", await admin.CommandAsync("INSTCMD off beeper.enable"));
        Assert.Equal("ERR DRIVER-NOT-CONNECTED", await admin.CommandAsync("SET VAR off ups.id x"));
    }
}
