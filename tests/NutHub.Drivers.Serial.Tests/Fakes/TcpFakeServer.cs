using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NutHub.Drivers.Serial.Tests.Fakes;

/// <summary>
/// A serial device server on the loopback interface (like ser2net in raw mode) with an emulated UPS behind it, so the
/// tcp transport can be exercised end to end.
/// </summary>
internal sealed class TcpFakeServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly IFakeDevice _device;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly Task _accept;
    private int _connections;

    public TcpFakeServer(IFakeDevice device)
    {
        _device = device;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptAsync();
    }

    public int Port { get; }

    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Closes the open connections, as a serial server that restarts.</summary>
    public void DropClients()
    {
        foreach (TcpClient client in _clients.Keys)
        {
            client.Client.Close(0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        DropClients();
        try
        {
            await _accept;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopped.
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
            Interlocked.Increment(ref _connections);
            _clients[client] = 0;
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            var buffer = new byte[256];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    break;
                }

                for (int i = 0; i < read; i++)
                {
                    _device.Receive(buffer[i], reply =>
                    {
                        lock (stream)
                        {
                            stream.Write(reply);
                        }
                    });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The client or the test went away.
        }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
        }
    }
}
