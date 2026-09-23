using NutHub.Core.Abstractions;
using NutHub.Core.Model;

namespace NutHub.Web.Api.Dto;

// The shapes of the read endpoints and of the event stream (docs/API.md, "Shared shapes" and "Reading").

internal enum UpsSeverity
{
    Ok,
    Info,
    Warning,
    Critical,
    Offline,
}

internal sealed record BatteryDto(double? Charge, double? Runtime, double? Voltage, double? ChargeLow, double? RuntimeLow);

internal sealed record UpsSummaryDto(
    string Name,
    string? Description,
    string Driver,
    string DriverName,
    bool Enabled,
    DriverState DriverState,
    string? DriverMessage,
    DataAvailability Availability,
    string Status,
    IReadOnlyList<string> Flags,
    bool ForcedShutdown,
    UpsSeverity Severity,
    string? Mfr,
    string? Model,
    string? Serial,
    BatteryDto Battery,
    double? Load,
    double? InputVoltage,
    double? OutputVoltage,
    double? InputFrequency,
    double? Temperature,
    double? Power,
    double? PowerNominal,
    double? RealPower,
    double? RealPowerNominal,
    DateTimeOffset? LastUpdate,
    int Clients,
    long Sequence);

internal sealed record ValueRangeDto(string Min, string Max);

internal sealed record VariableDto(
    string Name,
    string Value,
    string? Description,
    bool Writable,
    VariableType Type,
    int MaxLength,
    IReadOnlyList<string> EnumValues,
    IReadOnlyList<ValueRangeDto> Ranges,
    bool Overridden);

internal sealed record CommandDto(string Name, string? Description, bool Dangerous);

internal sealed record NutClientDto(
    long Id,
    string Address,
    int Port,
    string? Username,
    bool Tls,
    string? LoginUps,
    bool Primary,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastActivity,
    long Commands);

internal sealed record UpsDetailDto(
    UpsSummaryDto Summary,
    IReadOnlyList<VariableDto> Variables,
    IReadOnlyList<CommandDto> Commands,
    IReadOnlyList<NutClientDto> Clients,
    double PollIntervalSeconds);

internal sealed record EventDto(
    long Id,
    DateTimeOffset Timestamp,
    string? Ups,
    UpsEventType Type,
    EventSeverity Severity,
    EventCategory Category,
    string Message,
    string? Actor,
    IReadOnlyDictionary<string, string> Data);

internal sealed record EventPageDto(IReadOnlyList<EventDto> Items, bool HasMore);

internal sealed record HostProtectionDto(
    bool Enabled,
    HostProtectionState State,
    string? Reason,
    DateTimeOffset? ShutdownAt,
    bool DryRun,
    IReadOnlyList<string> CriticalUps,
    IReadOnlyList<string> Ups);

internal sealed record NutStatusDto(bool Enabled, IReadOnlyList<string> Endpoints, string? LastError, bool Tls, int Clients);

internal sealed record ServerInfoDto(
    string Name,
    string? Location,
    string Version,
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    DateTimeOffset Time,
    NutStatusDto Nut,
    HostProtectionDto HostProtection);

internal sealed record OverviewDto(ServerInfoDto Server, IReadOnlyList<UpsSummaryDto> Ups);

/// <summary>Each point is [unix milliseconds, average, minimum, maximum].</summary>
internal sealed record HistoryDto(
    DateTimeOffset From,
    DateTimeOffset To,
    int StepSeconds,
    IReadOnlyDictionary<string, IReadOnlyList<double[]>> Series);

internal sealed record ClientsSummaryDto(int Total, IReadOnlyDictionary<string, int> ByUps);

internal sealed record UpsRemovedDto(string Name);

internal sealed record PingDto(DateTimeOffset Time);

internal sealed record CommandResultDto(bool Ok, CommandStatus Status, string? Message)
{
    public static CommandResultDto From(CommandResult result) => new(result.IsSuccess, result.Status, result.Message);
}

internal sealed record CommandRequest(string? Command, string? Parameter);

internal sealed record VariableWriteRequest(string? Value);

internal sealed record OkDto(bool Ok);
