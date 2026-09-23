using System.Net;
using System.Net.Sockets;
using NutHub.Core.Configuration;

namespace NutHub.Protocol.Server;

/// <summary>
/// A normalised listen endpoint: "*" (every interface, IPv6 and IPv4) or a literal address in canonical form, and
/// a port. Two configuration entries that mean the same socket compare equal, so a configuration change rebinds
/// only the endpoints that really changed.
/// </summary>
internal readonly record struct ListenEndpointKey(string Address, int Port)
{
    public const string AnyAddress = "*";

    public bool IsAny => Address == AnyAddress;

    /// <summary>Validates and normalises a configured endpoint. Port 0 (an ephemeral port) is accepted.</summary>
    public static bool TryCreate(ListenEndpoint endpoint, out ListenEndpointKey key, out string? error)
    {
        string address = (endpoint.Address ?? "").Trim();
        key = default;
        if (endpoint.Port is < 0 or > 65535)
        {
            error = $"Invalid NUT listen port {endpoint.Port}.";
            return false;
        }

        if (address == AnyAddress)
        {
            key = new ListenEndpointKey(AnyAddress, endpoint.Port);
            error = null;
            return true;
        }

        if (address.Length > 2 && address[0] == '[' && address[^1] == ']')
        {
            address = address[1..^1];
        }

        if (!IPAddress.TryParse(address, out IPAddress? ip))
        {
            error = $"Invalid NUT listen address '{endpoint.Address}'.";
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        key = new ListenEndpointKey(ip.ToString(), endpoint.Port);
        error = null;
        return true;
    }

    /// <summary>Creates a listening socket for this endpoint.</summary>
    /// <remarks>
    /// "*" is one dual-mode IPv6 socket that also accepts IPv4 clients (as IPv4-mapped addresses); when the machine
    /// has no IPv6, a plain IPv4 socket. A literal IPv6 address, "::" included, listens on IPv6 only, so that it can
    /// coexist with a "0.0.0.0" listener on the same port.
    /// </remarks>
    public Socket CreateSocket(int backlog = 512)
    {
        if (IsAny)
        {
            if (Socket.OSSupportsIPv6)
            {
                try
                {
                    return Bind(IPAddress.IPv6Any, Port, dualMode: true, backlog);
                }
                catch (SocketException ex) when (IsIPv6Unavailable(ex.SocketErrorCode))
                {
                    // IPv6 is compiled in but disabled on this host: IPv4 only.
                }
            }

            return Bind(IPAddress.Any, Port, dualMode: false, backlog);
        }

        return Bind(IPAddress.Parse(Address), Port, dualMode: false, backlog);
    }

    public override string ToString() =>
        IsAny ? $"*:{Port}" : Address.Contains(':') ? $"[{Address}]:{Port}" : $"{Address}:{Port}";

    private static Socket Bind(IPAddress address, int port, bool dualMode, int backlog)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = dualMode;
            }

            socket.Bind(new IPEndPoint(address, port));
            socket.Listen(backlog);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsIPv6Unavailable(SocketError error) =>
        error is SocketError.AddressFamilyNotSupported or SocketError.ProtocolFamilyNotSupported
            or SocketError.ProtocolNotSupported or SocketError.SocketNotSupported or SocketError.AddressNotAvailable;
}
