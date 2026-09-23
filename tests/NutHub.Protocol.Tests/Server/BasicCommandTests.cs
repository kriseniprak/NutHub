using NutHub.Core;
using NutHub.Protocol.Tests.Infrastructure;
using Xunit.Abstractions;

namespace NutHub.Protocol.Tests.Server;

public sealed class BasicCommandTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Version_protocol_and_help()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();

        Assert.Equal($"NutHub {NutHubInfo.Version} (Network UPS Tools protocol 1.3 compatible) - {NutHubInfo.ProjectUrl}",
                     await client.CommandAsync("VER"));
        Assert.Equal("1.3", await client.CommandAsync("NETVER"));
        Assert.Equal("1.3", await client.CommandAsync("PROTVER"));
        Assert.Equal("Commands: HELP VER PROTVER GET LIST SET INSTCMD LOGIN LOGOUT USERNAME PASSWORD STARTTLS",
                     await client.CommandAsync("HELP"));
        Assert.Equal("1.3", await client.CommandAsync("netver"));
        Assert.Equal("1.3", await client.CommandAsync("ProtVer"));
    }

    [Fact]
    public async Task Configured_version_string_is_used_and_follows_changes()
    {
        await using var host = await NutTestHost.StartAsync(output, c =>
            c.Nut.VersionString = "Network UPS Tools upsd 2.8.2 - http://www.networkupstools.org/");
        await using var client = await host.ConnectAsync();

        Assert.Equal("Network UPS Tools upsd 2.8.2 - http://www.networkupstools.org/", await client.CommandAsync("VER"));
        Assert.Equal("VAR anything server.info \"Network UPS Tools upsd 2.8.2 - http://www.networkupstools.org/\"",
                     await client.CommandAsync("GET VAR anything server.info"));

        await host.Config.UpdateAsync(c => c.Nut.VersionString = "Custom \"server\"\nsecond line");
        Assert.Equal("Custom \"server\" second line", await client.CommandAsync("VER"));

        await host.Config.UpdateAsync(c => c.Nut.VersionString = null);
        Assert.StartsWith("NutHub ", await client.CommandAsync("VER"));
    }

    [Theory]
    [InlineData("VER extra")]
    [InlineData("NETVER x")]
    [InlineData("PROTVER x")]
    [InlineData("HELP me")]
    [InlineData("LOGOUT now")]
    [InlineData("GET")]
    [InlineData("GET VAR")]
    [InlineData("GET VAR ups1")]
    [InlineData("GET TYPE ups1")]
    [InlineData("GET NUMLOGINS")]
    [InlineData("GET UPSDESC")]
    [InlineData("GET FOO a b")]
    [InlineData("GET FOO")]
    [InlineData("LIST")]
    [InlineData("LIST VAR")]
    [InlineData("LIST ENUM ups1")]
    [InlineData("LIST RANGE ups1")]
    [InlineData("LIST FOO a b")]
    [InlineData("LIST FOO")]
    [InlineData("USERNAME")]
    [InlineData("USERNAME a b")]
    [InlineData("PASSWORD")]
    [InlineData("PASSWORD a b")]
    public async Task Wrong_arity_or_subcommand_is_an_invalid_argument(string request)
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();
        Assert.Equal("ERR INVALID-ARGUMENT", await client.CommandAsync(request));

        // The connection stays usable.
        Assert.Equal("1.3", await client.CommandAsync("NETVER"));
    }

    [Theory]
    [InlineData("FOO")]
    [InlineData("REQ ups1 ups.status")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# just a comment")]
    [InlineData("VERSION")]
    public async Task Unknown_or_empty_requests_are_unknown_commands(string request)
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();
        Assert.Equal("ERR UNKNOWN-COMMAND", await client.CommandAsync(request));
    }

    [Fact]
    public async Task Extra_words_after_get_and_list_are_ignored_like_upsd()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();
        Assert.Equal("VAR ups1 ups.status \"OL\"", await client.CommandAsync("GET VAR ups1 ups.status extra words"));
        Assert.Equal("OFF", await client.CommandAsync("GET TRACKING"));
    }

    [Fact]
    public async Task Pipelined_requests_are_answered_in_order()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();
        await client.SendRawAsync("NETVER\r\nGET VAR ups1 ups.status\nFOO\nLIST UPS\n"u8.ToArray());

        Assert.Equal("1.3", await client.ReadLineAsync());
        Assert.Equal("VAR ups1 ups.status \"OL\"", await client.ReadLineAsync());
        Assert.Equal("ERR UNKNOWN-COMMAND", await client.ReadLineAsync());
        Assert.Equal("BEGIN LIST UPS", await client.ReadLineAsync());
    }

    [Fact]
    public async Task Requests_split_across_packets_are_reassembled()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();
        await client.SendRawAsync("GET VAR u"u8.ToArray());
        await Task.Delay(50);
        await client.SendRawAsync("ps1 ups.st"u8.ToArray());
        await Task.Delay(50);
        await client.SendRawAsync("atus\n"u8.ToArray());
        Assert.Equal("VAR ups1 ups.status \"OL\"", await client.ReadLineAsync());
    }

    [Fact]
    public async Task Logout_says_goodbye_and_closes()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();
        await NutTestHost.WaitUntilAsync(() => host.Sessions.Sessions.Count == 1, "the session");

        Assert.Equal("OK Goodbye", await client.CommandAsync("LOGOUT"));
        Assert.True(await client.WaitForCloseAsync());
        await NutTestHost.WaitUntilAsync(() => host.Sessions.Sessions.Count == 0, "the session to close");
    }

    [Fact]
    public async Task Commands_update_the_session_registry()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();
        await client.CommandAsync("VER");
        await client.CommandAsync("NETVER");

        var session = Assert.Single(host.Sessions.Sessions);
        Assert.Equal("127.0.0.1", session.Address);
        Assert.Equal(2, session.Commands);
        Assert.Null(session.Username);
        Assert.False(session.Tls);
    }
}
