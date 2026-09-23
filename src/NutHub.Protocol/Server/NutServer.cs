using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NutHub.Core;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Runtime;
using NutHub.Core.Security;
using NutHub.Protocol.Commands;
using NutHub.Protocol.Security;
using NutHub.Protocol.Tracking;

namespace NutHub.Protocol.Server;

/// <summary>
/// The NUT protocol server (what upsd does): listens on the configured endpoints, accepts clients allowed by the
/// access list up to the connection limit, and serves each of them in its own task. It follows configuration changes
/// without a restart: listeners are rebound only when their endpoint changed, existing connections stay, and access
/// rules, TLS and limits apply to the next connection or command.
/// </summary>
internal sealed class NutServer : BackgroundService, INutServerStatus
{
    private readonly IConfigStore _config;
    private readonly TimeProvider _time;
    private readonly NutProtocolOptions _options;
    private readonly ILogger<NutServer> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly ConcurrentDictionary<long, ConnectionEntry> _connections = new();
    private readonly ClientAddressPolicy _policy;
    private readonly NutListenerManager _listeners;
    private readonly NutTlsCertificates _tls;
    private readonly TrackingStore _tracking;
    private readonly LoginThrottle _throttle;
    private readonly NutConnectionServices _connectionServices;
    private readonly LogRateLimiter _refusals;
    private readonly EventHub _hub;
    private int _activeConnections;
    private volatile bool _accepting;

    public NutServer(IConfigStore config, IUpsRegistry registry, NutSessionRegistry sessions, EventHub hub,
                     IPasswordHasher hasher, ISecretProtector secrets, NutHubPaths paths, TimeProvider time,
                     ILoggerFactory loggerFactory, NutProtocolOptions options)
    {
        _config = config;
        _hub = hub;
        _time = time;
        _options = options;
        _logger = loggerFactory.CreateLogger<NutServer>();
        var warnings = new LogRateLimiter(time, TimeSpan.FromMinutes(1));
        _throttle = new LoginThrottle(time, options, loggerFactory.CreateLogger<LoginThrottle>());
        _tracking = new TrackingStore(time, options);
        _tls = new NutTlsCertificates(secrets, paths, time, options, loggerFactory.CreateLogger<NutTlsCertificates>());
        var authorizer = new NutAuthorizer(config, hasher, sessions, _throttle, warnings,
                                           loggerFactory.CreateLogger<NutAuthorizer>());
        var processor = new NutCommandProcessor(config, registry, sessions, hub, authorizer, _tracking, _tls, time,
                                                warnings, loggerFactory.CreateLogger<NutCommandProcessor>(),
                                                _shutdown.Token);
        _policy = new ClientAddressPolicy(_logger);
        _connectionServices = new NutConnectionServices(processor, sessions, hub, time, options,
                                                         loggerFactory.CreateLogger<NutConnection>(), IsAddressAllowed);
        _listeners = new NutListenerManager(OnAccepted, time, options, _logger);
        _refusals = new LogRateLimiter(time, TimeSpan.FromMinutes(1));
    }

    public bool Enabled => _config.Current.Nut.Enabled;

    public IReadOnlyList<string> Endpoints => _listeners.Endpoints;

    public string? LastError
    {
        get
        {
            NutServerSettings nut = _config.Current.Nut;
            string? tlsError = nut.Enabled && nut.Tls.Enabled ? _tls.Error : null;
            string? listenError = _listeners.Error;
            return (listenError, tlsError) switch
            {
                (null, null) => null,
                (not null, null) => listenError,
                (null, not null) => tlsError,
                _ => listenError + " " + tlsError,
            };
        }
    }

    public bool TlsAvailable
    {
        get
        {
            NutServerSettings nut = _config.Current.Nut;
            return nut.Enabled && nut.Tls.Enabled && _tls.Certificate is not null;
        }
    }

    /// <summary>The bound endpoints, with the real port when port 0 was configured (tests).</summary>
    internal IReadOnlyList<IPEndPoint> BoundEndPoints => _listeners.BoundEndPoints;

    internal int ActiveConnections => Volatile.Read(ref _activeConnections);

    internal TrackingStore Tracking => _tracking;

    internal LoginThrottle Throttle => _throttle;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using CancellationTokenRegistration onStop = stoppingToken.Register(() => _shutdown.Cancel());
        using IDisposable hubSubscription = _hub.Subscribe(OnHubMessage);
        _config.Changed += OnConfigChanged;
        try
        {
            await ReconcileAsync().ConfigureAwait(false);
            using var timer = new PeriodicTimer(_options.HousekeepingInterval, _time);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await HousekeepingAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _config.Changed -= OnConfigChanged;
            await ShutdownAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Like upsd (kick_login_clients), a UPS that leaves the configuration takes the clients logged in to it along:
    /// they reconnect and learn at LOGIN that it is gone, instead of polling a UPS that no longer exists.
    /// </summary>
    private void OnHubMessage(HubMessage message)
    {
        if (message is not UpsRemovedMessage removed)
        {
            return;
        }

        foreach (ConnectionEntry entry in _connections.Values)
        {
            if (string.Equals(entry.Connection.Session.LoginUps, removed.Name, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Disconnecting NUT client {Address}: its UPS {Ups} was removed.",
                                       entry.Connection.Session.Address, removed.Name);
                entry.Connection.Abort($"the UPS {removed.Name} was removed");
            }
        }
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        // Never block the thread that saved the configuration.
        _ = Task.Run(ReconcileAsync);
    }

    /// <summary>Applies the current NUT settings: listeners, TLS certificate, access list.</summary>
    private async Task ReconcileAsync()
    {
        await _reconcileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            NutServerSettings nut = _config.Current.Nut;
            if (!nut.Enabled)
            {
                bool wasAccepting = _accepting;
                _accepting = false;
                await _listeners.ApplyAsync([]).ConfigureAwait(false);
                _tls.Apply(new NutTlsSettings { Enabled = false });
                foreach (ConnectionEntry entry in _connections.Values)
                {
                    entry.Connection.Abort("the NUT server was disabled");
                }

                if (wasAccepting)
                {
                    _logger.LogInformation("The NUT server is disabled.");
                }

                return;
            }

            _tls.Apply(nut.Tls);
            await _listeners.ApplyAsync(nut.Listen ?? []).ConfigureAwait(false);
            _accepting = true;

            // Connections accepted under an older access list must follow the new one.
            foreach (ConnectionEntry entry in _connections.Values)
            {
                if (!_policy.IsAllowed(nut, entry.Connection.Address))
                {
                    entry.Connection.Abort("the client address is no longer allowed");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying the NUT server settings failed.");
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task HousekeepingAsync()
    {
        try
        {
            DateTimeOffset now = _time.GetUtcNow();
            foreach (ConnectionEntry entry in _connections.Values)
            {
                if (now - entry.Connection.LastCommandAt >= _options.IdleTimeout)
                {
                    entry.Connection.Abort($"no request for {_options.IdleTimeout.TotalMinutes:0.#} minutes");
                }
            }

            _tracking.Cleanup();
            _throttle.Cleanup();

            await _reconcileGate.WaitAsync().ConfigureAwait(false);
            try
            {
                NutServerSettings nut = _config.Current.Nut;
                if (!_shutdown.IsCancellationRequested && nut.Enabled)
                {
                    _listeners.RetryFailed();
                    _tls.Apply(nut.Tls);
                }
            }
            finally
            {
                _reconcileGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NUT server housekeeping failed.");
        }
    }

    private bool IsAddressAllowed(IPAddress address) => _policy.IsAllowed(_config.Current.Nut, address);

    /// <summary>Called by the accept loops for every new socket; decides before reading anything from it.</summary>
    private void OnAccepted(Socket socket)
    {
        NutServerSettings nut = _config.Current.Nut;
        if (!_accepting || !nut.Enabled || _shutdown.IsCancellationRequested ||
            socket.RemoteEndPoint is not IPEndPoint remote)
        {
            socket.Dispose();
            return;
        }

        IPAddress address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        if (!_policy.IsAllowed(nut, address))
        {
            if (_refusals.ShouldLog("acl:" + address))
            {
                _logger.LogInformation("Refused a NUT connection from {Address}: not in the allowed networks.", address);
            }

            socket.Dispose();
            return;
        }

        if (Interlocked.Increment(ref _activeConnections) > nut.MaxConnections)
        {
            Interlocked.Decrement(ref _activeConnections);
            if (_refusals.ShouldLog("limit"))
            {
                _logger.LogWarning("Refused a NUT connection from {Address}: the limit of {Max} connections is reached.",
                                   address, nut.MaxConnections);
            }

            socket.Dispose();
            return;
        }

        NutConnection connection;
        try
        {
            socket.NoDelay = true;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            connection = new NutConnection(socket, remote, _connectionServices);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
        {
            Interlocked.Decrement(ref _activeConnections);
            _logger.LogDebug(ex, "A NUT connection from {Address} failed before it started.", address);
            socket.Dispose();
            return;
        }

        // The task is created before it starts so that the shutdown always finds it in the table.
        var start = new Task<Task>(() => RunConnectionAsync(connection));
        _connections[connection.Session.Id] = new ConnectionEntry(connection, start.Unwrap());
        start.Start(TaskScheduler.Default);
    }

    private async Task RunConnectionAsync(NutConnection connection)
    {
        try
        {
            await connection.RunAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "NUT connection {Id} ended with an error.", connection.Session.Id);
        }
        finally
        {
            _connections.TryRemove(connection.Session.Id, out _);
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    /// <summary>Stops accepting first, then closes the connections and waits for them.</summary>
    private async Task ShutdownAsync()
    {
        _accepting = false;
        try
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        await _reconcileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _listeners.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _reconcileGate.Release();
        }

        List<ConnectionEntry> entries = _connections.Values.ToList();
        foreach (ConnectionEntry entry in entries)
        {
            entry.Connection.Abort("the server is stopping");
        }

        try
        {
            // A wall-clock bound on purpose: shutdown must finish even if the injected clock does not move.
            await Task.WhenAll(entries.Select(e => e.Task)).WaitAsync(_options.ShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("{Count} NUT connections did not close within {Timeout} s.",
                               entries.Count(e => !e.Task.IsCompleted), _options.ShutdownTimeout.TotalSeconds);
        }

        _logger.LogDebug("The NUT server stopped.");
    }

    private sealed record ConnectionEntry(NutConnection Connection, Task Task);
}
