namespace NutHub.Drivers.Serial.Transport;

/// <summary>
/// A byte channel to a serial-line UPS: a local serial port, a TCP serial device server, or a USB-to-serial HID bridge.
/// The protocol code only ever sends a command and reads the reply up to a terminator, so this is all it needs; the
/// narrow surface is what lets the protocols run against an in-memory fake in tests.
/// </summary>
/// <remarks>
/// Not thread-safe: the drivers serialise every exchange (poll, instant command, variable write) themselves.
/// Failures of the link itself (port vanished, connection closed, permission denied) throw
/// <see cref="TransportException"/>; a device that simply does not answer is a timeout, reported through
/// <see cref="TransportRead.Status"/>.
/// </remarks>
internal interface ISerialTransport : IAsyncDisposable
{
    /// <summary>What the user configured, for messages: "COM3", "/dev/ttyUSB0", "tcp://10.0.0.5:2001", "USB 0665:5161".</summary>
    string Description { get; }

    bool IsOpen { get; }

    /// <summary>Opens the link, closing it first when it is already open (reopen after an error).</summary>
    Task OpenAsync(CancellationToken cancellationToken);

    /// <summary>Closes the link; safe to call when it is not open.</summary>
    Task CloseAsync();

    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Reads until one of <paramref name="terminators"/> arrives (the terminator is included in the result), until
    /// <paramref name="timeout"/> elapses (the bytes received so far are returned) or until
    /// <paramref name="maxLength"/> bytes arrived without a terminator (overflow). Bytes after the terminator stay
    /// buffered for the next read.
    /// </summary>
    Task<TransportRead> ReadUntilAsync(ReadOnlyMemory<byte> terminators, TimeSpan timeout, int maxLength,
                                       CancellationToken cancellationToken);

    /// <summary>Discards everything received and not read yet, so a stale reply cannot be taken for the next one.</summary>
    Task DiscardInputAsync(CancellationToken cancellationToken);
}

/// <summary>How a <see cref="ISerialTransport.ReadUntilAsync"/> call ended.</summary>
internal enum ReadStatus
{
    /// <summary>A terminator arrived; it is the last byte of the data.</summary>
    Complete,

    /// <summary>The timeout elapsed; the data holds whatever arrived (possibly nothing).</summary>
    Timeout,

    /// <summary>The maximum length was reached without a terminator: the device is sending garbage.</summary>
    Overflow,
}

/// <summary>The outcome of a read.</summary>
internal readonly record struct TransportRead(ReadStatus Status, byte[] Data)
{
    public bool IsComplete => Status == ReadStatus.Complete;

    /// <summary>The data without the trailing terminator, when there is one.</summary>
    public ReadOnlySpan<byte> Payload => IsComplete && Data.Length > 0 ? Data.AsSpan(0, Data.Length - 1) : Data;
}
