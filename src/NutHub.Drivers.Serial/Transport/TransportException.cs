namespace NutHub.Drivers.Serial.Transport;

/// <summary>Why a transport failed; lets the driver decide whether to retry and what to tell the user.</summary>
internal enum TransportErrorKind
{
    /// <summary>The port, device or host does not exist (adapter unplugged, typo in the name).</summary>
    NotFound,

    /// <summary>The operating system refused access (permissions, Linux groups, udev rules).</summary>
    AccessDenied,

    /// <summary>Another program holds the port or device.</summary>
    Busy,

    /// <summary>A network connection could not be established.</summary>
    ConnectionFailed,

    /// <summary>An open link broke (device removed, remote end closed the connection).</summary>
    ConnectionLost,

    /// <summary>The device cannot be driven through this transport at all.</summary>
    Unsupported,

    /// <summary>Any other input/output error.</summary>
    IoError,
}

/// <summary>
/// A failure of the link to the UPS, with a message written for the person who has to fix it (it ends up in the web
/// panel as the driver message): what is wrong and what to check.
/// </summary>
internal sealed class TransportException : IOException
{
    public TransportException(TransportErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public TransportErrorKind Kind { get; }
}
