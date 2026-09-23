using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NutHub.Protocol.Server;

/// <summary>One listening socket and its accept loop. Accepted sockets are handed over at once.</summary>
internal sealed class NutListener : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task _acceptLoop = Task.CompletedTask;

    private NutListener(ListenEndpointKey key, Socket socket, TimeProvider time, ILogger logger)
    {
        Key = key;
        _socket = socket;
        _time = time;
        _logger = logger;
        LocalEndPoint = (IPEndPoint)socket.LocalEndPoint!;
    }

    public ListenEndpointKey Key { get; }

    /// <summary>The bound address and port (the actual port when 0 was configured).</summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>Binds the endpoint and starts accepting. Throws <see cref="SocketException"/> when it cannot bind.</summary>
    public static NutListener Start(ListenEndpointKey key, Action<Socket> onAccepted, TimeProvider time, ILogger logger)
    {
        Socket socket = key.CreateSocket();
        var listener = new NutListener(key, socket, time, logger);
        listener._acceptLoop = Task.Run(() => listener.AcceptLoopAsync(onAccepted));
        return listener;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        _socket.Dispose();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The accept loop of {Endpoint} ended with an error.", LocalEndPoint);
        }
    }

    private async Task AcceptLoopAsync(Action<Socket> onAccepted)
    {
        CancellationToken token = _stop.Token;
        int consecutiveErrors = 0;
        while (!token.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _socket.AcceptAsync(token).ConfigureAwait(false);
                consecutiveErrors = 0;
            }
            catch (Exception ex) when (token.IsCancellationRequested ||
                                       ex is ObjectDisposedException or OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset
                                                 or SocketError.ConnectionAborted or SocketError.TimedOut)
            {
                continue; // the client gave up before the connection was accepted
            }
            catch (SocketException ex)
            {
                // Resource exhaustion (too many open files...): back off rather than spin.
                consecutiveErrors++;
                _logger.LogWarning("Accepting a NUT connection on {Endpoint} failed: {Message}", LocalEndPoint,
                                   ex.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, 50 * consecutiveErrors)), _time, token)
                              .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            try
            {
                onAccepted(client);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Handling a new NUT connection on {Endpoint} failed.", LocalEndPoint);
                client.Dispose();
            }
        }
    }
}
