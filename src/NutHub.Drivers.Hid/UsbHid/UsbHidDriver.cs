using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Hid.Transport;

namespace NutHub.Drivers.Hid.UsbHid;

/// <summary>Delays and limits of the usbhid driver; tests shorten them.</summary>
internal sealed record UsbHidTimings
{
    public static UsbHidTimings Default { get; } = new();

    /// <summary>Pause between connection attempts after the UPS was not found or was lost.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long one wait for an input report may hold the device thread; bounds the latency of queued work.</summary>
    public TimeSpan InterruptWait { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The least time a full reading may take before the UPS counts as hung. A dead device can make every feature
    /// request wait for the USB stack's own timeout (5 s on Linux), so this must exceed a few of those.
    /// </summary>
    public TimeSpan MinimumReadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait for an instant command or a variable write.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Failed attempts in a row before the USB device is reset; 0 never resets.</summary>
    public int ResetAfterFailures { get; init; } = 3;

    /// <summary>The least time between two resets of the same device, so a device that is gone is not reset in a loop.</summary>
    public TimeSpan ResetInterval { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// The usbhid driver for one UPS: finds the device, reads it through a <see cref="HidUpsSession"/> every poll
/// interval, applies input reports as they arrive, and reconnects after an unplug or a hang. Every device call runs
/// on the connection's own <see cref="DeviceWorker"/> thread, which serialises polls, input reports, commands and
/// writes. Updates are published only from <see cref="RunAsync"/>, in the order they were read.
/// </summary>
internal sealed class UsbHidDriver : IUpsDriver
{
    private readonly string _upsName;
    private readonly IHidDeviceSource _source;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly UsbHidTimings _timings;
    private UsbHidSettings _settings;
    private volatile Connection? _connection;
    private volatile HidDeviceInfo? _lastDevice;
    private DateTimeOffset? _lastReset;
    private long _version;
    private long _lastPublished;
    private int _disposed;

    public UsbHidDriver(string upsName, UsbHidSettings settings, IHidDeviceSource source, ILogger logger, TimeProvider time,
                        UsbHidTimings? timings = null)
    {
        _upsName = upsName ?? throw new ArgumentNullException(nameof(upsName));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _timings = timings ?? UsbHidTimings.Default;
    }

    public async Task RunAsync(IDriverContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var link = new DriverLinkReporter(context, _logger, _upsName);
        context.ReportConnecting("Looking for the USB UPS.");
        int failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            Connection? connection = null;
            bool hung = false;
            try
            {
                connection = await ConnectAsync(context.PollInterval, cancellationToken).ConfigureAwait(false);
                _connection = connection;
                PinDevice(connection.Session.Device);
                _logger.LogInformation("{Ups}: connected to {Device} with the '{Subdriver}' subdriver{Interrupt}.", _upsName,
                                       connection.Session.Description, connection.Session.Subdriver.Id,
                                       connection.Session.UsesInterruptPipe ? " (input reports enabled)" : "");
                Publish(context, connection.Initial);
                link.Connected();
                failures = 0;
                await PollAsync(connection, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (UsbHidUnavailableException ex)
            {
                link.Problem(ex.Message);
            }
            catch (TimeoutException)
            {
                hung = true;
                link.Problem("The UPS stopped answering; reconnecting.");
            }
            catch (UnauthorizedAccessException ex)
            {
                link.Problem(connection is null ? ex.Message : UsbHidErrors.AccessDenied(connection.Session.Device, ex));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
            {
                link.Problem(ex is HidDeviceLostException
                    ? $"Communication with the UPS was lost: {ex.Message}"
                    : $"Communication with the UPS failed: {ex.Message}");
            }
            finally
            {
                if (connection is not null)
                {
                    _connection = null;
                    await DisposeConnectionAsync(connection, hung).ConfigureAwait(false);
                }
            }

            try
            {
                await ResetDeviceIfStuckAsync(++failures, cancellationToken).ConfigureAwait(false);
                await Task.Delay(_timings.RetryDelay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// After several failed attempts in a row, asks the kernel to re-enumerate the device, which is what the scripts
    /// people run around NUT do by hand: a UPS that hangs while it stays plugged in comes back, and one that is
    /// really gone is unaffected. At most one reset per <see cref="UsbHidTimings.ResetInterval"/>, so a device that
    /// has been unplugged is not reset in a loop, and never while the option is off.
    /// </summary>
    private async Task ResetDeviceIfStuckAsync(int failures, CancellationToken cancellationToken)
    {
        int threshold = _timings.ResetAfterFailures;
        if (!_settings.UsbReset || threshold <= 0 || failures < threshold || _lastDevice is not { } device)
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        if (_lastReset is { } last && now - last < _timings.ResetInterval)
        {
            return;
        }

        _lastReset = now;
        (bool done, string detail) = await Task.Run(() =>
        {
            bool ok = _source.TryReset(device, out string text);
            return (ok, text);
        }, cancellationToken).ConfigureAwait(false);

        if (done)
        {
            _logger.LogWarning("{Ups}: the UPS has not answered {Failures} times; the USB device was reset ({Node}).",
                               _upsName, failures, detail);
        }
        else
        {
            _logger.LogInformation("{Ups}: the USB device could not be reset: {Detail}", _upsName, detail);
        }
    }

    public Task<CommandResult> InstantCommandAsync(string command, string? parameter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return RunOnDeviceAsync(session => session.ExecuteCommand(command, parameter), cancellationToken);
    }

    public Task<CommandResult> SetVariableAsync(string name, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        return RunOnDeviceAsync(session => session.WriteVariable(name, value), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Connection? connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            await DisposeConnectionAsync(connection, hung: false).ConfigureAwait(false);
        }
    }

    /// <summary>How long a full reading may take: several poll intervals, and never less than the minimum.</summary>
    private TimeSpan ReadTimeout(TimeSpan pollInterval)
    {
        TimeSpan scaled = pollInterval * 5;
        return scaled > _timings.MinimumReadTimeout ? scaled : _timings.MinimumReadTimeout;
    }

    /// <summary>
    /// After the first connection the driver sticks to that UPS: on reconnection it accepts only the same vendor,
    /// product and serial number, as NUT's reopen matcher does, so an unplug never silently swaps two UPSes.
    /// </summary>
    private void PinDevice(HidDeviceInfo device)
    {
        _settings = _settings with
        {
            VendorId = device.VendorId,
            ProductId = device.ProductId,
            Serial = _settings.Serial ?? device.Serial,
        };
    }

    private async Task<Connection> ConnectAsync(TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        var worker = new DeviceWorker($"usbhid {_upsName}");
        UsbHidSettings settings = _settings;
        Task<(IHidConnection Device, HidUpsSession Session, long Version, DriverUpdate Update)> open = worker.InvokeAsync(() =>
        {
            UsbHidLocateResult located = UsbHidDeviceLocator.Locate(_source, settings, _logger);
            if (located.Candidate is not { } candidate)
            {
                throw new UsbHidUnavailableException(located.Problem ?? "No USB UPS was found.");
            }

            _lastDevice = candidate.Device;
            IHidConnection device;
            try
            {
                device = _source.Open(candidate.Device);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UsbHidUnavailableException(UsbHidErrors.AccessDenied(candidate.Device, ex), ex);
            }

            try
            {
                candidate.Subdriver.Attach(candidate.Device);
                var session = new HidUpsSession(device, candidate.Descriptor, candidate.Subdriver, settings, _time, _logger);
                session.Initialize();
                return (device, session, Interlocked.Increment(ref _version), session.BuildUpdate());
            }
            catch
            {
                device.Dispose();
                throw;
            }
        }, cancellationToken);

        try
        {
            var (device, session, version, update) = await open.WaitAsync(ReadTimeout(pollInterval), _time, cancellationToken)
                                                              .ConfigureAwait(false);
            return new Connection(worker, device, session, (version, update));
        }
        catch
        {
            // A hung open still completes some day; the device it returns must not stay open.
            _ = open.ContinueWith(static t => t.Result.Device.Dispose(), CancellationToken.None,
                                  TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            worker.Stop(open.IsCompleted ? TimeSpan.FromSeconds(1) : TimeSpan.Zero);
            throw;
        }
    }

    private async Task PollAsync(Connection connection, IDriverContext context, CancellationToken cancellationToken)
    {
        HidUpsSession session = connection.Session;
        var events = Channel.CreateUnbounded<(long Version, DriverUpdate Update)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        if (session.UsesInterruptPipe)
        {
            byte[] buffer = new byte[Math.Max(connection.Device.MaxInputReportLength, 64)];
            connection.Worker.Idle = () => ReadInputReport(connection, buffer, events.Writer);
        }

        TimeSpan timeout = ReadTimeout(context.PollInterval);
        using var timer = new PeriodicTimer(context.PollInterval, _time);
        Task<bool>? tick = null;
        Task<bool>? input = null;
        while (true)
        {
            tick ??= timer.WaitForNextTickAsync(cancellationToken).AsTask();
            input ??= events.Reader.WaitToReadAsync(cancellationToken).AsTask();
            Task first = await Task.WhenAny(tick, input, connection.Worker.IdleFault).ConfigureAwait(false);
            if (first == connection.Worker.IdleFault)
            {
                Exception fault = await connection.Worker.IdleFault.ConfigureAwait(false);
                throw fault as HidDeviceLostException ?? new HidDeviceLostException(fault.Message, fault);
            }

            if (first == input)
            {
                await input.ConfigureAwait(false);
                input = null;
                while (events.Reader.TryRead(out var item))
                {
                    Publish(context, item);
                }

                continue;
            }

            await tick.ConfigureAwait(false);
            tick = null;
            var reading = await connection.Worker.InvokeAsync(() =>
            {
                session.Update();
                return (Interlocked.Increment(ref _version), session.BuildUpdate());
            }, cancellationToken).WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);
            Publish(context, reading);
        }
    }

    /// <summary>
    /// The idle work of the device thread: waits briefly for an input report and applies it. Updates that change
    /// something are handed to the poll loop, which publishes them at once instead of at the next poll.
    /// </summary>
    private void ReadInputReport(Connection connection, byte[] buffer, ChannelWriter<(long, DriverUpdate)> events)
    {
        long started = _time.GetTimestamp();
        int length = connection.Device.ReadInput(buffer, _timings.InterruptWait);
        if (length <= 0)
        {
            // Some stacks return at once when nothing is pending; do not spin.
            if (_time.GetElapsedTime(started) < _timings.InterruptWait / 4)
            {
                connection.Worker.WaitForWork(_timings.InterruptWait);
            }

            return;
        }

        try
        {
            if (connection.Session.ProcessInputReport(buffer.AsSpan(0, length)))
            {
                events.TryWrite((Interlocked.Increment(ref _version), connection.Session.BuildUpdate()));
            }
        }
        catch (Exception ex) when (ex is not HidDeviceLostException and not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "{Ups}: ignoring an input report that could not be decoded.", _upsName);
        }
    }

    /// <summary>Publishes a reading unless a newer one already went out (an input report may overtake a poll).</summary>
    private void Publish(IDriverContext context, (long Version, DriverUpdate Update) reading)
    {
        if (reading.Version <= _lastPublished)
        {
            return;
        }

        _lastPublished = reading.Version;
        context.Publish(reading.Update);
    }

    private async Task<CommandResult> RunOnDeviceAsync(Func<HidUpsSession, CommandResult> operation, CancellationToken cancellationToken)
    {
        Connection? connection = _connection;
        if (connection is null)
        {
            return CommandResult.NotConnected("The UPS is not connected.");
        }

        try
        {
            return await connection.Worker.InvokeAsync(() => operation(connection.Session), cancellationToken)
                                   .WaitAsync(_timings.CommandTimeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return CommandResult.NotConnected("The UPS was disconnected.");
        }
        catch (HidDeviceLostException ex)
        {
            return CommandResult.NotConnected($"The UPS was disconnected: {ex.Message}");
        }
        catch (TimeoutException)
        {
            return CommandResult.Fail("The UPS did not answer in time.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CommandResult.Fail($"The UPS did not accept the request: {ex.Message}");
        }
    }

    private async Task DisposeConnectionAsync(Connection connection, bool hung)
    {
        if (!connection.TryBeginDispose())
        {
            return;
        }

        if (hung)
        {
            // The device thread is stuck in a call: closing the handle from here is the only way to release it.
            CloseDevice(connection);
            connection.Worker.Stop(TimeSpan.Zero);
            return;
        }

        try
        {
            // Close the handle on the device thread, so no call is using it; a hung thread gets two seconds.
            await connection.Worker.InvokeAsync(connection.Device.Dispose)
                            .WaitAsync(TimeSpan.FromSeconds(2), _time).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or ObjectDisposedException or IOException)
        {
            // Closing from here unblocks a call stuck in the device, if the platform allows it.
            CloseDevice(connection);
        }
        finally
        {
            connection.Worker.Dispose();
        }
    }

    private void CloseDevice(Connection connection)
    {
        try
        {
            connection.Device.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "{Ups}: closing the device failed.", _upsName);
        }
    }

    /// <summary>One open device with its thread and session.</summary>
    private sealed class Connection(DeviceWorker worker, IHidConnection device, HidUpsSession session, (long, DriverUpdate) initial)
    {
        private int _disposing;

        public DeviceWorker Worker { get; } = worker;

        public IHidConnection Device { get; } = device;

        public HidUpsSession Session { get; } = session;

        /// <summary>The reading made while connecting, published first.</summary>
        public (long Version, DriverUpdate Update) Initial { get; } = initial;

        public bool TryBeginDispose() => Interlocked.Exchange(ref _disposing, 1) == 0;
    }
}

/// <summary>The UPS cannot be reached for a reason the message explains to the user (not found, no permission...).</summary>
internal sealed class UsbHidUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
