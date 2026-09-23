using System.Text.Json.Serialization;
using NutHub.Core.Configuration;
using NutHub.Core.Model;

namespace NutHub.Web.Api.Dto;

// Notification settings as the API shows and accepts them. Secrets: the SMTP password is never returned
// ("passwordSet" tells whether one is stored) and webhook header values are returned as null. Members are nullable
// so that an absent member keeps the stored value.

internal sealed record NotificationSettingsDto(
    IReadOnlyList<UpsEventType>? Events,
    EmailSettingsDto? Email,
    IReadOnlyList<WebhookDto>? Webhooks,
    IReadOnlyList<CommandHookDto>? Commands);

internal sealed record EmailSettingsDto(
    bool? Enabled,
    string? Host,
    int? Port,
    SmtpSecurity? Security,
    string? Username,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Password,
    bool? PasswordSet,
    string? From,
    IReadOnlyList<string>? To,
    string? SubjectPrefix,
    IReadOnlyList<UpsEventType>? Events);

internal sealed record WebhookDto(
    string? Id,
    string? Name,
    bool? Enabled,
    string? Url,
    string? Method,
    string? ContentType,
    string? BodyTemplate,
    IReadOnlyDictionary<string, string?>? Headers,
    IReadOnlyList<UpsEventType>? Events);

internal sealed record CommandHookDto(
    string? Id,
    string? Name,
    bool? Enabled,
    string? Command,
    string? Arguments,
    int? TimeoutSeconds,
    IReadOnlyList<UpsEventType>? Events);
