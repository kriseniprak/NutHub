using System.IO.Ports;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace NutHub.Drivers.Serial.Transport;

/// <summary>
/// A local serial port through <see cref="SerialPort"/>. Reads poll <see cref="SerialPort.BytesToRead"/> instead of
/// blocking in <see cref="SerialPort.Read(byte[], int, int)"/>: the asynchronous stream API of System.IO.Ports does not
/// honour cancellation on every platform, and a blocked read would pin a thread-pool thread whenever a UPS goes quiet.
/// At the 2400 baud these UPSes use (240 bytes/s) a 10 ms poll costs nothing.
/// </summary>
internal sealed class SerialPortTransport : BufferedTransport
{
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(10);

    private readonly SerialPortSettings _settings;
    private readonly ILogger _logger;
    private SerialPort? _port;

    public SerialPortTransport(SerialPortSettings settings, TimeProvider time, ILogger logger)
        : base(time)
    {
        _settings = settings;
        _logger = logger;
    }

    public override string Description => _settings.PortName;

    public override bool IsOpen => _port is { IsOpen: true };

    protected override Task OpenCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = _settings.PortName;
        EnsureExists(name);

        var port = new SerialPort(name, _settings.BaudRate, _settings.Parity, _settings.DataBits, _settings.StopBits)
        {
            Handshake = Handshake.None,
            ReadTimeout = 500,
            WriteTimeout = 2000,
            DtrEnable = _settings.Dtr,
            RtsEnable = _settings.Rts,
        };

        try
        {
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
        {
            port.Dispose();
            throw TranslateOpenError(name, ex);
        }

        _port = port;
        _logger.LogDebug("Opened serial port {Port} at {Baud} baud (DTR {Dtr}, RTS {Rts}).", name, _settings.BaudRate,
                         _settings.Dtr ? "on" : "off", _settings.Rts ? "on" : "off");
        return Task.CompletedTask;
    }

    protected override Task CloseCoreAsync()
    {
        SerialPort? port = Interlocked.Exchange(ref _port, null);
        if (port is not null)
        {
            try
            {
                port.Close();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The adapter may already be gone; nothing left to release.
                _logger.LogDebug("Closing {Port}: {Message}", _settings.PortName, ex.Message);
            }
            finally
            {
                port.Dispose();
            }
        }

        return Task.CompletedTask;
    }

    protected override Task WriteCoreAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SerialPort port = Port;
        byte[] bytes = data.ToArray();
        try
        {
            // A few bytes go straight into the driver's buffer; WriteTimeout bounds the rare stall.
            port.Write(bytes, 0, bytes.Length);
        }
        catch (TimeoutException ex)
        {
            throw new TransportException(TransportErrorKind.IoError,
                $"Writing to {_settings.PortName} timed out: the port does not send (flow control or a faulty adapter).", ex);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw Lost(ex);
        }

        return Task.CompletedTask;
    }

    protected override async Task<int> ReadSomeAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment) || segment.Array is null)
        {
            throw new ArgumentException("The buffer must be array-backed.", nameof(buffer));
        }

        long started = Time.GetTimestamp();
        while (true)
        {
            SerialPort port = Port;
            int available;
            try
            {
                available = port.BytesToRead;
                if (available > 0)
                {
                    return port.Read(segment.Array, segment.Offset, Math.Min(available, segment.Count));
                }
            }
            catch (TimeoutException)
            {
                // Data announced but not delivered in time: treat as nothing received yet.
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                throw Lost(ex);
            }

            TimeSpan remaining = timeout - Time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return 0;
            }

            await Task.Delay(remaining < PollDelay ? remaining : PollDelay, Time, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override Task DiscardCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            Port.DiscardInBuffer();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw Lost(ex);
        }

        return Task.CompletedTask;
    }

    private SerialPort Port => _port ?? throw new TransportException(TransportErrorKind.ConnectionLost,
                                                                     $"The serial port {_settings.PortName} is not open.");

    private TransportException Lost(Exception ex) =>
        new(TransportErrorKind.ConnectionLost,
            $"The serial port {_settings.PortName} stopped working ({ex.Message}). If it is a USB adapter, check that " +
            "it is still plugged in.", ex);

    private static void EnsureExists(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            string bare = name.StartsWith(@"\\.\", StringComparison.Ordinal) ? name[4..] : name;
            if (!SerialPort.GetPortNames().Contains(bare, StringComparer.OrdinalIgnoreCase))
            {
                throw new TransportException(TransportErrorKind.NotFound,
                    $"The serial port {name} does not exist. Check the port name in Device Manager (Ports (COM & LPT)) " +
                    "and, for a USB adapter, that it is plugged in and its driver is installed.");
            }
        }
        else if (!File.Exists(name))
        {
            throw new TransportException(TransportErrorKind.NotFound,
                $"The serial port {name} does not exist. Check that the adapter is plugged in; for USB adapters prefer " +
                "the stable /dev/serial/by-id/... name, since /dev/ttyUSBn numbers can change after a reboot.");
        }
    }

    private static TransportException TranslateOpenError(string name, Exception ex) => ex switch
    {
        UnauthorizedAccessException when OperatingSystem.IsWindows() => new TransportException(TransportErrorKind.Busy,
            $"The serial port {name} is in use by another program (another UPS service, a terminal program, or a " +
            "second NutHub UPS configured on the same port). Close it and NutHub will retry.", ex),
        UnauthorizedAccessException => new TransportException(TransportErrorKind.AccessDenied,
            $"Permission denied opening {name}. Add the NutHub service user to the group that owns the port " +
            "(usually 'dialout': sudo usermod -aG dialout nuthub) and restart the service.", ex),
        FileNotFoundException => new TransportException(TransportErrorKind.NotFound,
            $"The serial port {name} does not exist.", ex),
        ArgumentException => new TransportException(TransportErrorKind.NotFound,
            $"'{name}' is not a valid serial port name ({ex.Message}).", ex),
        IOException when ex.Message.Contains("busy", StringComparison.OrdinalIgnoreCase) => new TransportException(
            TransportErrorKind.Busy,
            $"The serial port {name} is in use by another program. Close it and NutHub will retry.", ex),
        _ => new TransportException(TransportErrorKind.IoError, $"Cannot open the serial port {name}: {ex.Message}", ex),
    };
}
