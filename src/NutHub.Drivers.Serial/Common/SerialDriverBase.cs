using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.Common;

/// <summary>The outcome of one poll: a complete update, or the reason there is none.</summary>
internal readonly record struct PollResult(DriverUpdate? Update, string? Error)
{
    public static PollResult Ok(DriverUpdate update) => new(update, null);

    public static PollResult Failed(string error) => new(null, error);
}

/// <summary>
/// The connection lifecycle shared by the serial-line drivers: open the link, identify the UPS, poll it every poll
/// interval, count failures, declare the data stale after a few consecutive failures, and start over (reopen the link,
/// identify again) when the link breaks or the UPS stays silent. Instant commands and variable writes are serialised
/// with the poll loop on one lock, because a serial line carries one exchange at a time.
/// </summary>
internal abstract class SerialDriverBase : IUpsDriver
{
    /// <summary>Consecutive failed polls before the data is declared stale (NUT nutdrv_qx uses MAXTRIES = 3).</summary>
    internal const int FailuresBeforeDisconnect = 3;

    /// <summary>Consecutive failed polls after which the link is closed and set up again from scratch.</summary>
    internal const int FailuresBeforeReconnect = 6;

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
    ];

    private readonly SemaphoreSlim _io = new(1, 1);
    private volatile bool _ready;
    private volatile bool _linkBroken;

    protected SerialDriverBase(string upsName, ISerialTransport transport, TimeProvider time, ILogger logger)
    {
        UpsName = upsName;
        Transport = transport;
        Time = time;
        Logger = logger;
    }

    protected string UpsName { get; }

    protected ISerialTransport Transport { get; }

    protected TimeProvider Time { get; }

    protected ILogger Logger { get; }

    /// <summary>True between a successful identification of the UPS and the loss of the link.</summary>
    internal bool IsReady => _ready;

    public async Task RunAsync(IDriverContext context, CancellationToken cancellationToken)
    {
        TimeProvider time = context.TimeProvider;
        int connectFailures = 0;
        bool connectedBefore = false;
        string? lastError = null;
        context.ReportConnecting($"Opening {Transport.Description}");
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? error = await ConnectAsync(cancellationToken).ConfigureAwait(false);
                if (error is not null)
                {
                    connectFailures++;
                    if (error != lastError)
                    {
                        lastError = error;
                        if (connectedBefore)
                        {
                            context.ReportDisconnected(error);
                        }
                        else
                        {
                            Logger.LogWarning("{Ups}: {Error}", UpsName, error);
                            context.ReportConnecting(error);
                        }
                    }

                    await Task.Delay(RetryDelay(connectFailures), time, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (connectedBefore || connectFailures > 0)
                {
                    Logger.LogInformation("{Ups}: connected to the UPS on {Transport}.", UpsName, Transport.Description);
                }

                connectFailures = 0;
                lastError = null;
                connectedBefore = true;
                await PollLoopAsync(context, cancellationToken).ConfigureAwait(false);
                await DisconnectAsync().ConfigureAwait(false);

                // Do not hammer a link that breaks right after opening.
                await Task.Delay(RetryDelays[0], time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
    }

    public Task<CommandResult> InstantCommandAsync(string command, string? parameter, CancellationToken cancellationToken) =>
        RunExclusiveAsync(ct => InstantCommandCoreAsync(command, parameter, ct), cancellationToken);

    public Task<CommandResult> SetVariableAsync(string name, string value, CancellationToken cancellationToken) =>
        RunExclusiveAsync(ct => SetVariableCoreAsync(name, value, ct), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        await Transport.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Opens the link and identifies the UPS; returns null on success, else what went wrong.</summary>
    internal async Task<string?> ConnectAsync(CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ready = false;
            _linkBroken = false;
            try
            {
                await Transport.OpenAsync(cancellationToken).ConfigureAwait(false);
                string? error = await InitializeAsync(cancellationToken).ConfigureAwait(false);
                if (error is null)
                {
                    _ready = true;
                    return null;
                }

                await CloseQuietlyAsync().ConfigureAwait(false);
                return error;
            }
            catch (TransportException ex)
            {
                await CloseQuietlyAsync().ConfigureAwait(false);
                return ex.Message;
            }
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>One poll of the UPS under the I/O lock. A broken link is remembered so the loop reconnects.</summary>
    internal async Task<PollResult> PollAsync(bool recovering, CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_ready)
            {
                return PollResult.Failed("The UPS is not connected.");
            }

            return await PollCoreAsync(recovering, cancellationToken).ConfigureAwait(false);
        }
        catch (TransportException ex)
        {
            _linkBroken = true;
            return PollResult.Failed(ex.Message);
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>Called with the link open; identifies the UPS and reads its static data. Returns null or an error.</summary>
    protected abstract Task<string?> InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the UPS once. <paramref name="recovering"/> is true when the previous poll failed, for protocols that
    /// need to wake the UPS up again first.
    /// </summary>
    protected abstract Task<PollResult> PollCoreAsync(bool recovering, CancellationToken cancellationToken);

    protected abstract Task<CommandResult> InstantCommandCoreAsync(string command, string? parameter, CancellationToken cancellationToken);

    protected abstract Task<CommandResult> SetVariableCoreAsync(string name, string value, CancellationToken cancellationToken);

    /// <summary>Forgets what was learnt about the UPS when the link is closed.</summary>
    protected virtual void OnDisconnected()
    {
    }

    private async Task PollLoopAsync(IDriverContext context, CancellationToken cancellationToken)
    {
        int failures = 0;
        string? lastError = null;
        while (true)
        {
            PollResult result = await PollAsync(failures > 0, cancellationToken).ConfigureAwait(false);
            if (result.Update is not null)
            {
                if (failures >= FailuresBeforeDisconnect)
                {
                    Logger.LogInformation("{Ups}: communication with the UPS re-established.", UpsName);
                }

                failures = 0;
                lastError = null;
                context.Publish(result.Update);
            }
            else
            {
                failures++;
                string error = result.Error ?? "The UPS did not answer.";
                Logger.LogDebug("{Ups}: poll failed ({Failures} in a row): {Error}", UpsName, failures, error);
                if (failures >= FailuresBeforeDisconnect && error != lastError)
                {
                    lastError = error;
                    context.ReportDisconnected(error);
                }

                if (_linkBroken || failures >= FailuresBeforeReconnect)
                {
                    if (!_linkBroken)
                    {
                        Logger.LogInformation("{Ups}: no valid answer in {Count} polls; reopening {Transport}.", UpsName,
                                              failures, Transport.Description);
                    }

                    return;
                }
            }

            await Task.Delay(context.PollInterval, context.TimeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<CommandResult> RunExclusiveAsync(Func<CancellationToken, Task<CommandResult>> operation,
                                                        CancellationToken cancellationToken)
    {
        if (!_ready)
        {
            return CommandResult.NotConnected($"The driver is not connected to the UPS on {Transport.Description}.");
        }

        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_ready)
            {
                return CommandResult.NotConnected($"The driver is not connected to the UPS on {Transport.Description}.");
            }

            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (TransportException ex)
        {
            _linkBroken = true;
            return CommandResult.Fail(ex.Message);
        }
        finally
        {
            _io.Release();
        }
    }

    private async Task DisconnectAsync()
    {
        _ready = false;

        // Let a command in progress finish its exchange, but never wait forever for it.
        bool locked = await _io.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try
        {
            await CloseQuietlyAsync().ConfigureAwait(false);
            OnDisconnected();
        }
        finally
        {
            if (locked)
            {
                _io.Release();
            }
        }
    }

    private async Task CloseQuietlyAsync()
    {
        try
        {
            await Transport.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Logger.LogDebug("{Ups}: closing {Transport}: {Message}", UpsName, Transport.Description, ex.Message);
        }
    }

    private static TimeSpan RetryDelay(int failures) => RetryDelays[Math.Clamp(failures - 1, 0, RetryDelays.Length - 1)];
}
