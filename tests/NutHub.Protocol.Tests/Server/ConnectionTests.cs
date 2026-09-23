using System.Text;
using NutHub.Protocol.Tests.Infrastructure;
using Xunit.Abstractions;

namespace NutHub.Protocol.Tests.Server;

public sealed class ConnectionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Clients_outside_the_allowed_networks_are_closed_before_reading()
    {
        await using var host = await NutTestHost.StartAsync(output, c => c.Nut.AllowedNetworks = ["10.0.0.0/8", "::1"]);
        await using var client = await host.ConnectAsync();
        Assert.True(await client.WaitForCloseAsync());
        Assert.Empty(host.Sessions.Sessions);
        Assert.Contains(host.Logs.Entries, e => e.Message.Contains("not in the allowed networks"));
    }

    [Fact]
    public async Task Allowed_networks_accept_matching_clients()
    {
        await using var host = await NutTestHost.StartAsync(output, c => c.Nut.AllowedNetworks = ["127.0.0.0/8"]);
        await using var client = await host.ConnectAsync();
        Assert.Equal("1.3", await client.CommandAsync("NETVER"));
    }

    [Fact]
    public async Task A_changed_access_list_closes_connections_it_no_longer_allows()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();
        Assert.Equal("1.3", await client.CommandAsync("NETVER"));

        await host.Config.UpdateAsync(c => c.Nut.AllowedNetworks = ["192.168.0.0/16"]);
        Assert.True(await client.WaitForCloseAsync());
        await NutTestHost.WaitUntilAsync(() => host.Sessions.Sessions.Count == 0, "the session to close");
    }

    [Fact]
    public async Task Connections_beyond_the_limit_are_refused()
    {
        await using var host = await NutTestHost.StartAsync(output, c => c.Nut.MaxConnections = 2);
        await using var first = await host.ConnectAsync();
        await using var second = await host.ConnectAsync();
        Assert.Equal("1.3", await first.CommandAsync("NETVER"));
        Assert.Equal("1.3", await second.CommandAsync("NETVER"));

        await using (var third = await host.ConnectAsync())
        {
            Assert.True(await third.WaitForCloseAsync());
        }

        await first.DisposeAsync();
        await NutTestHost.WaitUntilAsync(() => host.Server.ActiveConnections == 1, "a free slot");
        await using var fourth = await host.ConnectAsync();
        Assert.Equal("1.3", await fourth.CommandAsync("NETVER"));
        Assert.Equal("1.3", await second.CommandAsync("NETVER"));
    }

    [Fact]
    public async Task A_request_longer_than_1024_bytes_is_refused_and_the_connection_closed()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();
        await client.SendRawAsync(Encoding.ASCII.GetBytes("GET VAR ups1 " + new string('x', 3000) + "\n"));
        Assert.Equal("ERR INVALID-ARGUMENT", await client.ReadLineAsync());
        Assert.True(await client.WaitForCloseAsync());

        await using var other = await host.ConnectAsync();
        Assert.Equal("1.3", await other.CommandAsync("NETVER"));
    }

    [Fact]
    public async Task A_request_of_1024_bytes_is_accepted()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();
        string request = "GET VAR ups1 " + new string('x', 1024 - 13);
        Assert.Equal(1024, request.Length);
        Assert.Equal("ERR VAR-NOT-SUPPORTED", await client.CommandAsync(request + "\r"));
    }

    [Fact]
    public async Task Idle_connections_are_closed_after_15_minutes()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var idle = await host.ConnectAsync();
        await using var busy = await host.ConnectAsync();
        Assert.Equal("1.3", await idle.CommandAsync("NETVER"));

        host.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal("1.3", await busy.CommandAsync("NETVER"));
        host.Time.Advance(TimeSpan.FromMinutes(6));

        Assert.True(await idle.WaitForCloseAsync());
        Assert.Equal("1.3", await busy.CommandAsync("NETVER"));
    }

    [Fact]
    public async Task The_administrator_can_disconnect_a_client()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsAsync("monsecondary");
        Assert.Equal("OK", await client.CommandAsync("LOGIN ups1"));

        long id = Assert.Single(host.Sessions.Sessions).Id;
        Assert.True(host.Sessions.Disconnect(id));
        Assert.True(await client.WaitForCloseAsync());
        await NutTestHost.WaitUntilAsync(() => host.Sessions.Sessions.Count == 0, "the session to close");
        Assert.Equal(0, host.Sessions.GetLoginCount("ups1"));
        Assert.False(host.Sessions.Disconnect(id));
    }

    [Fact]
    public async Task A_client_that_does_not_read_cannot_block_the_server()
    {
        // Data never goes stale here, so every answer stays large while the test moves the clock.
        await using var host = await NutTestHost.StartAsync(output, c => c.Nut.MaxAgeSeconds = 3600);
        await using var flooder = await host.ConnectAsync(receiveBufferSize: 1024);

        // Thousands of LIST VAR requests whose answers are never read.
        byte[] requests = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("LIST VAR ups1\n", 20_000)));
        Task flood = Task.Run(async () =>
        {
            try
            {
                await flooder.SendRawAsync(requests);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        });

        // Wait until the server is stuck writing to it: its command counter stops moving for a while.
        long last = -1;
        var unchangedSince = System.Diagnostics.Stopwatch.StartNew();
        await NutTestHost.WaitUntilAsync(() =>
        {
            long now = host.Sessions.Sessions.FirstOrDefault()?.Commands ?? 0;
            if (now != last)
            {
                last = now;
                unchangedSince.Restart();
            }

            return now > 0 && now < 20_000 && unchangedSince.Elapsed > TimeSpan.FromMilliseconds(300);
        }, "the server to block on the flooder", TimeSpan.FromSeconds(30));

        // Everybody else is served normally.
        await using var other = await host.ConnectAsync();
        Assert.Equal("VAR ups1 ups.status \"OL\"", await other.CommandAsync("GET VAR ups1 ups.status"));

        // The write timeout disconnects the flooder.
        host.Time.Advance(TimeSpan.FromSeconds(31));
        await NutTestHost.WaitUntilAsync(() => host.Sessions.Sessions.Count == 1, "the flooder to be disconnected");
        Assert.Contains(host.Logs.Entries, e => e.Message.Contains("closed: the client did not read its answers"));
        Assert.Equal("1.3", await other.CommandAsync("NETVER"));
        await flood;
    }

    [Fact]
    public async Task Stopping_the_server_closes_the_connections()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();
        Assert.Equal("1.3", await client.CommandAsync("NETVER"));

        await host.Server.StopAsync(CancellationToken.None);
        Assert.True(await client.WaitForCloseAsync());
        Assert.Empty(host.Server.Endpoints);
    }
}
