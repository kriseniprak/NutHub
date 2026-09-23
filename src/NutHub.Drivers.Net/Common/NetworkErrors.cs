using System.Net.Sockets;
using System.Security.Authentication;

namespace NutHub.Drivers.Net.Common;

/// <summary>Classifies and describes the failures of a network exchange with a UPS server.</summary>
internal static class NetworkErrors
{
    /// <summary>
    /// Whether the exception is a communication failure the driver should survive by reconnecting, as opposed to a
    /// programming error that should end the driver run.
    /// </summary>
    public static bool IsTransient(Exception exception) =>
        exception is IOException or SocketException or TimeoutException or AuthenticationException
            or ObjectDisposedException or InvalidDataException;

    /// <summary>A one-line explanation for the web panel and the event log.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            "connection refused (is the server running and listening on this address?)",
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } =>
            "host name not found",
        SocketException { SocketErrorCode: SocketError.TimedOut } => "connection timed out",
        SocketException { SocketErrorCode: SocketError.HostUnreachable or SocketError.NetworkUnreachable } =>
            "host unreachable",
        SocketException { SocketErrorCode: SocketError.ConnectionReset } => "connection reset by the server",
        SocketException s => s.Message,
        IOException { InnerException: SocketException inner } => Describe(inner),
        AuthenticationException a => "TLS negotiation failed: " + (a.InnerException?.Message ?? a.Message),
        _ => exception.Message,
    };
}
