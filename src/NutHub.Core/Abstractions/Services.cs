using NutHub.Core.Model;

namespace NutHub.Core.Abstractions;

// Contracts between the projects. Core registers a default implementation of each (in memory, or "not available"),
// so every part works on its own; NutHub.Storage, NutHub.Services and NutHub.Protocol replace them.

/// <summary>The event log.</summary>
public interface IEventStore
{
    /// <summary>Records an event and returns it with its <see cref="UpsEvent.Id"/>.</summary>
    ValueTask<UpsEvent> AppendAsync(UpsEvent e, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<EventPage> QueryAsync(EventQuery query, CancellationToken cancellationToken = default);
}

/// <param name="Ups">Only this UPS (null: all, including server events).</param>
/// <param name="MinSeverity">Only this severity and above.</param>
/// <param name="Category">Only this category.</param>
/// <param name="From">Only events at or after this time.</param>
/// <param name="To">Only events before this time.</param>
/// <param name="BeforeId">Paging: only events with a smaller id.</param>
/// <param name="Limit">At most this many (1-1000).</param>
/// <param name="Search">Only events whose message contains this text (case-insensitive).</param>
public sealed record EventQuery(
    string? Ups = null,
    EventSeverity? MinSeverity = null,
    EventCategory? Category = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    long? BeforeId = null,
    int Limit = 100,
    string? Search = null);

public sealed record EventPage(IReadOnlyList<UpsEvent> Items, bool HasMore);

/// <summary>Time series of the numeric UPS variables.</summary>
public interface IHistoryStore
{
    Task<HistoryResult> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default);
}

/// <param name="Ups">The UPS.</param>
/// <param name="Variables">The variables wanted, e.g. "battery.charge".</param>
/// <param name="From">Start of the interval.</param>
/// <param name="To">End of the interval.</param>
/// <param name="MaxPoints">Samples are averaged into at most this many buckets per series.</param>
public sealed record HistoryQuery(string Ups, IReadOnlyList<string> Variables, DateTimeOffset From, DateTimeOffset To,
                                  int MaxPoints = 500);

/// <param name="StepSeconds">The bucket width used.</param>
/// <param name="Series">Per variable, the points in time order; variables without data are absent.</param>
public sealed record HistoryResult(DateTimeOffset From, DateTimeOffset To, int StepSeconds,
                                   IReadOnlyDictionary<string, IReadOnlyList<HistoryPoint>> Series);

/// <summary>A bucket: average, minimum and maximum of the samples in it.</summary>
public readonly record struct HistoryPoint(DateTimeOffset Timestamp, double Average, double Min, double Max);

public enum NotificationChannelKind
{
    Email,
    Webhook,
    Command,
}

/// <summary>Sends notifications for events.</summary>
public interface INotificationService
{
    /// <summary>Sends a test message through one channel (the webhook or command with <paramref name="id"/>).</summary>
    Task<CommandResult> SendTestAsync(NotificationChannelKind channel, string? id, CancellationToken cancellationToken = default);

    /// <summary>The most recent deliveries, newest first, for the web panel.</summary>
    IReadOnlyList<NotificationDelivery> RecentDeliveries { get; }
}

public sealed record NotificationDelivery(DateTimeOffset Timestamp, NotificationChannelKind Channel, string Target,
                                          UpsEventType EventType, string? Ups, bool Success, string? Error);

public enum HostProtectionState
{
    /// <summary>Host protection is off.</summary>
    Disabled,

    /// <summary>Watching the UPSes; nothing critical.</summary>
    Monitoring,

    /// <summary>A critical condition started the grace period (<c>ShutdownDelaySeconds</c>).</summary>
    Pending,

    /// <summary>FSD was set; waiting for the NUT secondaries to log out.</summary>
    WaitingForSecondaries,

    /// <summary>The shutdown command was issued.</summary>
    ShuttingDown,

    /// <summary>Dry run: everything happened except the shutdown itself.</summary>
    DryRunCompleted,
}

/// <param name="CriticalUps">The UPSes currently in a critical condition.</param>
/// <param name="ShutdownAt">When the shutdown will happen (Pending / WaitingForSecondaries).</param>
public sealed record HostProtectionStatus(bool Enabled, HostProtectionState State, string? Reason,
                                          DateTimeOffset? ShutdownAt, bool DryRun, IReadOnlyList<string> CriticalUps);

/// <summary>The automatic shutdown of the machine NutHub runs on.</summary>
public interface IHostProtectionService
{
    HostProtectionStatus GetStatus();

    /// <summary>
    /// The command run when <c>hostProtection.shutdownCommand</c> is empty, shown in the web panel as the default.
    /// </summary>
    string DefaultShutdownCommand { get; }

    /// <summary>Cancels a pending shutdown (during the grace period). False when nothing was pending.</summary>
    bool CancelPending(CommandOrigin origin);
}

/// <summary>The state of the NUT protocol listener, for the web panel.</summary>
public interface INutServerStatus
{
    bool Enabled { get; }

    /// <summary>The endpoints actually listening, e.g. "[::]:3493".</summary>
    IReadOnlyList<string> Endpoints { get; }

    /// <summary>The last problem (port in use...), or null.</summary>
    string? LastError { get; }

    bool TlsAvailable { get; }
}
