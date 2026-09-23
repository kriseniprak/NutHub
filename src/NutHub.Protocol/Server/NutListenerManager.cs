using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NutHub.Core.Configuration;

namespace NutHub.Protocol.Server;

/// <summary>
/// The set of listening sockets, kept in line with the configured endpoints. Changing the configuration closes
/// only the listeners whose endpoint went away and opens only the new ones; accepted connections are not affected.
/// Endpoints that cannot be bound (port in use, address not on this machine) are reported and tried again every
/// <see cref="NutProtocolOptions.BindRetryInterval"/>.
/// </summary>
/// <remarks>Not thread-safe: the server serialises calls. The published state can be read at any time.</remarks>
internal sealed class NutListenerManager : IAsyncDisposable
{
    private readonly Action<Socket> _onAccepted;
    private readonly TimeProvider _time;
    private readonly NutProtocolOptions _options;
    private readonly ILogger _logger;
    private readonly Dictionary<ListenEndpointKey, NutListener> _listeners = [];
    private readonly Dictionary<ListenEndpointKey, BindFailure> _failures = [];
    private List<ListenEndpointKey> _desired = [];
    private List<string> _invalid = [];
    private volatile PublishedState _state = PublishedState.Empty;

    public NutListenerManager(Action<Socket> onAccepted, TimeProvider time, NutProtocolOptions options, ILogger logger)
    {
        _onAccepted = onAccepted;
        _time = time;
        _options = options;
        _logger = logger;
    }

    /// <summary>The endpoints actually listening, e.g. "[::]:3493", in configuration order.</summary>
    public IReadOnlyList<string> Endpoints => _state.Endpoints;

    public IReadOnlyList<IPEndPoint> BoundEndPoints => _state.Bound;

    /// <summary>Why some endpoints are not listening, or null when all are.</summary>
    public string? Error => _state.Error;

    public bool HasFailures => _state.HasFailures;

    /// <summary>Listens on exactly these endpoints (none: stops listening).</summary>
    public async Task ApplyAsync(IReadOnlyList<ListenEndpoint> endpoints)
    {
        var desired = new List<ListenEndpointKey>();
        var invalid = new List<string>();
        foreach (ListenEndpoint endpoint in endpoints)
        {
            if (ListenEndpointKey.TryCreate(endpoint, out ListenEndpointKey key, out string? error))
            {
                if (!desired.Contains(key))
                {
                    desired.Add(key);
                }
            }
            else
            {
                invalid.Add(error!);
            }
        }

        _desired = desired;
        _invalid = invalid;

        foreach (ListenEndpointKey key in _listeners.Keys.Where(k => !desired.Contains(k)).ToList())
        {
            NutListener listener = _listeners[key];
            _listeners.Remove(key);
            await listener.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("The NUT server stopped listening on {Endpoint}.", listener.LocalEndPoint);
        }

        foreach (ListenEndpointKey key in _failures.Keys.Where(k => !desired.Contains(k)).ToList())
        {
            _failures.Remove(key);
        }

        foreach (ListenEndpointKey key in desired)
        {
            if (!_listeners.ContainsKey(key))
            {
                TryBind(key);
            }
        }

        Publish();
    }

    /// <summary>Tries again the endpoints that failed, once their retry interval has elapsed.</summary>
    public void RetryFailed()
    {
        if (_failures.Count == 0)
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        foreach (var (key, failure) in _failures.ToList())
        {
            if (now - failure.LastAttempt >= _options.BindRetryInterval)
            {
                TryBind(key);
            }
        }

        Publish();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (NutListener listener in _listeners.Values.ToList())
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }

        _listeners.Clear();
        _failures.Clear();
        _desired = [];
        _invalid = [];
        Publish();
    }

    private void TryBind(ListenEndpointKey key)
    {
        try
        {
            NutListener listener = NutListener.Start(key, _onAccepted, _time, _logger);
            _listeners[key] = listener;
            bool recovered = _failures.Remove(key);
            _logger.LogInformation("The NUT server listens on {Endpoint}{Note}.", listener.LocalEndPoint,
                                   recovered ? " (the earlier problem is solved)" : "");
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or NotSupportedException)
        {
            string reason = Describe(ex);
            bool known = _failures.TryGetValue(key, out BindFailure? previous) && previous.Reason == reason;
            _failures[key] = new BindFailure(reason, _time.GetUtcNow());
            if (known)
            {
                _logger.LogDebug("The NUT server still cannot listen on {Endpoint}: {Reason}.", key, reason);
            }
            else
            {
                _logger.LogError("The NUT server cannot listen on {Endpoint}: {Reason}. Retrying every {Interval} s.",
                                 key, reason, _options.BindRetryInterval.TotalSeconds);
            }
        }
    }

    private static string Describe(Exception ex) => ex is SocketException se
        ? se.SocketErrorCode switch
        {
            SocketError.AddressAlreadyInUse => "the port is already in use by another program",
            SocketError.AddressNotAvailable => "this address does not belong to this machine",
            SocketError.AccessDenied => OperatingSystem.IsWindows()
                ? "access denied"
                : "access denied (ports below 1024 need extra privileges)",
            _ => se.Message,
        }
        : ex.Message;

    private void Publish()
    {
        var bound = new List<IPEndPoint>();
        foreach (ListenEndpointKey key in _desired)
        {
            if (_listeners.TryGetValue(key, out NutListener? listener))
            {
                bound.Add(listener.LocalEndPoint);
            }
        }

        var errors = new List<string>(_invalid);
        foreach (ListenEndpointKey key in _desired)
        {
            if (_failures.TryGetValue(key, out BindFailure? failure))
            {
                errors.Add($"Cannot listen on {key}: {failure.Reason}.");
            }
        }

        _state = new PublishedState(bound.Select(e => e.ToString()).ToArray(), bound.ToArray(),
                                    errors.Count == 0 ? null : string.Join(" ", errors), _failures.Count > 0);
    }

    private sealed record BindFailure(string Reason, DateTimeOffset LastAttempt);

    private sealed record PublishedState(IReadOnlyList<string> Endpoints, IReadOnlyList<IPEndPoint> Bound,
                                         string? Error, bool HasFailures)
    {
        public static readonly PublishedState Empty = new([], [], null, false);
    }
}
