using Microsoft.Extensions.Logging;
using NutHub.Protocol.Tests.Infrastructure;
using Xunit.Abstractions;

namespace NutHub.Protocol.Tests.Server;

public sealed class AuthenticationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("LOGIN ups1")]
    [InlineData("LOGIN")]
    [InlineData("PRIMARY ups1")]
    [InlineData("MASTER ups1")]
    [InlineData("FSD ups1")]
    [InlineData("SET VAR ups1 ups.id x")]
    [InlineData("SET TRACKING ON")]
    [InlineData("SET")]
    [InlineData("INSTCMD ups1 beeper.enable")]
    [InlineData("INSTCMD")]
    public async Task Privileged_commands_need_username_then_password_before_anything_else(string request)
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();
        Assert.Equal("ERR USERNAME-REQUIRED", await client.CommandAsync(request));
        Assert.Equal("OK", await client.CommandAsync("USERNAME admin"));
        Assert.Equal("ERR PASSWORD-REQUIRED", await client.CommandAsync(request));
    }

    [Fact]
    public async Task Username_and_password_can_be_given_once()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();
        Assert.Equal("OK", await client.CommandAsync("USERNAME monsecondary"));
        Assert.Equal("ERR ALREADY-SET-USERNAME", await client.CommandAsync("USERNAME admin"));
        Assert.Equal("OK", await client.CommandAsync("PASSWORD " + NutTestClient.Quote(NutTestHost.Password)));
        Assert.Equal("ERR ALREADY-SET-PASSWORD", await client.CommandAsync("PASSWORD other"));
        Assert.Equal("OK", await client.CommandAsync("LOGIN ups1"));
    }

    [Fact]
    public async Task Credentials_are_checked_when_a_privileged_command_runs()
    {
        await using var host = await NutTestHost.StartAsync(output);

        // Wrong password: USERNAME and PASSWORD still answer OK, as in upsd.
        await using (var wrong = await host.ConnectAsAsync("monsecondary", "not the password"))
        {
            Assert.Equal("ERR ACCESS-DENIED", await wrong.CommandAsync("LOGIN ups1"));
            Assert.Null(Assert.Single(host.Sessions.Sessions).Username);
        }

        await using (var unknown = await host.ConnectAsAsync("stranger"))
        {
            Assert.Equal("ERR ACCESS-DENIED", await unknown.CommandAsync("LOGIN ups1"));
        }

        // Account names are case-sensitive, like upsd.users.
        await using (var wrongCase = await host.ConnectAsAsync("MonSecondary"))
        {
            Assert.Equal("ERR ACCESS-DENIED", await wrongCase.CommandAsync("LOGIN ups1"));
        }

        await using var good = await host.ConnectAsAsync("monsecondary");
        Assert.Equal("OK", await good.CommandAsync("LOGIN ups1"));
        await NutTestHost.WaitUntilAsync(() => host.Sessions.Sessions.Any(s => s.Username == "monsecondary"),
                                         "the account in the session registry");
    }

    [Fact]
    public async Task Error_precedence_follows_upsd()
    {
        await using var host = await NutTestHost.StartAsync(output);
        host.Device("ups2").Disconnect();
        await host.WaitForUpsAsync("ups2", s => !s.IsAvailable, "stale ups2");

        await using var client = await host.ConnectAsAsync("admin", "wrong password");

        // LOGIN / PRIMARY / FSD: the UPS name is checked before the credentials.
        Assert.Equal("ERR UNKNOWN-UPS", await client.CommandAsync("LOGIN nope"));
        Assert.Equal("ERR UNKNOWN-UPS", await client.CommandAsync("PRIMARY nope"));
        Assert.Equal("ERR UNKNOWN-UPS", await client.CommandAsync("FSD nope"));
        Assert.Equal("ERR INVALID-ARGUMENT", await client.CommandAsync("FSD"));

        // SET VAR: UPS, then data, then credentials, then the variable.
        Assert.Equal("ERR UNKNOWN-UPS", await client.CommandAsync("SET VAR nope ups.id x"));
        Assert.Equal("ERR DATA-STALE", await client.CommandAsync("SET VAR ups2 ups.id x"));
        Assert.Equal("ERR ACCESS-DENIED", await client.CommandAsync("SET VAR ups1 no.such.var x"));

        // INSTCMD: UPS, data, command, then credentials.
        Assert.Equal("ERR UNKNOWN-UPS", await client.CommandAsync("INSTCMD nope beeper.enable"));
        Assert.Equal("ERR DATA-STALE", await client.CommandAsync("INSTCMD ups2 beeper.enable"));
        Assert.Equal("ERR CMD-NOT-SUPPORTED", await client.CommandAsync("INSTCMD ups1 no.such.cmd"));
        Assert.Equal("ERR ACCESS-DENIED", await client.CommandAsync("INSTCMD ups1 beeper.enable"));
    }

    [Fact]
    public async Task Each_right_is_granted_as_configured()
    {
        await using var host = await NutTestHost.StartAsync(output);
        (string User, string Request, string Expected)[] cases =
        [
            ("monsecondary", "LOGIN ups1", "OK"),
            ("monprimary", "LOGIN ups1", "OK"),
            ("nobody", "LOGIN ups1", "ERR ACCESS-DENIED"),
            ("admin", "LOGIN ups1", "ERR ACCESS-DENIED"),
            ("monsecondary", "PRIMARY ups1", "ERR ACCESS-DENIED"),
            ("monprimary", "PRIMARY ups1", "OK PRIMARY-GRANTED"),
            ("monprimary", "master ups1", "OK MASTER-GRANTED"),
            ("admin", "PRIMARY ups1", "ERR ACCESS-DENIED"),
            ("monsecondary", "FSD ups2", "ERR ACCESS-DENIED"),
            ("nobody", "FSD ups2", "ERR ACCESS-DENIED"),
            ("fsdonly", "FSD ups2", "OK FSD-SET"),
            ("monprimary", "FSD ups2", "OK FSD-SET"),
            ("monprimary", "SET VAR ups1 ups.id x", "ERR ACCESS-DENIED"),
            ("fsdonly", "SET VAR ups1 ups.id x", "ERR ACCESS-DENIED"),
            ("admin", "SET VAR ups1 ups.id x", "OK"),
            ("operator", "SET VAR ups1 ups.id y", "OK"),
            ("operator", "SET VAR ups2 ups.id y", "ERR ACCESS-DENIED"),
            ("operator", "INSTCMD ups1 beeper.enable", "OK"),
            ("operator", "INSTCMD ups1 BEEPER.ENABLE", "OK"),
            ("operator", "INSTCMD ups1 beeper.disable", "ERR ACCESS-DENIED"),
            ("operator", "INSTCMD ups2 beeper.enable", "ERR ACCESS-DENIED"),
            ("operator", "FSD ups1", "ERR ACCESS-DENIED"),
            ("admin", "INSTCMD ups2 beeper.disable", "OK"),
            ("nobody", "INSTCMD ups1 beeper.enable", "ERR ACCESS-DENIED"),
            ("monprimary", "INSTCMD ups1 beeper.enable", "ERR ACCESS-DENIED"),
        ];

        foreach (var (user, request, expected) in cases)
        {
            await using var client = await host.ConnectAsAsync(user);
            Assert.True(expected == await client.CommandAsync(request), $"{user}: {request} should answer {expected}");
        }
    }

    [Fact]
    public async Task Tls_can_be_required_for_credentials()
    {
        await using var host = await NutTestHost.StartAsync(output, c =>
        {
            c.Nut.Tls.Enabled = true;
            c.Nut.Tls.RequireTlsForAuthentication = true;
        });
        await NutTestHost.WaitUntilAsync(() => host.Server.TlsAvailable, "the certificate");

        await using var plain = await host.ConnectAsync();
        Assert.Equal("ERR ACCESS-DENIED", await plain.CommandAsync("USERNAME admin"));
        Assert.Equal("ERR ACCESS-DENIED", await plain.CommandAsync("PASSWORD x"));
        Assert.Equal("ERR USERNAME-REQUIRED", await plain.CommandAsync("LOGIN ups1"));

        await using var secure = await host.ConnectAsync();
        await secure.StartTlsAsync();
        Assert.Equal("OK", await secure.CommandAsync("USERNAME monsecondary"));
        Assert.Equal("OK", await secure.CommandAsync("PASSWORD " + NutTestClient.Quote(NutTestHost.Password)));
        Assert.Equal("OK", await secure.CommandAsync("LOGIN ups1"));
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_address_out()
    {
        await using var host = await NutTestHost.StartAsync(output);
        for (int i = 0; i < 10; i++)
        {
            await using var guess = await host.ConnectAsAsync("monsecondary", "guess" + i);
            Assert.Equal("ERR ACCESS-DENIED", await guess.CommandAsync("LOGIN ups1"));
        }

        await using (var locked = await host.ConnectAsAsync("monsecondary"))
        {
            Assert.Equal("ERR ACCESS-DENIED", await locked.CommandAsync("LOGIN ups1"));
            Assert.Equal("VAR ups1 ups.status \"OL\"", await locked.CommandAsync("GET VAR ups1 ups.status"));
        }

        Assert.Single(host.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("failed NUT password checks"));

        host.Time.Advance(TimeSpan.FromMinutes(5));
        await using var later = await host.ConnectAsAsync("monsecondary");
        Assert.Equal("OK", await later.CommandAsync("LOGIN ups1"));
    }

    [Fact]
    public async Task Passwords_never_reach_the_logs()
    {
        const string wrong = "Wr0ng-Secret-42";
        await using var host = await NutTestHost.StartAsync(output);
        await using (var bad = await host.ConnectAsAsync("admin", wrong))
        {
            Assert.Equal("ERR ACCESS-DENIED", await bad.CommandAsync("INSTCMD ups1 beeper.enable"));
            Assert.Equal("ERR ACCESS-DENIED", await bad.CommandAsync("SET VAR ups1 ups.id x"));
        }

        await using (var good = await host.ConnectAsAsync("monprimary"))
        {
            Assert.Equal("OK", await good.CommandAsync("LOGIN ups1"));
            Assert.Equal("ERR ALREADY-SET-PASSWORD", await good.CommandAsync("PASSWORD again-" + wrong));
        }

        Assert.NotEmpty(host.Logs.Entries);
        Assert.DoesNotContain(host.Logs.Entries, e => e.Message.Contains(wrong) ||
                                                     e.Message.Contains(NutTestHost.Password) ||
                                                     e.Message.Contains("s3cret"));
    }
}
