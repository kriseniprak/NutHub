using System.Text.RegularExpressions;
using NutHub.Core.Model;
using NutHub.Protocol.Tests.Infrastructure;
using Xunit.Abstractions;

namespace NutHub.Protocol.Tests.Server;

public sealed partial class TrackingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Tracking_is_off_by_default_and_needs_credentials()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();

        Assert.Equal("OFF", await client.CommandAsync("GET TRACKING"));
        Assert.Equal("ERR FEATURE-NOT-CONFIGURED",
                     await client.CommandAsync("GET TRACKING 1bd31808-cb49-4aec-9d75-d056e6f018d2"));
        Assert.Equal("ERR USERNAME-REQUIRED", await client.CommandAsync("SET TRACKING ON"));

        Assert.Equal("OK", await client.CommandAsync("USERNAME nobody"));
        Assert.Equal("OK", await client.CommandAsync("PASSWORD " + NutTestClient.Quote(NutTestHost.Password)));
        Assert.Equal("ERR INVALID-ARGUMENT", await client.CommandAsync("SET TRACKING MAYBE"));
        Assert.Equal("OK", await client.CommandAsync("SET TRACKING on"));
        Assert.Equal("ON", await client.CommandAsync("GET TRACKING"));
        Assert.Equal("ERR UNKNOWN", await client.CommandAsync("GET TRACKING 1bd31808-cb49-4aec-9d75-d056e6f018d2"));
        Assert.Equal("OK", await client.CommandAsync("SET TRACKING OFF"));
        Assert.Equal("OFF", await client.CommandAsync("GET TRACKING"));
    }

    [Fact]
    public async Task Tracked_instant_command_answers_at_once_and_reports_its_result()
    {
        await using var host = await NutTestHost.StartAsync(output);
        FakeDevice device = host.Device("ups1");
        var release = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.OnCommand = (_, _, _) => release.Task;

        await using var client = await host.ConnectAsAsync("admin");
        Assert.Equal("OK", await client.CommandAsync("SET TRACKING ON"));

        string answer = await client.CommandAsync("INSTCMD ups1 load.off.delay 60");
        string id = TrackingId(answer);
        Assert.Equal("PENDING", await client.CommandAsync("GET TRACKING " + id));

        // Tracking results are shared: another tracking client can ask about the id.
        await using var other = await host.ConnectAsAsync("nobody");
        Assert.Equal("OK", await other.CommandAsync("SET TRACKING ON"));
        Assert.Equal("PENDING", await other.CommandAsync("GET TRACKING " + id.ToUpperInvariant()));

        release.SetResult(CommandResult.Ok);
        await WaitForResultAsync(client, id, "SUCCESS");
        Assert.Equal(("load.off.delay", "60"), Assert.Single(device.Executed));
    }

    [Fact]
    public async Task Tracked_failures_and_writes()
    {
        await using var host = await NutTestHost.StartAsync(output);
        FakeDevice device = host.Device("ups1");
        await using var client = await host.ConnectAsAsync("admin");
        Assert.Equal("OK", await client.CommandAsync("SET TRACKING ON"));

        device.OnCommand = (_, _, _) => Task.FromResult(CommandResult.Fail("no"));
        string failed = TrackingId(await client.CommandAsync("INSTCMD ups1 beeper.enable"));
        await WaitForResultAsync(client, failed, "ERR FAILED");

        device.OnCommand = (_, _, _) => Task.FromResult(CommandResult.InvalidArgument("bad"));
        string invalid = TrackingId(await client.CommandAsync("INSTCMD ups1 load.off.delay x"));
        await WaitForResultAsync(client, invalid, "ERR INVALID-ARGUMENT");

        string write = TrackingId(await client.CommandAsync("SET VAR ups1 ups.id tracked"));
        await WaitForResultAsync(client, write, "SUCCESS");
        Assert.Equal(("ups.id", "tracked"), Assert.Single(device.Written));

        // Checks that upsd makes before tracking still answer at once.
        Assert.Equal("ERR INVALID-VALUE", await client.CommandAsync("SET VAR ups1 ups.beeper.status loud"));
        Assert.Equal("ERR CMD-NOT-SUPPORTED", await client.CommandAsync("INSTCMD ups1 no.such.cmd"));

        Assert.Equal("OK", await client.CommandAsync("SET TRACKING OFF"));
        device.OnCommand = null;
        Assert.Equal("OK", await client.CommandAsync("INSTCMD ups1 beeper.enable"));
    }

    [Fact]
    public async Task Results_are_forgotten_after_an_hour()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsAsync("admin");
        Assert.Equal("OK", await client.CommandAsync("SET TRACKING ON"));
        string id = TrackingId(await client.CommandAsync("INSTCMD ups1 beeper.enable"));
        await WaitForResultAsync(client, id, "SUCCESS");

        // A new connection: the first one is closed as idle meanwhile.
        host.Time.Advance(TimeSpan.FromMinutes(61));
        await using var later = await host.ConnectAsAsync("admin");
        Assert.Equal("OK", await later.CommandAsync("SET TRACKING ON"));
        Assert.Equal("ERR UNKNOWN", await later.CommandAsync("GET TRACKING " + id));
    }

    private static string TrackingId(string answer)
    {
        Match match = TrackingAnswer().Match(answer);
        Assert.True(match.Success, $"'{answer}' is not an OK TRACKING answer");
        return match.Groups[1].Value;
    }

    private static async Task WaitForResultAsync(NutTestClient client, string id, string expected)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string last;
        do
        {
            last = await client.CommandAsync("GET TRACKING " + id);
            if (last == expected)
            {
                return;
            }

            await Task.Delay(10);
        }
        while (watch.Elapsed < TimeSpan.FromSeconds(10));

        Assert.Equal(expected, last);
    }

    [GeneratedRegex("^OK TRACKING ([0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})$")]
    private static partial Regex TrackingAnswer();
}
