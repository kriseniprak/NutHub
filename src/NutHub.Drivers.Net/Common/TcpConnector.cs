using System.Net.Sockets;

namespace NutHub.Drivers.Net.Common;

/// <summary>Opens TCP connections to UPS servers with a bounded wait.</summary>
internal static class TcpConnector
{
    /// <summary>
    /// Connects to <paramref name="host"/> (name, IPv4 or IPv6 literal, with or without brackets), trying every
    /// address the name resolves to. Throws <see cref="TimeoutException"/> when nothing answers in time.
    /// </summary>
    public static async Task<Socket> ConnectAsync(string host, int port, TimeSpan timeout, TimeProvider time,
                                                  CancellationToken cancellationToken)
    {
        string target = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;

        // A dual-mode socket reaches both IPv4 and IPv6 addresses, whichever the name resolves to first.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // Keep-alive lets the operating system notice a server that vanished without closing the connection.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            using var scope = new TimeoutScope(timeout, time, cancellationToken);
            try
            {
                await socket.ConnectAsync(target, port, scope.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (scope.TimedOut)
            {
                throw new TimeoutException(
                    $"no answer from {host}:{port} within {timeout.TotalSeconds:0.#} s");
            }

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
