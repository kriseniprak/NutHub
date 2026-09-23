using System.Net;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Core.Security;

namespace NutHub.Core.Configuration;

/// <summary>Thrown by <see cref="IConfigStore.UpdateAsync"/> for a configuration that fails validation.</summary>
public sealed class ConfigValidationException(IReadOnlyDictionary<string, string> errors)
    : Exception("The configuration is not valid: " + string.Join("; ", errors.Select(e => $"{e.Key}: {e.Value}")))
{
    /// <summary>Field path ("ups[0].name", "web.httpPort") to message.</summary>
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}

/// <summary>Checks a whole configuration; every rule reports the field it concerns.</summary>
public static class ConfigValidator
{
    private static readonly string[] NutActions = ["SET", "FSD"];

    public static IReadOnlyDictionary<string, string> Validate(NutHubConfig config, IDriverCatalog? drivers = null)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        void Error(string key, string message) => errors.TryAdd(key, message);

        if (config.SchemaVersion > NutHubConfig.CurrentSchemaVersion)
        {
            Error("schemaVersion", "The configuration was written by a newer version of NutHub.");
        }

        if (string.IsNullOrWhiteSpace(config.Server.Name) || config.Server.Name.Length > 100)
        {
            Error("server.name", "The server name must be 1 to 100 characters.");
        }

        // Text that ends up in NUT protocol lines, mail headers or log lines must stay on one line.
        SingleLine(config.Server.Name, "server.name", Error);
        SingleLine(config.Server.Location, "server.location", Error);

        // NUT server
        var nut = config.Nut;
        if (nut.Listen.Count == 0 && nut.Enabled)
        {
            Error("nut.listen", "At least one listen address is needed.");
        }

        for (int i = 0; i < nut.Listen.Count; i++)
        {
            ValidateBind(nut.Listen[i].Address, $"nut.listen[{i}].address", Error);
            ValidatePort(nut.Listen[i].Port, $"nut.listen[{i}].port", Error);
        }

        if (nut.MaxConnections is < 1 or > 10_000)
        {
            Error("nut.maxConnections", "Must be between 1 and 10000.");
        }

        if (nut.MaxAgeSeconds is < 1 or > 3600)
        {
            Error("nut.maxAgeSeconds", "Must be between 1 and 3600 seconds.");
        }

        if (nut.FsdClearDelaySeconds is < 0 or > 86_400)
        {
            Error("nut.fsdClearDelaySeconds", "Must be between 0 and 86400 seconds.");
        }

        ValidateNetworks(nut.AllowedNetworks, "nut.allowedNetworks", Error);

        // Web
        var web = config.Web;
        ValidateBind(web.BindAddress, "web.bindAddress", Error);
        ValidatePort(web.HttpPort, "web.httpPort", Error);
        ValidatePort(web.HttpsPort, "web.httpsPort", Error);
        if (web.Enabled && !web.HttpEnabled && !web.HttpsEnabled)
        {
            Error("web.httpEnabled", "Enable HTTP, HTTPS or both.");
        }

        if (web.HttpEnabled && web.HttpsEnabled && web.HttpPort == web.HttpsPort)
        {
            Error("web.httpsPort", "HTTP and HTTPS need different ports.");
        }

        if (web.SessionHours is < 1 or > 24 * 90)
        {
            Error("web.sessionHours", "Must be between 1 and 2160 hours.");
        }

        ValidateNetworks(web.AllowedNetworks, "web.allowedNetworks", Error);

        if (nut.Enabled && web.Enabled)
        {
            foreach (var listen in nut.Listen)
            {
                if ((web.HttpEnabled && listen.Port == web.HttpPort) || (web.HttpsEnabled && listen.Port == web.HttpsPort))
                {
                    Error("web.httpPort", $"Port {listen.Port} is already used by the NUT server.");
                }
            }
        }

        // UPSes
        var upsNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < config.Ups.Count; i++)
        {
            var ups = config.Ups[i];
            string p = $"ups[{i}]";
            if (!NutFormat.IsValidUpsName(ups.Name))
            {
                Error(p + ".name", "Use 1 to 64 letters, digits, '.', '_' or '-', starting with a letter or digit.");
            }
            else if (!upsNames.Add(ups.Name))
            {
                Error(p + ".name", $"Another UPS is already named '{ups.Name}'.");
            }

            if (ups.Description is { Length: > 200 })
            {
                Error(p + ".description", "At most 200 characters.");
            }

            SingleLine(ups.Description, p + ".description", Error);
            foreach (var (key, value) in ups.Options)
            {
                SingleLine(value, $"{p}.options.{key}", Error);
            }

            if (string.IsNullOrWhiteSpace(ups.Driver))
            {
                Error(p + ".driver", "Choose a driver.");
            }
            else if (drivers is not null && drivers.Find(ups.Driver) is null)
            {
                Error(p + ".driver", $"Unknown driver '{ups.Driver}'.");
            }

            if (ups.PollIntervalSeconds is < 0.5 or > 300)
            {
                Error(p + ".pollIntervalSeconds", "Must be between 0.5 and 300 seconds.");
            }

            foreach (var key in ups.Overrides.Keys)
            {
                if (!NutFormat.IsValidVariableName(key))
                {
                    Error(p + ".overrides", $"'{key}' is not a valid variable name.");
                }

                SingleLine(ups.Overrides[key], p + ".overrides", Error);
            }

            if (ups.LowBattery.ChargePercent is < 0 or > 100)
            {
                Error(p + ".lowBattery.chargePercent", "Must be between 0 and 100.");
            }

            if (ups.LowBattery.RuntimeSeconds is < 0 or > 86_400)
            {
                Error(p + ".lowBattery.runtimeSeconds", "Must be between 0 and 86400.");
            }
        }

        // NUT accounts
        var nutUsers = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < config.NutUsers.Count; i++)
        {
            var user = config.NutUsers[i];
            string p = $"nutUsers[{i}]";
            if (!IsValidAccountName(user.Name))
            {
                Error(p + ".name", "Use 1 to 64 letters, digits, '.', '_', '-' or '@'.");
            }
            else if (!nutUsers.Add(user.Name))
            {
                Error(p + ".name", $"Another NUT account is already named '{user.Name}'.");
            }

            if (string.IsNullOrEmpty(user.PasswordHash))
            {
                Error(p + ".password", "A password is required.");
            }

            if (!Enum.IsDefined(user.Monitor))
            {
                Error(p + ".monitor", "Use none, secondary or primary.");
            }

            foreach (string action in user.Actions)
            {
                if (!NutActions.Contains(action, StringComparer.OrdinalIgnoreCase))
                {
                    Error(p + ".actions", $"Unknown action '{action}' (use SET or FSD).");
                }
            }

            foreach (string cmd in user.InstantCommands)
            {
                if (!string.Equals(cmd, "ALL", StringComparison.OrdinalIgnoreCase) && !NutFormat.IsValidVariableName(cmd))
                {
                    Error(p + ".instantCommands", $"'{cmd}' is not a valid command name.");
                }
            }

            foreach (string ups in user.AllowedUps)
            {
                if (!upsNames.Contains(ups))
                {
                    Error(p + ".allowedUps", $"There is no UPS named '{ups}'.");
                }
            }
        }

        // Web accounts
        var webUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < config.WebUsers.Count; i++)
        {
            var user = config.WebUsers[i];
            string p = $"webUsers[{i}]";
            if (!IsValidAccountName(user.Name))
            {
                Error(p + ".name", "Use 1 to 64 letters, digits, '.', '_', '-' or '@'.");
            }
            else if (!webUsers.Add(user.Name))
            {
                Error(p + ".name", $"Another web account is already named '{user.Name}'.");
            }

            if (string.IsNullOrEmpty(user.PasswordHash))
            {
                Error(p + ".password", "A password is required.");
            }

            if (!Enum.IsDefined(user.Role))
            {
                Error(p + ".role", "Use viewer, operator or admin.");
            }
        }

        if (!config.WebUsers.Any(u => u.Role == WebRole.Admin && !u.Disabled))
        {
            Error("webUsers", "At least one enabled administrator account is required.");
        }

        // Notifications
        var email = config.Notifications.Email;
        if (email.Enabled)
        {
            if (string.IsNullOrWhiteSpace(email.Host))
            {
                Error("notifications.email.host", "The SMTP server is required.");
            }

            ValidatePort(email.Port, "notifications.email.port", Error);
            if (!IsEmail(email.From))
            {
                Error("notifications.email.from", "A valid sender address is required.");
            }

            if (email.To.Count == 0 || email.To.Any(t => !IsEmail(t)))
            {
                Error("notifications.email.to", "At least one valid recipient is required.");
            }
        }

        var hookIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < config.Notifications.Webhooks.Count; i++)
        {
            var hook = config.Notifications.Webhooks[i];
            string p = $"notifications.webhooks[{i}]";
            if (string.IsNullOrWhiteSpace(hook.Id) || !hookIds.Add(hook.Id))
            {
                Error(p + ".id", "Each webhook needs a unique id.");
            }

            if (!Uri.TryCreate(hook.Url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                Error(p + ".url", "An absolute http:// or https:// URL is required.");
            }

            if (hook.Method is not ("POST" or "PUT" or "GET"))
            {
                Error(p + ".method", "Use POST, PUT or GET.");
            }

            foreach (string header in hook.Headers.Keys)
            {
                if (!IsHttpToken(header))
                {
                    Error(p + ".headers", $"'{header}' is not a valid HTTP header name.");
                }
            }
        }

        for (int i = 0; i < config.Notifications.Commands.Count; i++)
        {
            var hook = config.Notifications.Commands[i];
            string p = $"notifications.commands[{i}]";
            if (string.IsNullOrWhiteSpace(hook.Id) || !hookIds.Add(hook.Id))
            {
                Error(p + ".id", "Each command needs a unique id.");
            }

            if (string.IsNullOrWhiteSpace(hook.Command))
            {
                Error(p + ".command", "The program to run is required.");
            }

            if (hook.TimeoutSeconds is < 1 or > 3600)
            {
                Error(p + ".timeoutSeconds", "Must be between 1 and 3600 seconds.");
            }
        }

        // Host protection
        var hp = config.HostProtection;
        foreach (string ups in hp.Ups)
        {
            if (!upsNames.Contains(ups))
            {
                Error("hostProtection.ups", $"There is no UPS named '{ups}'.");
            }
        }

        if (hp.Enabled)
        {
            if (hp.Ups.Count == 0)
            {
                Error("hostProtection.ups", "Choose the UPS that powers this machine.");
            }
            else if (hp.MinimumSupplies < 1 || hp.MinimumSupplies > hp.Ups.Count)
            {
                Error("hostProtection.minimumSupplies", $"Must be between 1 and {hp.Ups.Count}.");
            }
        }

        if (hp.BatteryChargeBelow is < 0 or > 100)
        {
            Error("hostProtection.batteryChargeBelow", "Must be between 0 and 100.");
        }

        if (hp.SecondariesTimeoutSeconds is < 0 or > 3600 || hp.ShutdownDelaySeconds is < 0 or > 3600 ||
            hp.PowerOffDelaySeconds is < 0 or > 3600 || hp.CommunicationLostOnBatterySeconds is < 0 or > 3600)
        {
            Error("hostProtection", "Delays must be between 0 and 3600 seconds.");
        }

        if (hp.Enabled && hp.PowerOffUps && hp.PowerOffDelaySeconds < 30)
        {
            // The power-off command is sent before the operating system shuts down: leave it the time to finish.
            Error("hostProtection.powerOffDelaySeconds", "Leave the machine at least 30 seconds to shut down.");
        }

        if (!NutFormat.IsValidVariableName(hp.PowerOffCommand))
        {
            Error("hostProtection.powerOffCommand", "Not a valid command name.");
        }

        // History
        var history = config.History;
        if (history.SampleIntervalSeconds is < 5 or > 3600)
        {
            Error("history.sampleIntervalSeconds", "Must be between 5 and 3600 seconds.");
        }

        if (history.RetentionDays is < 1 or > 3650)
        {
            Error("history.retentionDays", "Must be between 1 and 3650 days.");
        }

        if (history.EventRetentionDays is < 1 or > 3650)
        {
            Error("history.eventRetentionDays", "Must be between 1 and 3650 days.");
        }

        foreach (string variable in history.Variables)
        {
            if (!NutFormat.IsValidVariableName(variable))
            {
                Error("history.variables", $"'{variable}' is not a valid variable name.");
            }
        }

        return errors;
    }

    public static void ThrowIfInvalid(NutHubConfig config, IDriverCatalog? drivers = null)
    {
        var errors = Validate(config, drivers);
        if (errors.Count > 0)
        {
            throw new ConfigValidationException(errors);
        }
    }

    /// <summary>1 to 64 of letters, digits, '.', '_', '-', '@'; not only dots, which a URL path cannot address.</summary>
    public static bool IsValidAccountName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 64 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@') &&
        !name.All(c => c == '.');

    private static bool IsEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 254 && value.IndexOf('@') > 0 &&
        value.IndexOf('@') < value.Length - 1 && !value.Any(char.IsWhiteSpace);

    /// <summary>An HTTP header name (RFC 9110 token).</summary>
    private static bool IsHttpToken(string name) =>
        name.Length is > 0 and <= 256 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));

    private static void SingleLine(string? value, string key, Action<string, string> error)
    {
        if (value is not null && value.Any(char.IsControl))
        {
            error(key, "Line breaks and control characters are not allowed.");
        }
    }

    private static void ValidatePort(int port, string key, Action<string, string> error)
    {
        if (port is < 1 or > 65535)
        {
            error(key, "Must be a port between 1 and 65535.");
        }
    }

    private static void ValidateBind(string address, string key, Action<string, string> error)
    {
        if (address != "*" && !IPAddress.TryParse(address, out _))
        {
            error(key, "Use * for every interface, or an IP address of this machine.");
        }
    }

    private static void ValidateNetworks(IEnumerable<string> networks, string key, Action<string, string> error)
    {
        foreach (string entry in networks)
        {
            if (!AddressFilter.TryParseNetwork(entry, out _))
            {
                error(key, $"'{entry}' is not a valid address or network (e.g. 192.168.1.0/24).");
                return;
            }
        }
    }
}
