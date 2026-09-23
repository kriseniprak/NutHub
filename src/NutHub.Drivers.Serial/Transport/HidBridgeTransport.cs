using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NutHub.Hidraw;

namespace NutHub.Drivers.Serial.Transport;

/// <summary>
/// A USB-to-serial HID bridge (the cypress, phoenix, ippon and sgs communication subdrivers of NUT
/// drivers/nutdrv_qx.c). Commands go out as 8-byte output reports (NUT sends them as SET_REPORT control transfers; the
/// Windows and Linux HID drivers do the same when the device has no interrupt OUT endpoint) and replies come back as
/// input reports.
/// </summary>
/// <remarks>
/// The HID layers only offer blocking reads, so a dedicated background thread reads input reports into a channel; the
/// protocol code then waits on the channel asynchronously, with a timeout, without pinning thread-pool threads.
/// </remarks>
internal sealed class HidBridgeTransport : BufferedTransport
{
    private const int ChunkSize = 8;

    private readonly UsbBridgeSettings _settings;
    private readonly ILogger _logger;
    private readonly IHidBridgeBackend _backend;
    private readonly object _sync = new();
    private IHidBridgePort? _port;
    private Channel<byte[]>? _reports;
    private Thread? _reader;
    private volatile bool _closing;
    private UsbBridgeKind _kind;
    private bool _drainQuirk;
    private int _outputReportLength;
    private string _description;

    /// <param name="backend">The HID layer; null for the one of this system (<see cref="HidBridgeBackend.Create"/>).</param>
    public HidBridgeTransport(UsbBridgeSettings settings, TimeProvider time, ILogger logger, IHidBridgeBackend? backend = null)
        : base(time)
    {
        _settings = settings;
        _logger = logger;
        _backend = backend ?? HidBridgeBackend.Create();
        _description = DescribeSettings(settings);
    }

    public override string Description => _description;

    public override bool IsOpen => _port is not null;

    /// <summary>The framing in use once open; exposed for diagnostics and tests.</summary>
    public UsbBridgeKind Kind => _kind;

    protected override Task OpenCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (HidBridgeDevice device, int inputLength, int outputLength) = FindDevice();
        UsbBridgeInfo? info = UsbBridgeCatalog.Find(device.VendorId, device.ProductId);
        UsbBridgeKind? kind = _settings.Kind ?? info?.Kind;
        if (kind is null)
        {
            throw new TransportException(TransportErrorKind.Unsupported,
                $"The USB device {device.UsbId} cannot be used: " +
                (info?.UnsupportedReason ?? "it is not a known Megatec/Q1 USB bridge; set the USB bridge type explicitly if you know it") + ".");
        }

        _kind = kind.Value;
        _drainQuirk = info?.DrainQuirk ?? false;
        _outputReportLength = outputLength;
        if (_outputReportLength < 2 || inputLength < 2)
        {
            throw new TransportException(TransportErrorKind.Unsupported,
                $"The USB device {device.UsbId} does not declare HID input and output " +
                "reports, so it cannot be driven through the operating system's HID driver (it needs raw USB access).");
        }

        IHidBridgePort port = _backend.Open(device);
        _description = $"USB {device.UsbId} ({_kind.ToString().ToLowerInvariant()})";
        var reports = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        lock (_sync)
        {
            _closing = false;
            _port = port;
            _reports = reports;
            _reader = new Thread(() => ReaderLoop(port, reports.Writer, inputLength))
            {
                IsBackground = true,
                Name = "NutHub USB bridge reader " + _description,
            };
            _reader.Start();
        }

        _logger.LogDebug("Opened {Device} (output report {Out} bytes, input report {In} bytes).", _description,
                         _outputReportLength, inputLength);
        return Task.CompletedTask;
    }

    protected override Task CloseCoreAsync()
    {
        IHidBridgePort? port;
        Thread? reader;
        lock (_sync)
        {
            _closing = true;
            port = _port;
            reader = _reader;
            _port = null;
            _reader = null;
            _reports = null;
        }

        if (port is not null)
        {
            try
            {
                // A read blocked on the reader thread returns within its timeout and then finds the port closed.
                port.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _logger.LogDebug("Closing {Device}: {Message}", _description, ex.Message);
            }
        }

        reader?.Join(TimeSpan.FromSeconds(2));
        return Task.CompletedTask;
    }

    protected override async Task WriteCoreAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        IHidBridgePort port = _port ?? throw Lost(null);
        foreach (byte[] report in BuildReports(data.Span, _kind, _outputReportLength))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await Task.Run(() => port.Write(report), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
            {
                throw Lost(ex);
            }
        }
    }

    protected override async Task<int> ReadSomeAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Channel<byte[]> reports = _reports ?? throw Lost(null);
        using var timer = new CancellationTokenSource(timeout, Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        while (true)
        {
            byte[] report;
            try
            {
                report = await reports.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return 0;
            }
            catch (ChannelClosedException ex)
            {
                throw Lost(ex.InnerException);
            }

            byte[] payload = DecodeReport(report, _kind);
            if (payload.Length == 0)
            {
                continue;
            }

            int count = Math.Min(payload.Length, buffer.Length);
            payload.AsSpan(0, count).CopyTo(buffer.Span);
            return count;
        }
    }

    protected override async Task DiscardCoreAsync(CancellationToken cancellationToken)
    {
        Channel<byte[]> reports = _reports ?? throw Lost(null);
        Drain(reports);
        if (_kind == UsbBridgeKind.Phoenix || _drainQuirk)
        {
            // These converters keep emitting the tail of an earlier reply for a while (NUT flushes them before
            // every command); give them a moment and drop that too, or it would be taken for the next reply.
            await Task.Delay(TimeSpan.FromMilliseconds(30), Time, cancellationToken).ConfigureAwait(false);
            Drain(reports);
        }
    }

    /// <summary>Splits serial bytes into output reports (report id 0 first, as the HID layers expect).</summary>
    internal static IEnumerable<byte[]> BuildReports(ReadOnlySpan<byte> data, UsbBridgeKind kind, int reportLength)
    {
        var reports = new List<byte[]>();
        int perReport = kind == UsbBridgeKind.Sgs ? ChunkSize - 1 : ChunkSize;
        int offset = 0;
        do
        {
            int count = Math.Min(perReport, data.Length - offset);
            var report = new byte[Math.Max(reportLength, ChunkSize + 1)];
            if (kind == UsbBridgeKind.Sgs)
            {
                report[1] = (byte)count;
                data.Slice(offset, count).CopyTo(report.AsSpan(2));
            }
            else
            {
                data.Slice(offset, count).CopyTo(report.AsSpan(1));
            }

            reports.Add(report);
            offset += count;
        }
        while (offset < data.Length);

        return reports;
    }

    /// <summary>
    /// Extracts the serial bytes from an input report (report id first). Mirrors the reply handling of the NUT
    /// cypress/phoenix, ippon and sgs command functions.
    /// </summary>
    internal static byte[] DecodeReport(ReadOnlySpan<byte> report, UsbBridgeKind kind)
    {
        if (report.Length <= 1)
        {
            return [];
        }

        ReadOnlySpan<byte> data = report[1..];
        switch (kind)
        {
            case UsbBridgeKind.Sgs:
            {
                int count = Math.Min(data[0], (byte)(ChunkSize - 1));
                count = Math.Min(count, data.Length - 1);
                return data.Slice(1, count).ToArray();
            }

            case UsbBridgeKind.Ippon:
            {
                // The whole reply is in this one report: up to the CR, else up to a NUL, else everything; the NUT
                // driver then adds the CR the device may have left out.
                int cr = data.IndexOf((byte)'\r');
                int length = cr >= 0 ? cr + 1 : (data.IndexOf((byte)0) is var nul and >= 0 ? nul : data.Length);
                byte[] reply = data[..length].ToArray();
                return cr >= 0 ? reply : [.. reply, (byte)'\r'];
            }

            default:
            {
                // 8 bytes of the serial stream; anything after the CR is padding.
                int cr = data.IndexOf((byte)'\r');
                return (cr >= 0 ? data[..(cr + 1)] : data).ToArray();
            }
        }
    }

    /// <summary>
    /// The Linux message for a hidraw node the service may not open; <paramref name="errno"/> is what open() returned
    /// when known (EPERM: a device cgroup refused it; EACCES: its file mode).
    /// </summary>
    internal static TransportException LinuxAccessDenied(string usbId, string path, int errno = 0, bool? inContainer = null)
    {
        string denied = $"Cannot open the USB device {usbId} ({path})";
        string message = errno == Errno.EPerm
            ? $"{denied}: {Errno.Describe(errno)}. {HidrawAccess.DeviceCgroupAdvice(path)}"
            : inContainer ?? HidrawAccess.InContainer
                ? $"{denied}: {(errno == 0 ? "permission denied" : Errno.Describe(errno))}. " +
                  HidrawAccess.ContainerPermissionAdvice(path, "packaging/linux/99-nuthub-ups.rules")
                : $"{denied}: {(errno == 0 ? "permission denied or in use" : Errno.Describe(errno))}. Give the NutHub " +
                  "service user access to the hidraw device (a udev rule granting it to the service group), then replug " +
                  "the UPS; make sure NUT or another UPS program is not using it.";
        return new TransportException(TransportErrorKind.AccessDenied, message);
    }

    private void ReaderLoop(IHidBridgePort port, ChannelWriter<byte[]> writer, int inputLength)
    {
        var buffer = new byte[inputLength];
        try
        {
            while (!_closing)
            {
                int read = port.Read(buffer, TimeSpan.FromSeconds(1));
                if (read > 0)
                {
                    writer.TryWrite(buffer.AsSpan(0, read).ToArray());
                }
            }

            writer.TryComplete();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            writer.TryComplete(_closing ? null : ex);
        }
    }

    private static void Drain(Channel<byte[]> reports)
    {
        while (reports.Reader.TryRead(out _))
        {
        }
    }

    /// <summary>The device to open, with its input and output report lengths.</summary>
    private (HidBridgeDevice Device, int Input, int Output) FindDevice()
    {
        List<HidBridgeDevice> candidates;
        try
        {
            candidates = _backend.GetDevices(_settings.VendorId, _settings.ProductId).ToList();
        }
        catch (Exception ex) when (IsHidPlatformFailure(ex))
        {
            throw new TransportException(TransportErrorKind.IoError, $"Cannot list the USB HID devices: {ex.Message}", ex);
        }

        IEnumerable<HidBridgeDevice> matching = candidates.Where(d =>
            (_settings.VendorId is not null || UsbBridgeCatalog.Find(d.VendorId, d.ProductId)?.IsSupported == true) &&
            (_settings.SerialNumber is null ||
             string.Equals(_backend.GetSerialNumber(d), _settings.SerialNumber, StringComparison.OrdinalIgnoreCase)));

        // A device may expose several HID collections; the one to talk to has both input and output reports.
        (HidBridgeDevice? device, (int Input, int Output) lengths) = matching
            .Select(d => (Device: d, Lengths: _backend.GetReportLengths(d)))
            .OrderByDescending(d => d.Lengths.Output > 1 && d.Lengths.Input > 1)
            .FirstOrDefault();
        if (device is null)
        {
            string what = _settings.VendorId is null ? "No known Megatec/Q1 USB bridge" : $"No USB device {DescribeIds()}";
            string serial = _settings.SerialNumber is null ? "" : $" with serial number '{_settings.SerialNumber}'";
            throw new TransportException(TransportErrorKind.NotFound,
                $"{what}{serial} is connected. Check the USB cable" +
                (OperatingSystem.IsLinux() ? " and that the device appears in 'lsusb'." : " and Device Manager (Human Interface Devices)."));
        }

        return (device, lengths.Input, lengths.Output);
    }

    private TransportException Lost(Exception? ex) =>
        new(TransportErrorKind.ConnectionLost,
            ex is null
                ? $"{_description} is not open."
                : $"{_description} stopped working ({ex.Message}); check that the USB cable is still connected.",
            ex);

    private string DescribeIds() =>
        _settings.ProductId is null
            ? $"with vendor id {_settings.VendorId:x4}"
            : $"{_settings.VendorId:x4}:{_settings.ProductId:x4}";

    private static string DescribeSettings(UsbBridgeSettings settings) =>
        settings.VendorId is null
            ? "USB (first known bridge)"
            : settings.ProductId is null
                ? string.Create(CultureInfo.InvariantCulture, $"USB {settings.VendorId:x4}:*")
                : string.Create(CultureInfo.InvariantCulture, $"USB {settings.VendorId:x4}:{settings.ProductId:x4}");

    /// <summary>
    /// Failures of the HID layer itself rather than of one device: no hidraw/libudev on this Linux, a platform HidSharp
    /// does not support, permissions on the device list. They must not escape as unexpected exceptions.
    /// </summary>
    internal static bool IsHidPlatformFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException
            or NotSupportedException or DllNotFoundException or EntryPointNotFoundException or TypeInitializationException;
}
