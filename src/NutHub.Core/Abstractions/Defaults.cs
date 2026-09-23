using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Runtime;

namespace NutHub.Core.Abstractions;

/// <summary>Keeps the last events in memory; replaced by the SQLite store of NutHub.Storage.</summary>
public sealed class InMemoryEventStore : IEventStore
{
    private const int Capacity = 5000;
    private readonly LinkedList<UpsEvent> _events = new();
    private readonly object _lock = new();
    private long _nextId;

    public ValueTask<UpsEvent> AppendAsync(UpsEvent e, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            e = e with { Id = ++_nextId };
            _events.AddFirst(e);
            while (_events.Count > Capacity)
            {
                _events.RemoveLast();
            }
        }

        return ValueTask.FromResult(e);
    }

    public Task<EventPage> QueryAsync(EventQuery query, CancellationToken cancellationToken = default)
    {
        int limit = Math.Clamp(query.Limit, 1, 1000);
        List<UpsEvent> matches;
        lock (_lock)
        {
            matches = _events.Where(e => Matches(e, query)).Take(limit + 1).ToList();
        }

        bool hasMore = matches.Count > limit;
        return Task.FromResult(new EventPage(matches.Take(limit).ToList(), hasMore));
    }

    internal static bool Matches(UpsEvent e, EventQuery q) =>
        (q.Ups is null || string.Equals(e.Ups, q.Ups, StringComparison.OrdinalIgnoreCase)) &&
        (q.MinSeverity is null || e.Severity >= q.MinSeverity) &&
        (q.Category is null || e.Category == q.Category) &&
        (q.From is null || e.Timestamp >= q.From) &&
        (q.To is null || e.Timestamp < q.To) &&
        (q.BeforeId is null || e.Id < q.BeforeId) &&
        (string.IsNullOrEmpty(q.Search) || e.Message.Contains(q.Search, StringComparison.OrdinalIgnoreCase));
}

public sealed class NullHistoryStore : IHistoryStore
{
    public Task<HistoryResult> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(new HistoryResult(query.From, query.To, 0,
                                          new Dictionary<string, IReadOnlyList<HistoryPoint>>()));
}

public sealed class NullNotificationService : INotificationService
{
    public IReadOnlyList<NotificationDelivery> RecentDeliveries => [];

    public Task<CommandResult> SendTestAsync(NotificationChannelKind channel, string? id,
                                             CancellationToken cancellationToken = default) =>
        Task.FromResult(CommandResult.Fail("Notifications are not available in this build."));
}

public sealed class NullHostProtectionService(IConfigStore config) : IHostProtectionService
{
    public HostProtectionStatus GetStatus() =>
        new(false, HostProtectionState.Disabled, null, null, config.Current.HostProtection.DryRun, []);

    public bool CancelPending(CommandOrigin origin) => false;

    public string DefaultShutdownCommand => "";
}

public sealed class NullNutServerStatus : INutServerStatus
{
    public bool Enabled => false;

    public IReadOnlyList<string> Endpoints => [];

    public string? LastError => "The NUT protocol server is not part of this build.";

    public bool TlsAvailable => false;
}

/// <summary>
/// Moves every <see cref="UpsEventMessage"/> into the <see cref="IEventStore"/>, then announces the stored event
/// (with its id) as <see cref="EventRecordedMessage"/>.
/// </summary>
public sealed class EventRecorderService(EventHub hub, IEventStore store, ILogger<EventRecorderService> logger)
    : BackgroundService
{
    private ChannelSubscription? _subscription;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe before returning: ExecuteAsync runs later on its own task, and events published by the
        // services started after this one must not be lost in between.
        _subscription = hub.SubscribeChannel(4096, m => m is UpsEventMessage);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _subscription!.Reader;
        try
        {
            await foreach (HubMessage message in reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await RecordAsync(((UpsEventMessage)message).Event, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Shutting down: keep what is still queued (ServerStopping and the last state changes).
        while (reader.TryRead(out HubMessage? message))
        {
            await RecordAsync(((UpsEventMessage)message).Event, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }

    private async Task RecordAsync(UpsEvent e, CancellationToken cancellationToken)
    {
        try
        {
            UpsEvent stored = await store.AppendAsync(e, cancellationToken).ConfigureAwait(false);
            LogEvent(stored);
            hub.Publish(new EventRecordedMessage(stored));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not record the event '{Message}'.", e.Message);
        }
    }

    private void LogEvent(UpsEvent e)
    {
        LogLevel level = e.Severity switch
        {
            EventSeverity.Critical => LogLevel.Critical,
            EventSeverity.Warning => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        logger.Log(level, "[{Type}] {Message}", e.Type, e.Message);
    }
}
