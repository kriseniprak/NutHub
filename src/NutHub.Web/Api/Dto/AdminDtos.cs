using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Model;

namespace NutHub.Web.Api.Dto;

// Shapes of the authentication and administration endpoints (docs/API.md). Input records use nullable members so
// that an absent value can be told apart from a given one ("absent keeps" semantics of the updates).

internal sealed record UserInfoDto(string Name, string? DisplayName, WebRole Role, bool MustChangePassword);

internal sealed record AuthStateDto(bool Authenticated, UserInfoDto? User, bool AnonymousRead, string ServerName,
                                    string Version);

internal sealed record LoginRequest(string? Username, string? Password);

internal sealed record LoginResponse(UserInfoDto User);

internal sealed record PasswordChangeRequest(string? CurrentPassword, string? NewPassword);

internal sealed record DriverOptionChoiceDto(string Value, string Label);

internal sealed record DriverOptionConditionDto(string Key, IReadOnlyList<string> Values);

internal sealed record DriverOptionDto(
    string Key,
    string Label,
    DriverOptionType Type,
    bool Required,
    string? Default,
    string? Help,
    IReadOnlyList<DriverOptionChoiceDto>? Choices,
    double? Min,
    double? Max,
    bool Advanced,
    DriverOptionConditionDto? VisibleWhen);

internal sealed record DriverDto(
    string Id,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Platforms,
    bool Supported,
    bool SupportsDiscovery,
    IReadOnlyList<DriverOptionDto> Options);

internal sealed record DiscoveredDeviceDto(string Title, string? Detail, IReadOnlyDictionary<string, string> Options,
                                           string? SuggestedName);

/// <summary><c>message</c> explains an empty result (timeout, discovery failure); null otherwise.</summary>
internal sealed record DiscoveryResultDto(IReadOnlyList<DiscoveredDeviceDto> Devices, string? Message);

internal sealed record LowBatteryDto(double? ChargePercent, int? RuntimeSeconds, bool IgnoreDeviceFlag);

internal sealed record UpsConfigDto(
    string? Name,
    string? Description,
    string? Driver,
    bool? Enabled,
    double? PollIntervalSeconds,
    [property: JsonConverter(typeof(LenientStringMapConverter))] Dictionary<string, string?>? Options,
    IReadOnlyList<string>? SecretsSet,
    [property: JsonConverter(typeof(LenientStringMapConverter))] Dictionary<string, string?>? Overrides,
    LowBatteryDto? LowBattery);

internal sealed record UpsOrderRequest(IReadOnlyList<string>? Names);

internal sealed record NutUserDto(
    string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Password,
    NutMonitorRole? Monitor,
    IReadOnlyList<string>? Actions,
    IReadOnlyList<string>? InstantCommands,
    IReadOnlyList<string>? AllowedUps);

internal sealed record WebUserDto(
    string? Name,
    string? DisplayName,
    WebRole? Role,
    bool? Disabled,
    bool? MustChangePassword,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Password);

internal sealed record NutImportRequest(string? UpsConf, string? UpsdUsers, bool Apply);

internal sealed record NutImportResultDto(IReadOnlyList<UpsConfigDto> Ups, IReadOnlyList<NutUserDto> NutUsers,
                                          IReadOnlyList<string> Warnings, bool Applied);

internal sealed record NotificationTestRequest(NotificationChannelKind? Channel, string? Id);

internal sealed record NotificationDeliveryDto(DateTimeOffset Timestamp, NotificationChannelKind Channel, string Target,
                                               UpsEventType EventType, string? Ups, bool Success, string? Error);

internal sealed record LogEntryDto(long Id, DateTimeOffset Timestamp, LogLevel Level, string Category, string Message,
                                   string? Exception);

internal sealed record DriverSupportDto(string Id, bool Supported);

internal sealed record SystemInfoDto(
    string Version,
    string Os,
    string Architecture,
    string Framework,
    int ProcessId,
    string MachineName,
    bool IsService,
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    long WorkingSetBytes,
    string DataDirectory,
    string ConfigFile,
    string DatabaseFile,
    IReadOnlyList<DriverSupportDto> Drivers);

internal sealed record HostProtectionAdminDto(HostProtectionSettings Settings, HostProtectionDto Status,
                                              string DefaultShutdownCommand);
