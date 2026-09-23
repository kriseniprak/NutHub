using System.Text;
using Microsoft.Extensions.Logging;
using NutHub.Drivers.Serial.Common;
using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.ApcSmart;

/// <summary>How a line is read from an APC Smart UPS (the SER_* read flags of NUT drivers/apcsmart.h).</summary>
[Flags]
internal enum ApcReadMode
{
    Default = 0,

    /// <summary>Handle the alert characters (on battery, low battery...) found in the input (SER_AA).</summary>
    AlertAware = 1,

    /// <summary>Reading the capability string: '#' is data there (SER_CC).</summary>
    CapabilityCheck = 2,

    /// <summary>Reading the command set: nothing is filtered (SER_CS).</summary>
    CommandSet = 4,

    /// <summary>Silence is an acceptable answer (SER_TO).</summary>
    TimeoutAllowed = 8,

    /// <summary>'*' ends the read and means "OK" (older models answer shutdown commands with it; SER_HA).</summary>
    HandleAsterisk = 16,
}

/// <summary>A line read from the UPS: <see cref="Ok"/> false for a read error, a silent UPS or garbage.</summary>
internal readonly record struct ApcLine(bool Ok, string Text)
{
    public static readonly ApcLine Failed = new(false, string.Empty);

    public bool IsNotAvailable => Text == "NA";
}

/// <summary>
/// The byte-level conversation with an APC Smart UPS: single-character commands, replies ending in CR LF, and alert
/// characters that the UPS may send at any time. A port of apc_read_i, apc_write*, apc_flush of NUT
/// drivers/apcsmart.c.
/// </summary>
internal sealed class ApcSmartLink
{
    /// <summary>Wait for the rest of a reply that was cut short, and the "peek" window before a repeated command.</summary>
    public static readonly TimeSpan PeekTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>The short wait used around smart mode and shutdown commands (SER_D1).</summary>
    public static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(1500);

    private const string IgnoredAlertAware = "\r|&";
    private const string AlertCharacters = "$!%+#?=";
    private const string IgnoredDefault = IgnoredAlertAware + AlertCharacters;
    private const string IgnoredCapabilities = "\r|&$!%+?=";
    private const int MaxLine = 512;

    private static readonly byte[] LineEnd = [(byte)'\n'];
    private static readonly byte[] LineEndOrAsterisk = [(byte)'\n', (byte)'*'];
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(1300);
    private static readonly TimeSpan PaceDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(6);

    private readonly ISerialTransport _transport;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly Action<char> _onAlert;
    private readonly ILogger _logger;

    /// <param name="timeout">The normal reply timeout (NUT: 3 s).</param>
    /// <param name="onAlert">Called for every alert character met in an alert-aware read.</param>
    public ApcSmartLink(ISerialTransport transport, TimeProvider time, TimeSpan timeout, Action<char> onAlert, ILogger logger)
    {
        _transport = transport;
        _time = time;
        _timeout = timeout;
        _onAlert = onAlert;
        _logger = logger;
    }

    public string Description => _transport.Description;

    public Task WriteAsync(char code, CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("{Transport} > {Command}", _transport.Description, CText.Printable(code.ToString()));
        }

        return _transport.WriteAsync(new[] { (byte)code }, cancellationToken);
    }

    /// <summary>Writes raw bytes (dual-byte commands).</summary>
    public Task WriteAsync(byte[] data, CancellationToken cancellationToken) => _transport.WriteAsync(data, cancellationToken);

    /// <summary>
    /// Sends a command twice, 1.3 s apart, as the UPS requires for the power commands; anything the UPS says in between
    /// means it refused, and the second character is then not sent (NUT apc_write_rep).
    /// </summary>
    public async Task<bool> WriteRepeatedAsync(char code, CancellationToken cancellationToken)
    {
        await WriteAsync(code, cancellationToken).ConfigureAwait(false);
        ApcLine peek = await ReadAsync(ApcReadMode.TimeoutAllowed, PeekTimeout, cancellationToken).ConfigureAwait(false);
        if (!peek.Ok || peek.Text.Length > 0)
        {
            return false;
        }

        await Task.Delay(RepeatDelay, _time, cancellationToken).ConfigureAwait(false);
        await WriteAsync(code, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Sends a multi-character command ("@000", "-UPS_ID\r\r\r"): the first character, a check that the UPS did not
    /// answer NA (otherwise the rest would be taken for commands), then the rest paced 50 ms apart (NUT apc_write_long).
    /// </summary>
    public async Task<bool> WriteLongAsync(string code, CancellationToken cancellationToken)
    {
        await WriteAsync(code[0], cancellationToken).ConfigureAwait(false);
        ApcLine peek = await ReadAsync(ApcReadMode.TimeoutAllowed, PeekTimeout, cancellationToken).ConfigureAwait(false);
        if (!peek.Ok || peek.Text.Length > 0)
        {
            return false;
        }

        for (int i = 1; i < code.Length; i++)
        {
            await _transport.WriteAsync(new[] { (byte)code[i] }, cancellationToken).ConfigureAwait(false);
            await Task.Delay(PaceDelay, _time, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Drops pending input before a command. In alert-aware mode the pending bytes are read and their alerts handled
    /// first, so an "on battery" alert is not lost (NUT apc_flush).
    /// </summary>
    public async Task FlushAsync(bool alertAware, CancellationToken cancellationToken)
    {
        if (!alertAware)
        {
            await _transport.DiscardInputAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        for (int i = 0; i < 16; i++)
        {
            TransportRead read = await _transport.ReadUntilAsync(LineEnd, TimeSpan.FromMilliseconds(20), MaxLine, cancellationToken)
                                                 .ConfigureAwait(false);
            if (read.Data.Length == 0)
            {
                return;
            }

            Filter(read.Status == ReadStatus.Complete ? read.Data.AsSpan(0, read.Data.Length - 1) : read.Data,
                   ApcReadMode.AlertAware);
        }

        await _transport.DiscardInputAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one line (NUT apc_read_i).</summary>
    /// <param name="timeout">Null for the default: the configured reply timeout, 6 s for capability and command-set reads.</param>
    public async Task<ApcLine> ReadAsync(ApcReadMode mode, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        bool asterisk = (mode & ApcReadMode.HandleAsterisk) != 0;
        TimeSpan wait = timeout ?? ((mode & (ApcReadMode.CapabilityCheck | ApcReadMode.CommandSet)) != 0 ? LongTimeout : _timeout);
        TransportRead read = await _transport.ReadUntilAsync(asterisk ? LineEndOrAsterisk : LineEnd, wait, MaxLine, cancellationToken)
                                             .ConfigureAwait(false);
        if (read.Status == ReadStatus.Overflow)
        {
            _logger.LogDebug("{Transport}: reply too long, discarded.", _transport.Description);
            await _transport.DiscardInputAsync(cancellationToken).ConfigureAwait(false);
            return ApcLine.Failed;
        }

        bool complete = read.Status == ReadStatus.Complete;
        if (complete && asterisk && read.Data[^1] == (byte)'*')
        {
            // Older models acknowledge a shutdown with '*'; some also send "OK": give it a moment and drop it.
            await _transport.ReadUntilAsync(LineEnd, TimeSpan.FromSeconds(1), MaxLine, cancellationToken).ConfigureAwait(false);
            return new ApcLine(true, "OK");
        }

        string text = Filter(complete ? read.Data.AsSpan(0, read.Data.Length - 1) : read.Data, mode);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("{Transport} < {Reply} ({Status})", _transport.Description,
                             CText.Printable(CText.Latin1.GetString(read.Data)), read.Status);
        }

        if (complete)
        {
            return new ApcLine(true, text);
        }

        // A reply cut short is an error; plain silence is only acceptable when the caller says so.
        return text.Length == 0 && (mode & ApcReadMode.TimeoutAllowed) != 0 ? new ApcLine(true, string.Empty) : ApcLine.Failed;
    }

    /// <summary>Removes the characters to ignore and hands alert characters to the status tracking.</summary>
    private string Filter(ReadOnlySpan<byte> data, ApcReadMode mode)
    {
        string ignore;
        string alerts = string.Empty;
        if ((mode & ApcReadMode.CapabilityCheck) != 0)
        {
            ignore = IgnoredCapabilities;
        }
        else if ((mode & ApcReadMode.CommandSet) != 0)
        {
            // Everything is data in the command set, alert characters included; only the CR the tty layer drops in
            // NUT (IGNCR) goes.
            ignore = "\r";
        }
        else if ((mode & ApcReadMode.AlertAware) != 0)
        {
            ignore = IgnoredAlertAware;
            alerts = AlertCharacters;
        }
        else
        {
            ignore = IgnoredDefault;
        }

        var sb = new StringBuilder(data.Length);
        foreach (byte b in data)
        {
            char c = (char)b;
            if (c == '*' || ignore.Contains(c, StringComparison.Ordinal))
            {
                continue;
            }

            if (alerts.Contains(c, StringComparison.Ordinal))
            {
                _onAlert(c);
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
