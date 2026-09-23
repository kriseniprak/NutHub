using Microsoft.Extensions.Logging;
using NutHub.Drivers.Serial.Common;
using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.Megatec;

/// <summary>A reply to a Q* command, as text with one character per byte (ISO-8859-1).</summary>
/// <param name="Text">The reply, carriage return included when one arrived; empty when the UPS stayed silent.</param>
internal readonly record struct QxAnswer(string Text)
{
    public static readonly QxAnswer None = new(string.Empty);

    public bool IsEmpty => Text.Length == 0;
}

/// <summary>Sends one Q* command and returns the reply (NUT qx_command).</summary>
internal interface IQxLink
{
    Task<QxAnswer> ExchangeAsync(string command, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IQxLink"/> over a transport: drop stale input, send the command, read up to the carriage return. A reply
/// cut short by the timeout is completed with the carriage return the device left out, as NUT does for the USB bridges
/// (the length checks of the fields still reject it when it is really incomplete).
/// </summary>
internal sealed class QxTransportLink : IQxLink
{
    private const int MaxReply = 512;
    private static readonly byte[] CarriageReturn = [(byte)'\r'];

    private readonly ISerialTransport _transport;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;

    public QxTransportLink(ISerialTransport transport, TimeSpan timeout, ILogger logger)
    {
        _transport = transport;
        _timeout = timeout;
        _logger = logger;
    }

    public async Task<QxAnswer> ExchangeAsync(string command, CancellationToken cancellationToken)
    {
        await _transport.DiscardInputAsync(cancellationToken).ConfigureAwait(false);
        await _transport.WriteAsync(CText.Latin1.GetBytes(command), cancellationToken).ConfigureAwait(false);
        TransportRead read = await _transport.ReadUntilAsync(CarriageReturn, _timeout, MaxReply, cancellationToken)
                                             .ConfigureAwait(false);
        string text = CText.Latin1.GetString(read.Data);
        if (read.Status == ReadStatus.Timeout && text.Length > 0)
        {
            text += "\r";
        }

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("{Transport} > {Command} < {Reply} ({Status})", _transport.Description,
                             CText.Printable(command), CText.Printable(text), read.Status);
        }

        return new QxAnswer(text);
    }
}
