using System.Net;
using System.Net.Sockets;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Protocol.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace NutHub.Protocol.Tests.Server;

public sealed class ListenerTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Status_describes_the_listeners()
    {
        await using var host = await NutTestHost.StartAsync(output);
        INutServerStatus status = host.Services.GetRequiredService<INutServerStatus>();

        Assert.Same(host.Server, status);
        Assert.True(status.Enabled);
        Assert.Equal([$"127.0.0.1:{host.EndPoint.Port}"], status.Endpoints);
        Assert.Null(status.LastError);
        Assert.False(status.TlsAvailable);
        Assert.NotEqual(0, host.EndPoint.Port);
    }

    [Fact]
    public async Task Changed_endpoints_are_rebound_without_dropping_connections()
    {
        await using var host = await NutTestHost.StartAsync(output);
        IPEndPoint oldEndPoint = host.EndPoint;
        await using var existing = await host.ConnectAsync();
        Assert.Equal("1.3", await existing.CommandAsync("NETVER"));

        string newAddress = Socket.OSSupportsIPv6 ? "::1" : "127.0.0.2";
        await host.Config.UpdateAsync(c => c.Nut.Listen = [new ListenEndpoint { Address = newAddress, Port = 0 }]);
        await NutTestHost.WaitUntilAsync(
            () => host.Server.BoundEndPoints.Count == 1 && host.Server.BoundEndPoints[0].Address.Equals(IPAddress.Parse(newAddress)),
            "the new listener");

        await using (var fresh = await NutTestClient.ConnectAsync(host.EndPoint))
        {
            Assert.Equal("1.3", await fresh.CommandAsync("NETVER"));
            Assert.Equal(2, host.Sessions.Sessions.Count);
        }

        Assert.Equal("1.3", await existing.CommandAsync("NETVER"));
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(oldEndPoint);
        });
    }

    [Fact]
    public async Task Unrelated_changes_keep_the_listening_socket()
    {
        await using var host = await NutTestHost.StartAsync(output);
        IPEndPoint before = host.EndPoint;
        await host.Config.UpdateAsync(c => c.Nut.MaxConnections = 100);
        await host.Config.UpdateAsync(c => c.Nut.Listen = [new ListenEndpoint { Address = " 127.0.0.1", Port = 0 }]);
        await Task.Delay(200);
        Assert.Equal(before, host.EndPoint);
    }

    [Fact]
    public async Task A_port_in_use_is_reported_and_retried()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        int port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        try
        {
            await using var host = await NutTestHost.StartAsync(output, c =>
                c.Nut.Listen = [new ListenEndpoint { Address = "127.0.0.1", Port = port }]);

            Assert.Empty(host.Server.Endpoints);
            Assert.Contains($"Cannot listen on 127.0.0.1:{port}", host.Server.LastError);
            Assert.Contains("in use", host.Server.LastError);

            blocker.Stop();
            host.Time.Advance(TimeSpan.FromSeconds(30));
            await NutTestHost.WaitUntilAsync(() => host.Server.Endpoints.Count == 1, "the retry to bind");
            Assert.Null(host.Server.LastError);

            await using var client = await host.ConnectAsync();
            Assert.Equal("1.3", await client.CommandAsync("NETVER"));
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task Invalid_addresses_are_reported()
    {
        await using var host = await NutTestHost.StartAsync(output, c => c.Nut.Listen =
        [
            new ListenEndpoint { Address = "nonsense", Port = 3493 },
            new ListenEndpoint { Address = "127.0.0.1", Port = 0 },
        ]);
        Assert.Single(host.Server.Endpoints);
        Assert.Contains("Invalid NUT listen address 'nonsense'", host.Server.LastError);
    }

    [Fact]
    public async Task A_disabled_server_listens_nowhere_and_follows_changes()
    {
        await using var host = await NutTestHost.StartAsync(output, c => c.Nut.Enabled = false);
        await Task.Delay(100);
        Assert.False(host.Server.Enabled);
        Assert.Empty(host.Server.Endpoints);

        await host.Config.UpdateAsync(c => c.Nut.Enabled = true);
        await NutTestHost.WaitUntilAsync(() => host.Server.Endpoints.Count == 1, "the listener");
        await using var client = await host.ConnectAsync();
        Assert.Equal("1.3", await client.CommandAsync("NETVER"));

        await host.Config.UpdateAsync(c => c.Nut.Enabled = false);
        Assert.True(await client.WaitForCloseAsync());
        await NutTestHost.WaitUntilAsync(() => host.Server.Endpoints.Count == 0, "the listener to close");
    }
}
