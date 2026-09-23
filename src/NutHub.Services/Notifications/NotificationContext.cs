using System.Globalization;
using System.Text.Json;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Runtime;

namespace NutHub.Services.Notifications;

/// <summary>The key readings of a UPS that go into every notification, read from the registry.</summary>
internal sealed record UpsReadings(string Name, string? Status, string? Charge, string? Runtime, string? Load,
                                   string? InputVoltage, bool Available)
{
    public static UpsReadings? From(IUpsRegistry registry, string? ups)
    {
        if (ups is null || registry.Find(ups) is not { } unit)
        {
            return null;
        }

        UpsSnapshot s = unit.Snapshot;
        return new UpsReadings(s.Name, s.Get("ups.status"), s.Get("battery.charge"), s.Get("battery.runtime"),
                               s.Get("ups.load"), s.Get("input.voltage"), s.IsAvailable);
    }

    /// <summary>The readings as NUT variables, for the default webhook document.</summary>
    public IEnumerable<KeyValuePair<string, string>> AsVariables()
    {
        if (Status is not null) yield return new("ups.status", Status);
        if (Charge is not null) yield return new("battery.charge", Charge);
        if (Runtime is not null) yield return new("battery.runtime", Runtime);
        if (Load is not null) yield return new("ups.load", Load);
        if (InputVoltage is not null) yield return new("input.voltage", InputVoltage);
    }
}

/// <summary>
/// One event about to be notified, with everything a channel needs to render it: the server identity and the
/// readings of the UPS concerned at the time of the notification.
/// </summary>
internal sealed record NotificationContext(UpsEvent Event, string ServerName, string? Location, UpsReadings? Readings,
                                           bool IsTest = false)
{
    public static NotificationContext Create(UpsEvent e, NutHubConfig config, IUpsRegistry registry, bool isTest = false) =>
        new(e, config.Server.Name, config.Server.Location, UpsReadings.From(registry, e.Ups), isTest);

    /// <summary>The event type as the web API names it ("onBattery"), or "test" for a test message.</summary>
    public string TypeName => IsTest ? "test" : JsonNamingPolicy.CamelCase.ConvertName(Event.Type.ToString());

    /// <summary>The values of the placeholders of webhook templates and command arguments.</summary>
    public IReadOnlyDictionary<string, string> PlaceholderValues()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ups"] = Event.Ups ?? "",
            ["type"] = TypeName,
            ["notifytype"] = IsTest ? "TEST" : NotifyTypes.For(Event.Type),
            ["severity"] = JsonNamingPolicy.CamelCase.ConvertName(Event.Severity.ToString()),
            ["category"] = JsonNamingPolicy.CamelCase.ConvertName(Event.Category.ToString()),
            ["message"] = Event.Message,
            ["timestamp"] = FormatTimestamp(Event.Timestamp),
            ["server"] = ServerName,
            ["location"] = Location ?? "",
            ["status"] = Readings?.Status ?? "",
            ["charge"] = Readings?.Charge ?? "",
            ["runtime"] = Readings?.Runtime ?? "",
            ["load"] = Readings?.Load ?? "",
            ["actor"] = Event.Actor ?? "",
        };
    }

    public static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
