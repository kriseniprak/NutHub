using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Serial.Megatec;
using NutHub.Drivers.Serial.Tests.Fakes;
using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.Tests.Transport;

/// <summary>The tcp transport against a loopback serial device server.</summary>
public sealed class TcpTransportTests
{
    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>Answers every CR-terminated line with a fixed text.</summary>
    private sealed class FixedReply(string reply) : IFakeDevice
    {
        public void Receive(byte value, Action<byte[]> send)
        {
            if (value == '\r' && reply.Length > 0)
            {
                send(FakeTransport.Latin1(reply));
            }
        }
    }

    [Fact]
    public async Task The_megatec_driver_polls_and_commands_a_UPS_behind_a_serial_server_and_survives_a_restart()
    {
        var ups = new FakeMegatecUps();
        await using var server = new TcpFakeServer(ups);
        IUpsDriver driver = new MegatecDriverFactory().Create(new DriverCreateContext
        {
            UpsName = "tcp",
            Options = TestSettings.Options(("transport", "tcp"), ("host", "127.0.0.1"),
                                           ("tcpPort", server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                                           ("protocol", "megatec")),
            PollInterval = TimeSpan.FromMilliseconds(50),
            LoggerFactory = NullLoggerFactory.Instance,
            TimeProvider = TimeProvider.System,
            Services = new NoServices(),
        });
        var context = new FakeDriverContext(TimeProvider.System, TimeSpan.FromMilliseconds(50));

        await using (DriverRun.Start(driver, context))
        {
            await context.WaitForAsync(c => c.PublishCount >= 2, "two polls over TCP");
            Assert.Equal("OL", context.Status);
            Assert.Equal("226.0", context.Last!.Variables["input.voltage"]);

            CommandResult result = await driver.InstantCommandAsync("beeper.toggle", null, CancellationToken.None);
            Assert.True(result.IsSuccess, result.ToString());
            Assert.Contains("Q", ups.Received);

            ups.SetOnBattery(23.0, lowBattery: true);
            await context.WaitForAsync(c => c.Status == "OB LB", "on battery over TCP");

            // The server restarts: the connection breaks, and the driver connects again.
            server.DropClients();
            await context.WaitForAsync(_ => server.Connections >= 2, "a new connection");
            int published = context.PublishCount;
            await context.WaitForAsync(c => c.PublishCount > published + 1, "polls on the new connection");
        }
    }

    [Fact]
    public async Task Lines_are_split_on_the_terminator_and_the_rest_is_kept()
    {
        await using var server = new TcpFakeServer(new FixedReply("AB\rCD\r"));
        await using var transport = new TcpTransport(new TcpSettings("127.0.0.1", server.Port), TimeProvider.System, NullLogger.Instance);
        await transport.OpenAsync(CancellationToken.None);

        await transport.WriteAsync(FakeTransport.Latin1("X\r"), CancellationToken.None);
        TransportRead first = await transport.ReadUntilAsync(new[] { (byte)'\r' }, TimeSpan.FromSeconds(5), 64, CancellationToken.None);
        TransportRead second = await transport.ReadUntilAsync(new[] { (byte)'\r' }, TimeSpan.FromSeconds(5), 64, CancellationToken.None);
        TransportRead silence = await transport.ReadUntilAsync(new[] { (byte)'\r' }, TimeSpan.FromMilliseconds(100), 64, CancellationToken.None);

        Assert.Equal("AB\r", System.Text.Encoding.Latin1.GetString(first.Data));
        Assert.Equal("CD\r", System.Text.Encoding.Latin1.GetString(second.Data));
        Assert.Equal(ReadStatus.Timeout, silence.Status);
        Assert.Empty(silence.Data);
    }

    [Fact]
    public async Task Garbage_without_terminator_is_an_overflow()
    {
        await using var server = new TcpFakeServer(new FixedReply(new string('x', 100)));
        await using var transport = new TcpTransport(new TcpSettings("127.0.0.1", server.Port), TimeProvider.System, NullLogger.Instance);
        await transport.OpenAsync(CancellationToken.None);

        await transport.WriteAsync(FakeTransport.Latin1("Q1\r"), CancellationToken.None);
        TransportRead read = await transport.ReadUntilAsync(new[] { (byte)'\r' }, TimeSpan.FromSeconds(5), 32, CancellationToken.None);

        Assert.Equal(ReadStatus.Overflow, read.Status);
        Assert.Equal(32, read.Data.Length);
    }

    [Fact]
    public async Task A_refused_connection_gives_an_actionable_error()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await using var transport = new TcpTransport(new TcpSettings("127.0.0.1", port), TimeProvider.System, NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<TransportException>(() => transport.OpenAsync(CancellationToken.None));

        Assert.Equal(TransportErrorKind.ConnectionFailed, ex.Kind);
        Assert.Contains("refused", ex.Message);
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task A_closed_connection_is_reported_as_lost()
    {
        await using var server = new TcpFakeServer(new FixedReply(string.Empty));
        await using var transport = new TcpTransport(new TcpSettings("127.0.0.1", server.Port), TimeProvider.System, NullLogger.Instance);
        await transport.OpenAsync(CancellationToken.None);
        for (int i = 0; i < 100 && server.Connections == 0; i++)
        {
            await Task.Delay(10);
        }

        server.DropClients();
        var ex = await Assert.ThrowsAsync<TransportException>(() =>
            transport.ReadUntilAsync(new[] { (byte)'\r' }, TimeSpan.FromSeconds(5), 64, CancellationToken.None));

        Assert.Equal(TransportErrorKind.ConnectionLost, ex.Kind);
    }
}
