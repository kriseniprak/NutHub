using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Protocol.Tests.Infrastructure;
using Xunit.Abstractions;

namespace NutHub.Protocol.Tests.Server;

public sealed class LoginTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Logins_are_counted_listed_and_announced()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var first = await host.ConnectAsAsync("monsecondary");
        await using var second = await host.ConnectAsAsync("monprimary");

        Assert.Equal("OK", await first.CommandAsync("LOGIN ups1"));
        Assert.Equal("ERR ALREADY-LOGGED-IN", await first.CommandAsync("LOGIN ups2"));
        Assert.Equal("OK", await second.CommandAsync("LOGIN UPS1"));

        Assert.Equal("NUMLOGINS ups1 2", await first.CommandAsync("GET NUMLOGINS ups1"));
        Assert.Equal("NUMLOGINS ups2 0", await first.CommandAsync("GET NUMLOGINS ups2"));
        Assert.Equal(
        [
            "BEGIN LIST CLIENT Ups1",
            "CLIENT ups1 127.0.0.1",
            "CLIENT ups1 127.0.0.1",
            "END LIST CLIENT Ups1",
        ], await first.ListAsync("LIST CLIENT Ups1"));
        Assert.Equal(["ERR UNKNOWN-UPS"], await first.ListAsync("LIST CLIENT nope"));
        Assert.Equal(2, host.Sessions.GetLoginCount("ups1"));

        UpsEvent[] logins = host.Events.Where(e => e.Type == UpsEventType.NutClientLogin).ToArray();
        Assert.Equal(2, logins.Length);
        Assert.All(logins, e => Assert.Equal("ups1", e.Ups));
        Assert.Contains(logins, e => e.Actor == "nut:monsecondary@127.0.0.1");
        Assert.Contains(logins, e => e.Actor == "nut:monprimary@127.0.0.1");

        // A clean LOGOUT...
        Assert.Equal("OK Goodbye", await first.CommandAsync("LOGOUT"));
        await NutTestHost.WaitUntilAsync(
            () => host.Events.Any(e => e.Type == UpsEventType.NutClientLogout && e.Message.Contains("logged out of ups1") &&
                                       e.Actor == "nut:monsecondary@127.0.0.1"),
            "the logout event");
        Assert.Equal(1, host.Sessions.GetLoginCount("ups1"));
        Assert.Equal("NUMLOGINS ups1 1", await second.CommandAsync("GET NUMLOGINS ups1"));

        // ...and a connection that just drops.
        second.Socket.Close();
        await NutTestHost.WaitUntilAsync(() => host.Sessions.GetLoginCount("ups1") == 0, "the dropped client");
        await NutTestHost.WaitUntilAsync(
            () => host.Events.Any(e => e.Type == UpsEventType.NutClientLogout && e.Message.Contains("disconnected from ups1")),
            "the logout event");
    }

    [Fact]
    public async Task Session_registry_follows_the_connection()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsAsync("monprimary");
        NutSession session = Assert.Single(host.Sessions.Sessions);
        Assert.Null(session.Username);

        Assert.Equal("OK", await client.CommandAsync("LOGIN ups2"));
        Assert.Equal("OK PRIMARY-GRANTED", await client.CommandAsync("PRIMARY ups2"));

        Assert.Equal("monprimary", session.Username);
        Assert.Equal("ups2", session.LoginUps);
        Assert.True(session.Primary);
        Assert.Equal(4, session.Commands);
    }

    [Fact]
    public async Task Fsd_puts_fsd_in_front_of_the_status()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsAsync("monprimary");
        Assert.Equal("OK", await client.CommandAsync("LOGIN ups1"));
        Assert.Equal("OK PRIMARY-GRANTED", await client.CommandAsync("PRIMARY ups1"));
        Assert.Equal("OK FSD-SET", await client.CommandAsync("FSD ups1"));

        Assert.True(host.Registry.Find("ups1")!.Snapshot.ForcedShutdown);
        Assert.Equal("VAR ups1 ups.status \"FSD OL\"", await client.CommandAsync("GET VAR ups1 ups.status"));
        Assert.Contains("VAR ups1 ups.status \"FSD OL\"", await client.ListAsync("LIST VAR ups1"));
        Assert.Equal("VAR ups2 ups.status \"OL\"", await client.CommandAsync("GET VAR ups2 ups.status"));

        UpsEvent fsd = Assert.Single(host.Events, e => e.Type == UpsEventType.ForcedShutdown);
        Assert.Equal("ups1", fsd.Ups);
        Assert.Equal("nut:monprimary@127.0.0.1", fsd.Actor);

        // A second FSD is harmless.
        Assert.Equal("OK FSD-SET", await client.CommandAsync("FSD ups1"));
    }

    [Fact]
    public async Task Fsd_is_allowed_on_a_ups_without_data()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsAsync("fsdonly");
        Assert.Equal("OK FSD-SET", await client.CommandAsync("FSD off"));
        Assert.True(host.Registry.Find("off")!.Snapshot.ForcedShutdown);
    }
}
