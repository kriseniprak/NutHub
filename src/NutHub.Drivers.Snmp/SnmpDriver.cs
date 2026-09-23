using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Snmp.Engine;
using NutHub.Drivers.Snmp.Transport;

namespace NutHub.Drivers.Snmp;

/// <summary>
/// One UPS behind an SNMP agent. Connecting means detecting the MIB and reading every object once; then the
/// device is polled every poll interval. Any failure (timeout, authentication, agent that no longer implements the
/// MIB) drops the connection, is reported once, and is followed by a new connection attempt after a growing delay,
/// so a card that reboots or is replaced is picked up again without restarting the driver.
/// </summary>
internal sealed class SnmpDriver : IUpsDriver
{
    private readonly SnmpSettings _settings;
    private readonly ILogger _logger;
    private readonly Func<SnmpSettings, TimeProvider, ILogger, ISnmpTransport> _transportFactory;

    // Serialises polls, commands and writes: the session's caches are not thread-safe, and a command must not be
    // interleaved with the objects of a poll on agents that handle one request at a time.
    private readonly SemaphoreSlim _deviceLock = new(1, 1);

    private ISnmpTransport? _transport;
    private MibSession? _session;
    private bool _disposed;

    public SnmpDriver(SnmpSettings settings, ILogger logger,
                      Func<SnmpSettings, TimeProvider, ILogger, ISnmpTransport>? transportFactory = null)
    {
        _settings = settings;
        _logger = logger;
        _transportFactory = transportFactory ?? ((s, time, log) => new SnmpTransport(s, time, log));
    }

    /// <summary>The MIB of the current connection, for diagnostics and tests; null while disconnected.</summary>
    internal string? CurrentMib => Volatile.Read(ref _session)?.Mib.Name;

    public async Task RunAsync(IDriverContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        TimeProvider time = context.TimeProvider;
        context.ReportConnecting($"Connecting to {_settings.Target} (SNMP {_settings.VersionText})");

        int failures = 0;
        string? reportedReason = null;
        try
        {
            while (true)
            {
                try
                {
                    await ConnectAsync(time, cancellationToken).ConfigureAwait(false);
                    failures = 0;
                    reportedReason = null;
                    while (true)
                    {
                        DriverUpdate update = await PollAsync(cancellationToken).ConfigureAwait(false);
                        context.Publish(update);
                        await Task.Delay(context.PollInterval, time, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not DriverConfigurationException)
                {
                    await DisconnectAsync().ConfigureAwait(false);
                    string reason = Describe(ex);
                    if (reason != reportedReason)
                    {
                        context.ReportDisconnected(reason);
                        reportedReason = reason;
                    }
                    else
                    {
                        _logger.LogDebug("{Target}: still failing: {Reason}", _settings.Target, reason);
                    }
                }

                IReadOnlyList<TimeSpan> delays = _settings.ReconnectDelays;
                TimeSpan delay = delays.Count == 0 ? context.PollInterval : delays[Math.Min(failures, delays.Count - 1)];
                failures++;
                await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
    }

    public async Task<CommandResult> InstantCommandAsync(string command, string? parameter,
                                                         CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        return await WithSessionAsync(
            s => s.ExecuteCommandAsync(command, parameter, cancellationToken), command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommandResult> SetVariableAsync(string name, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        return await WithSessionAsync(
            s => s.SetVariableAsync(name, value, cancellationToken), name, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
    }

    private async Task ConnectAsync(TimeProvider time, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ISnmpTransport transport = _transportFactory(_settings, time, _logger);
        try
        {
            var client = new SnmpClient(transport, _settings.MaxObjectsPerRequest, _logger);
            MibDetector.Result detected = await MibDetector.DetectAsync(client, _settings.Mib, _logger, cancellationToken)
                .ConfigureAwait(false);
            var session = new MibSession(client, detected.Mib, _settings, _logger);
            await session.InitializeAsync(cancellationToken).ConfigureAwait(false);

            await _deviceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _transport = transport;
                Volatile.Write(ref _session, session);
            }
            finally
            {
                _deviceLock.Release();
            }

            _logger.LogInformation(
                "Connected to {Target} (SNMP {Version}, sysObjectID {SysObjectId}): {Mib} MIB {MibVersion}, {Commands} commands.",
                _settings.Target, _settings.VersionText, detected.SysObjectId ?? "none", detected.Mib.Name,
                detected.Mib.Version, session.Commands.Count);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<DriverUpdate> PollAsync(CancellationToken cancellationToken)
    {
        await _deviceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MibSession session = _session ?? throw new InvalidOperationException("Not connected.");
            return await session.PollAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    private async Task<CommandResult> WithSessionAsync(Func<MibSession, Task<CommandResult>> action, string what,
                                                       CancellationToken cancellationToken)
    {
        await _deviceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not { } session)
            {
                return CommandResult.NotConnected($"The driver is not connected to {_settings.Target}.");
            }

            return await action(session).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SnmpNoResponseException or SnmpAuthenticationException or SnmpProtocolException
                                       or SnmpException or SocketException)
        {
            _logger.LogWarning("{Target}: {What} failed: {Message}", _settings.Target, what, ex.Message);
            return CommandResult.Fail(Describe(ex));
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    private async Task DisconnectAsync()
    {
        ISnmpTransport? transport;
        await _deviceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            transport = _transport;
            _transport = null;
            Volatile.Write(ref _session, null);
        }
        finally
        {
            _deviceLock.Release();
        }

        if (transport is not null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The message shown to the user for a failure: what happened and what to check.</summary>
    private string Describe(Exception ex)
    {
        string target = _settings.Target;
        switch (ex)
        {
            case SnmpNoResponseException { Detail: { } detail }:
                return $"No answer from {target}: {detail} (check the address and port, and that SNMP is enabled on the card)";
            case SnmpNoResponseException:
                return $"No answer from {target} (check address, community / credentials, and that SNMP is enabled on the card)";
            case SnmpAuthenticationException or SnmpMibNotFoundException:
                return ex.Message;
            case SnmpProtocolException or SnmpErrorStatusException:
                return $"{target}: {ex.Message}";
            case SocketException socket when socket.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData:
                return $"Cannot resolve the host name {_settings.Host}: {socket.Message}";
            case SocketException socket:
                return $"Cannot reach {target}: {socket.Message}";
            case SnmpException snmp:
                return $"{target} sent a message that cannot be decoded: {snmp.Message}";
            default:
                _logger.LogError(ex, "Unexpected error while talking to {Target}.", target);
                return $"Error while talking to {target}: {ex.Message}";
        }
    }
}
