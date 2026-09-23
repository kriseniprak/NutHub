using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Web.Admin;
using NutHub.Web.Api.Dto;

namespace NutHub.Web.Api.Endpoints;

/// <summary>Notification settings, test messages and the delivery log (admin).</summary>
internal static class NotificationEndpoints
{
    public static void Map(RouteGroupBuilder admin)
    {
        admin.MapGet("/notifications", (ConfigWriter writer) => View(writer.Current.Notifications));
        admin.MapPut("/notifications", UpdateAsync);
        admin.MapPost("/notifications/test", TestAsync);
        admin.MapGet("/notifications/deliveries", (INotificationService notifications) =>
            notifications.RecentDeliveries
                .Select(d => new NotificationDeliveryDto(d.Timestamp, d.Channel, d.Target, d.EventType, d.Ups, d.Success, d.Error))
                .ToList());
    }

    public static NotificationSettingsDto View(NotificationSettings n) =>
        new(n.Events,
            new EmailSettingsDto(n.Email.Enabled, n.Email.Host, n.Email.Port, n.Email.Security, n.Email.Username, null,
                                 !string.IsNullOrEmpty(n.Email.Password), n.Email.From, n.Email.To,
                                 n.Email.SubjectPrefix, n.Email.Events),
            n.Webhooks.Select(w => new WebhookDto(
                w.Id, w.Name, w.Enabled, w.Url, w.Method, w.ContentType, w.BodyTemplate,
                w.Headers.ToDictionary(h => h.Key, _ => (string?)null, StringComparer.OrdinalIgnoreCase), w.Events)).ToList(),
            n.Commands.Select(c => new CommandHookDto(
                c.Id, c.Name, c.Enabled, c.Command, c.Arguments, c.TimeoutSeconds, c.Events)).ToList());

    private static async Task<NotificationSettingsDto> UpdateAsync(NotificationSettingsDto body, HttpContext context,
                                                                   ConfigWriter writer)
    {
        NutHubConfig saved = await writer.UpdateAsync(context, config =>
        {
            NotificationSettings n = config.Notifications;
            if (body.Events is not null)
            {
                n.Events = body.Events.Distinct().ToList();
            }

            if (body.Email is { } email)
            {
                ApplyEmail(email, n.Email);
            }

            if (body.Webhooks is not null)
            {
                n.Webhooks = body.Webhooks
                    .Select((w, i) => BuildWebhook(Required(w, $"webhooks[{i}]"), i, n.Webhooks)).ToList();
            }

            if (body.Commands is not null)
            {
                n.Commands = body.Commands
                    .Select((c, i) => BuildCommand(Required(c, $"commands[{i}]"), n.Commands)).ToList();
            }
        }, "notification settings changed", FieldMaps.Section("notifications")).ConfigureAwait(false);

        return View(saved.Notifications);
    }

    private static void ApplyEmail(EmailSettingsDto dto, EmailSettings email)
    {
        email.Enabled = dto.Enabled ?? email.Enabled;
        email.Host = dto.Host?.Trim() ?? email.Host;
        email.Port = dto.Port ?? email.Port;
        email.Security = dto.Security ?? email.Security;
        if (dto.Username is not null)
        {
            email.Username = string.IsNullOrWhiteSpace(dto.Username) ? null : dto.Username.Trim();
        }

        // Secret: null keeps, "" clears, anything else replaces (encrypted by the configuration store).
        if (dto.Password is not null)
        {
            email.Password = dto.Password.Length == 0 ? null : dto.Password;
        }

        email.From = dto.From?.Trim() ?? email.From;
        if (dto.To is not null)
        {
            email.To = dto.To.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        }

        email.SubjectPrefix = dto.SubjectPrefix ?? email.SubjectPrefix;
        // The list of events is sent whole: null means "the events of every channel".
        email.Events = dto.Events?.Distinct().ToList();
    }

    private static T Required<T>(T? item, string field) where T : class =>
        item ?? throw ApiException.Validation(field, "A value is required.");

    private static WebhookSettings BuildWebhook(WebhookDto dto, int index, List<WebhookSettings> stored)
    {
        WebhookSettings? old = string.IsNullOrEmpty(dto.Id) ? null : stored.FirstOrDefault(w => w.Id == dto.Id);
        var hook = new WebhookSettings
        {
            Name = dto.Name?.Trim() ?? old?.Name ?? "",
            Enabled = dto.Enabled ?? old?.Enabled ?? true,
            Url = dto.Url?.Trim() ?? old?.Url ?? "",
            Method = (dto.Method ?? old?.Method ?? "POST").Trim().ToUpperInvariant(),
            ContentType = dto.ContentType ?? old?.ContentType ?? "application/json",
            BodyTemplate = string.IsNullOrEmpty(dto.BodyTemplate) ? null : dto.BodyTemplate,
            Events = dto.Events?.Distinct().ToList(),
        };
        if (!string.IsNullOrEmpty(dto.Id))
        {
            hook.Id = dto.Id;
        }

        // Header values are secrets: null keeps the stored value, a string replaces it, a missing header is removed.
        foreach (var (name, value) in dto.Headers ?? new Dictionary<string, string?>())
        {
            string key = name.Trim();
            if (key.Length == 0)
            {
                continue;
            }

            if (value is not null)
            {
                hook.Headers[key] = value;
            }
            else if (old is not null && old.Headers.TryGetValue(key, out string? kept))
            {
                hook.Headers[key] = kept;
            }
            else
            {
                throw ApiException.Validation($"webhooks[{index}].headers.{key}", "Enter a value for this new header.");
            }
        }

        return hook;
    }

    private static CommandHookSettings BuildCommand(CommandHookDto dto, List<CommandHookSettings> stored)
    {
        CommandHookSettings? old = string.IsNullOrEmpty(dto.Id) ? null : stored.FirstOrDefault(c => c.Id == dto.Id);
        var hook = new CommandHookSettings
        {
            Name = dto.Name?.Trim() ?? old?.Name ?? "",
            Enabled = dto.Enabled ?? old?.Enabled ?? true,
            Command = dto.Command?.Trim() ?? old?.Command ?? "",
            Arguments = string.IsNullOrWhiteSpace(dto.Arguments) ? null : dto.Arguments,
            TimeoutSeconds = dto.TimeoutSeconds ?? old?.TimeoutSeconds ?? 30,
            Events = dto.Events?.Distinct().ToList(),
        };
        if (!string.IsNullOrEmpty(dto.Id))
        {
            hook.Id = dto.Id;
        }

        return hook;
    }

    private static async Task<CommandResultDto> TestAsync(NotificationTestRequest body, INotificationService notifications,
                                                          HttpContext context)
    {
        NotificationChannelKind channel = body.Channel
                                          ?? throw ApiException.Validation("channel", "Choose email, webhook or command.");
        var result = await notifications.SendTestAsync(channel, string.IsNullOrWhiteSpace(body.Id) ? null : body.Id,
                                                       context.RequestAborted).ConfigureAwait(false);
        return CommandResultDto.From(result);
    }
}
