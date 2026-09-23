namespace NutHub.Core.Model;

/// <summary>Everything NutHub records in its event log and can notify about.</summary>
public enum UpsEventType
{
    // Power
    OnBattery,
    Online,
    LowBattery,
    LowBatteryCleared,
    ForcedShutdown,
    ForcedShutdownCleared,

    // Device condition
    ReplaceBattery,
    ReplaceBatteryCleared,
    Overload,
    OverloadCleared,
    Bypass,
    BypassCleared,
    Off,
    OffCleared,
    Calibration,
    CalibrationEnded,
    Trim,
    TrimEnded,
    Boost,
    BoostEnded,
    Alarm,
    AlarmCleared,
    TestStarted,
    TestEnded,

    // Communication with the device
    CommunicationLost,
    CommunicationRestored,
    NoCommunication,
    DriverStarted,
    DriverStopped,
    DriverFailed,

    // Actions on the UPS
    CommandExecuted,
    CommandFailed,
    VariableChanged,

    // Protection of the NutHub host itself
    ShutdownPending,
    ShutdownCancelled,
    ShutdownStarted,

    // Server and audit
    ServerStarted,
    ServerStopping,
    ConfigurationChanged,
    UserLogin,
    UserLoginFailed,
    NutClientLogin,
    NutClientLogout,

    /// <summary>A test message sent from the notification settings.</summary>
    NotificationTest,
}

public enum EventSeverity
{
    Info,
    Notice,
    Warning,
    Critical,
}

public enum EventCategory
{
    Power,
    Device,
    Communication,
    Command,
    Shutdown,
    Audit,
    System,
}

/// <summary>
/// One entry of the event log. <see cref="Id"/> is 0 until the event store has recorded it.
/// </summary>
public sealed record UpsEvent
{
    public long Id { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The UPS concerned, or null for server-wide events.</summary>
    public string? Ups { get; init; }

    public required UpsEventType Type { get; init; }

    public EventSeverity Severity { get; init; }

    public EventCategory Category { get; init; }

    /// <summary>An English, human-readable message (the web panel may localise from <see cref="Type"/>).</summary>
    public required string Message { get; init; }

    /// <summary>Who caused it, for commands and audit events: "web:admin@192.168.1.10", "nut:upsmon@10.0.0.5"...</summary>
    public string? Actor { get; init; }

    /// <summary>Optional structured details (variable values at the time, command name...).</summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }

    /// <summary>Creates an event with the default severity and category of its type.</summary>
    public static UpsEvent Create(UpsEventType type, DateTimeOffset timestamp, string? ups, string message,
                                  string? actor = null, IReadOnlyDictionary<string, string>? data = null,
                                  EventSeverity? severity = null) =>
        new()
        {
            Type = type,
            Timestamp = timestamp,
            Ups = ups,
            Message = message,
            Actor = actor,
            Data = data,
            Severity = severity ?? UpsEventCatalog.DefaultSeverity(type),
            Category = UpsEventCatalog.CategoryOf(type),
        };
}

/// <summary>Default severity and category of each event type.</summary>
public static class UpsEventCatalog
{
    public static EventCategory CategoryOf(UpsEventType type) => type switch
    {
        UpsEventType.OnBattery or UpsEventType.Online or UpsEventType.LowBattery or UpsEventType.LowBatteryCleared
            or UpsEventType.ForcedShutdown or UpsEventType.ForcedShutdownCleared => EventCategory.Power,
        UpsEventType.CommunicationLost or UpsEventType.CommunicationRestored or UpsEventType.NoCommunication
            or UpsEventType.DriverStarted or UpsEventType.DriverStopped or UpsEventType.DriverFailed
            => EventCategory.Communication,
        UpsEventType.CommandExecuted or UpsEventType.CommandFailed or UpsEventType.VariableChanged
            => EventCategory.Command,
        UpsEventType.ShutdownPending or UpsEventType.ShutdownCancelled or UpsEventType.ShutdownStarted
            => EventCategory.Shutdown,
        UpsEventType.ConfigurationChanged or UpsEventType.UserLogin or UpsEventType.UserLoginFailed
            or UpsEventType.NutClientLogin or UpsEventType.NutClientLogout => EventCategory.Audit,
        UpsEventType.ServerStarted or UpsEventType.ServerStopping or UpsEventType.NotificationTest
            => EventCategory.System,
        _ => EventCategory.Device,
    };

    public static EventSeverity DefaultSeverity(UpsEventType type) => type switch
    {
        UpsEventType.LowBattery or UpsEventType.ForcedShutdown or UpsEventType.ShutdownStarted
            or UpsEventType.ShutdownPending => EventSeverity.Critical,
        UpsEventType.OnBattery or UpsEventType.ReplaceBattery or UpsEventType.Overload or UpsEventType.Bypass
            or UpsEventType.Off or UpsEventType.Alarm or UpsEventType.CommunicationLost
            or UpsEventType.NoCommunication or UpsEventType.DriverFailed or UpsEventType.CommandFailed
            or UpsEventType.UserLoginFailed => EventSeverity.Warning,
        UpsEventType.Online or UpsEventType.LowBatteryCleared or UpsEventType.ForcedShutdownCleared
            or UpsEventType.CommunicationRestored or UpsEventType.ShutdownCancelled or UpsEventType.ServerStarted
            or UpsEventType.ServerStopping or UpsEventType.CommandExecuted or UpsEventType.VariableChanged
            or UpsEventType.ConfigurationChanged => EventSeverity.Notice,
        _ => EventSeverity.Info,
    };

    /// <summary>The events that are notified by default on a fresh installation.</summary>
    public static IReadOnlyList<UpsEventType> DefaultNotified { get; } =
    [
        UpsEventType.OnBattery,
        UpsEventType.Online,
        UpsEventType.LowBattery,
        UpsEventType.ForcedShutdown,
        UpsEventType.ReplaceBattery,
        UpsEventType.Overload,
        UpsEventType.CommunicationLost,
        UpsEventType.CommunicationRestored,
        UpsEventType.NoCommunication,
        UpsEventType.ShutdownPending,
        UpsEventType.ShutdownStarted,
        UpsEventType.ShutdownCancelled,
    ];
}
