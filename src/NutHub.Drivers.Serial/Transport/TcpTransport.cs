using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NutHub.Drivers.Serial.Transport;

/// <summary>
/// A raw TCP connection to a serial device server: ser2net in raw mode, a Moxa/Lantronix box in "TCP server" mode, or
/// the fake UPS in tools/fake-serial-ups. The server owns the serial parameters (baud rate, 8N1); every byte is passed
/// through unchanged. Telnet/RFC 2217 negotiation is not spoken, so the server must be in raw mode.
/// </summary>
internal sealed class TcpTransport : BufferedTransport
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly TcpSettings _settings;
    private readonly ILogger _logger;
    private Socket? _socket;
    private NetworkStream? _stream;

    public TcpTransport(TcpSettings settings, TimeProvider time, ILogger logger)
        : base(time)
    {
        _settings = settings;
        _logger = logger;
    }

    public override string Description => $"tcp://{_settings.Host}:{_settings.Port}";

    public override bool IsOpen => _stream is not null;

    protected override async Task OpenCoreAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeout = new CancellationTokenSource(ConnectTimeout, Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await socket.ConnectAsync(_settings.Host, _settings.Port, linked.Token).ConfigureAwait(false);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TransportException(TransportErrorKind.ConnectionFailed,
                $"No answer from {_settings.Host}:{_settings.Port} within {ConnectTimeout.TotalSeconds:0} s. Check the " +
                "address of the serial device server and that no firewall blocks the port.");
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw TranslateConnectError(ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _logger.LogDebug("Connected to {Endpoint}.", Description);
    }

    protected override Task CloseCoreAsync()
    {
        NetworkStream? stream = Interlocked.Exchange(ref _stream, null);
        _socket = null;
        stream?.Dispose();
        return Task.CompletedTask;
    }

    protected override async Task WriteCoreAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        NetworkStream stream = Stream;
        using var timeout = new CancellationTokenSource(WriteTimeout, Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await stream.WriteAsync(data, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransportException(TransportErrorKind.ConnectionLost,
                $"Writing to {Description} timed out; the connection is stuck.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw Lost(ex);
        }
    }

    protected override async Task<int> ReadSomeAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        NetworkStream stream = Stream;
        using var timer = new CancellationTokenSource(timeout, Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        int read;
        try
        {
            read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw Lost(ex);
        }

        if (read == 0)
        {
            throw new TransportException(TransportErrorKind.ConnectionLost,
                $"{Description} closed the connection. The serial device server may have restarted or accepts only one " +
                "client at a time.");
        }

        return read;
    }

    protected override Task DiscardCoreAsync(CancellationToken cancellationToken)
    {
        Socket socket = _socket ?? throw Lost(null);
        NetworkStream stream = Stream;
        var scratch = new byte[256];
        try
        {
            // Only what already arrived: Available never waits.
            while (socket.Available > 0)
            {
                if (stream.Read(scratch, 0, Math.Min(scratch.Length, socket.Available)) == 0)
                {
                    throw Lost(null);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw Lost(ex);
        }

        return Task.CompletedTask;
    }

    private NetworkStream Stream => _stream ?? throw Lost(null);

    private TransportException Lost(Exception? ex) =>
        new(TransportErrorKind.ConnectionLost,
            ex is null
                ? $"The connection to {Description} is closed."
                : $"The connection to {Description} was lost ({ex.Message}).",
            ex);

    private TransportException TranslateConnectError(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => new TransportException(
            TransportErrorKind.NotFound,
            $"The host name '{_settings.Host}' cannot be resolved. Check the spelling or use an IP address.", ex),
        SocketError.ConnectionRefused => new TransportException(TransportErrorKind.ConnectionFailed,
            $"{_settings.Host} refused the connection on port {_settings.Port}. Check the TCP port configured in the " +
            "serial device server (ser2net, NPort...) and that the service is running.", ex),
        SocketError.HostUnreachable or SocketError.NetworkUnreachable => new TransportException(
            TransportErrorKind.ConnectionFailed,
            $"{_settings.Host} is not reachable from this machine (routing or firewall).", ex),
        SocketError.TimedOut => new TransportException(TransportErrorKind.ConnectionFailed,
            $"No answer from {_settings.Host}:{_settings.Port}. Check the address and the firewall.", ex),
        _ => new TransportException(TransportErrorKind.ConnectionFailed,
            $"Cannot connect to {_settings.Host}:{_settings.Port}: {ex.Message}", ex),
    };
}
