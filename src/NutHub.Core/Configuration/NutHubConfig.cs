using System.Text.Json.Serialization;
using NutHub.Core.Model;

namespace NutHub.Core.Configuration;

// The whole configuration of NutHub, stored as camelCase JSON in nuthub.json. Instances handed out by IConfigStore
// are shared snapshots: never modify them, go through IConfigStore.UpdateAsync, which works on a copy.
//
// Secrets (passwords, SNMP communities, webhook headers...) are stored encrypted with ISecretProtector: their values
// start with "enc:". Code that needs the clear text calls ISecretProtector.Unprotect.

public sealed class NutHubConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public ServerSettings Server { get; set; } = new();

    public NutServerSettings Nut { get; set; } = new();

    public WebSettings Web { get; set; } = new();

    /// <summary>The UPSes, in display order.</summary>
    public List<UpsConfig> Ups { get; set; } = [];

    /// <summary>Accounts for NUT clients (upsmon, upsc with login, NutDesk...), like upsd.users.</summary>
    public List<NutUserConfig> NutUsers { get; set; } = [];

    /// <summary>Accounts for the web panel.</summary>
    public List<WebUserConfig> WebUsers { get; set; } = [];

    public NotificationSettings Notifications { get; set; } = new();

    public HostProtectionSettings HostProtection { get; set; } = new();

    public HistorySettings History { get; set; } = new();
}

public sealed class ServerSettings
{
    /// <summary>The name shown in the web panel title and in notifications.</summary>
    public string Name { get; set; } = "NutHub";

    /// <summary>Optional free text: "Server room, rack 2".</summary>
    public string? Location { get; set; }
}

/// <summary>The NUT protocol server (what upsd does).</summary>
public sealed class NutServerSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Addresses to listen on. Address "*" means every interface, IPv6 and IPv4 (dual-mode socket, falling back to
    /// IPv4 only when IPv6 is unavailable); "0.0.0.0" every IPv4 interface; "::" every IPv6 interface; otherwise a
    /// literal IP address of this machine.
    /// </summary>
    public List<ListenEndpoint> Listen { get; set; } = [new ListenEndpoint()];

    /// <summary>Connections beyond this are refused.</summary>
    public int MaxConnections { get; set; } = 256;

    /// <summary>
    /// Client networks allowed to connect, in CIDR notation ("192.168.1.0/24", "10.0.0.5", "::1"). Empty allows
    /// every address.
    /// </summary>
    public List<string> AllowedNetworks { get; set; } = [];

    /// <summary>
    /// Data older than this is stale (NUT MAXAGE). A UPS polled less often is given two poll intervals instead.
    /// </summary>
    public int MaxAgeSeconds { get; set; } = 15;

    /// <summary>
    /// A forced shutdown (FSD) is cleared automatically once the UPS has been back on line power, without low
    /// battery, for this many seconds. 0 keeps it until an operator clears it or NutHub restarts (upsd behaviour).
    /// </summary>
    public int FsdClearDelaySeconds { get; set; } = 60;

    public NutTlsSettings Tls { get; set; } = new();

    /// <summary>The answer to VER; null uses the default, which names NutHub and its version.</summary>
    public string? VersionString { get; set; }
}

public sealed class ListenEndpoint
{
    public string Address { get; set; } = "*";

    public int Port { get; set; } = NutHubInfo.DefaultNutPort;

    public override string ToString() => Address.Contains(':') && Address != "*" ? $"[{Address}]:{Port}" : $"{Address}:{Port}";
}

/// <summary>STARTTLS support for NUT clients.</summary>
public sealed class NutTlsSettings
{
    /// <summary>Accept STARTTLS. Without a certificate path, a self-signed certificate is generated.</summary>
    public bool Enabled { get; set; }

    /// <summary>A PKCS#12 (.pfx/.p12) or PEM certificate with its private key.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>Password of the certificate file (secret).</summary>
    public string? CertificatePassword { get; set; }

    /// <summary>Refuse USERNAME / PASSWORD before STARTTLS, so credentials never travel in clear text.</summary>
    public bool RequireTlsForAuthentication { get; set; }
}

/// <summary>The web panel.</summary>
public sealed class WebSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>"*" for every interface, or a literal IP address.</summary>
    public string BindAddress { get; set; } = "*";

    public bool HttpEnabled { get; set; } = true;

    public int HttpPort { get; set; } = NutHubInfo.DefaultHttpPort;

    public bool HttpsEnabled { get; set; }

    public int HttpsPort { get; set; } = NutHubInfo.DefaultHttpsPort;

    /// <summary>PKCS#12 or PEM certificate for HTTPS; null generates a self-signed one.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>Password of the certificate file (secret).</summary>
    public string? CertificatePassword { get; set; }

    /// <summary>Send HTTP visitors to HTTPS (only when both are enabled).</summary>
    public bool RedirectHttpToHttps { get; set; }

    /// <summary>Let visitors see the dashboard and events without signing in (read-only).</summary>
    public bool AllowAnonymousRead { get; set; }

    /// <summary>Signed-in sessions expire after this many hours without activity.</summary>
    public int SessionHours { get; set; } = 12;

    /// <summary>Client networks allowed to open the panel (CIDR); empty allows every address.</summary>
    public List<string> AllowedNetworks { get; set; } = [];
}

/// <summary>One UPS served by NutHub (a section of ups.conf).</summary>
public sealed class UpsConfig
{
    /// <summary>The NUT name ("rack1" in "rack1@server"): letters, digits, '.', '_', '-'.</summary>
    public string Name { get; set; } = "";

    /// <summary>Human description (NUT "desc").</summary>
    public string? Description { get; set; }

    /// <summary>The driver factory id: "usbhid", "megatec", "apcsmart", "snmp", "nut", "apcupsd", "simulated"...</summary>
    public string Driver { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>How often the driver reads the device.</summary>
    public double PollIntervalSeconds { get; set; } = 2;

    /// <summary>Driver options (see IUpsDriverFactory.Options); secret ones are encrypted.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Values that replace what the device reports (NUT "override.*"), e.g. "battery.charge.low": "30" or
    /// "ups.mfr": "APC". Overridden variables become read-only.
    /// </summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.Ordinal);

    public LowBatteryPolicy LowBattery { get; set; } = new();
}

/// <summary>
/// Lets NutHub declare "low battery" itself, for devices that report it too late or never.
/// </summary>
public sealed class LowBatteryPolicy
{
    /// <summary>On battery with a charge at or below this percentage: add LB.</summary>
    public double? ChargePercent { get; set; }

    /// <summary>On battery with a runtime at or below this many seconds: add LB.</summary>
    public int? RuntimeSeconds { get; set; }

    /// <summary>Ignore the LB flag reported by the device (use only the thresholds above).</summary>
    public bool IgnoreDeviceFlag { get; set; }
}

public enum NutMonitorRole
{
    /// <summary>Not an upsmon account: cannot LOGIN.</summary>
    None,

    /// <summary>"upsmon secondary": may LOGIN.</summary>
    Secondary,

    /// <summary>"upsmon primary": may LOGIN, PRIMARY and FSD.</summary>
    Primary,
}

/// <summary>An account for NUT clients (a section of upsd.users).</summary>
public sealed class NutUserConfig
{
    public string Name { get; set; } = "";

    /// <summary>PBKDF2 hash made by PasswordHasher; NUT sends passwords in clear text, only the hash is kept.</summary>
    public string PasswordHash { get; set; } = "";

    public NutMonitorRole Monitor { get; set; } = NutMonitorRole.None;

    /// <summary>Extra rights, upsd.users "actions": "SET" (write variables) and "FSD".</summary>
    public List<string> Actions { get; set; } = [];

    /// <summary>Instant commands allowed: names, or "ALL".</summary>
    public List<string> InstantCommands { get; set; } = [];

    /// <summary>The UPSes this account may act on; empty means all of them.</summary>
    public List<string> AllowedUps { get; set; } = [];
}

public enum WebRole
{
    /// <summary>Sees the dashboard, UPS details, history and events.</summary>
    Viewer,

    /// <summary>Viewer, plus instant commands, variable writes and FSD on the UPSes.</summary>
    Operator,

    /// <summary>Everything, including configuration and accounts.</summary>
    Admin,
}

public sealed class WebUserConfig
{
    public string Name { get; set; } = "";

    public string? DisplayName { get; set; }

    public string PasswordHash { get; set; } = "";

    public WebRole Role { get; set; } = WebRole.Viewer;

    public bool Disabled { get; set; }

    /// <summary>Ask for a new password at the next sign-in (set for the generated first admin password).</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// Changes whenever the password or the role changes; the web panel stores it in the session and rejects
    /// sessions with an older stamp, which signs out every browser of that account.
    /// </summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
}

public sealed class NotificationSettings
{
    /// <summary>The events notified by every channel that does not list its own.</summary>
    public List<UpsEventType> Events { get; set; } = [.. UpsEventCatalog.DefaultNotified];

    public EmailSettings Email { get; set; } = new();

    public List<WebhookSettings> Webhooks { get; set; } = [];

    public List<CommandHookSettings> Commands { get; set; } = [];
}

public enum SmtpSecurity
{
    /// <summary>STARTTLS when the server offers it.</summary>
    Auto,

    None,

    StartTls,

    /// <summary>TLS from the first byte (port 465).</summary>
    SslOnConnect,
}

public sealed class EmailSettings
{
    public bool Enabled { get; set; }

    public string Host { get; set; } = "";

    public int Port { get; set; } = 587;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;

    public string? Username { get; set; }

    /// <summary>Secret.</summary>
    public string? Password { get; set; }

    public string From { get; set; } = "";

    public List<string> To { get; set; } = [];

    public string SubjectPrefix { get; set; } = "[NutHub]";

    /// <summary>Null uses <see cref="NotificationSettings.Events"/>.</summary>
    public List<UpsEventType>? Events { get; set; }
}

public sealed class WebhookSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public string Url { get; set; } = "";

    /// <summary>POST, PUT or GET.</summary>
    public string Method { get; set; } = "POST";

    public string ContentType { get; set; } = "application/json";

    /// <summary>
    /// Body template with placeholders {ups}, {type}, {severity}, {message}, {timestamp}, {server}, {status},
    /// {charge}, {runtime}; null sends the default JSON document.
    /// </summary>
    public string? BodyTemplate { get; set; }

    /// <summary>Extra headers; values are secrets (they often carry tokens).</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Null uses <see cref="NotificationSettings.Events"/>.</summary>
    public List<UpsEventType>? Events { get; set; }
}

/// <summary>Runs a program for each event (like upsmon NOTIFYCMD), with the details in environment variables.</summary>
public sealed class CommandHookSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>The executable or script.</summary>
    public string Command { get; set; } = "";

    /// <summary>Arguments, with the same placeholders as webhook bodies.</summary>
    public string? Arguments { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Null uses <see cref="NotificationSettings.Events"/>.</summary>
    public List<UpsEventType>? Events { get; set; }
}

/// <summary>
/// Shutting down the machine NutHub runs on when its UPS runs out, the job upsmon does in primary mode.
/// </summary>
public sealed class HostProtectionSettings
{
    public bool Enabled { get; set; }

    /// <summary>The UPSes that power this machine.</summary>
    public List<string> Ups { get; set; } = [];

    /// <summary>
    /// How many of <see cref="Ups"/> must be healthy for the machine to keep running (upsmon MINSUPPLIES); 1 for
    /// a machine with one power supply.
    /// </summary>
    public int MinimumSupplies { get; set; } = 1;

    /// <summary>A UPS is critical when it is on battery with a low battery (OB LB).</summary>
    public bool OnLowBattery { get; set; } = true;

    /// <summary>...or on battery with a charge at or below this percentage.</summary>
    public double? BatteryChargeBelow { get; set; }

    /// <summary>...or on battery with a runtime at or below this many seconds.</summary>
    public int? RuntimeBelowSeconds { get; set; }

    /// <summary>...or on battery for longer than this many seconds.</summary>
    public int? OnBatteryLongerThanSeconds { get; set; }

    /// <summary>...or when someone set a forced shutdown (FSD) on it.</summary>
    public bool OnForcedShutdown { get; set; } = true;

    /// <summary>...or when it was on battery and communication has been lost for longer than this (upsmon DEADTIME).</summary>
    public int CommunicationLostOnBatterySeconds { get; set; } = 15;

    /// <summary>Set FSD on the critical UPSes first, so NUT secondaries shut down before this machine.</summary>
    public bool NotifySecondaries { get; set; } = true;

    /// <summary>How long to wait for the secondaries to log out (upsmon HOSTSYNC).</summary>
    public int SecondariesTimeoutSeconds { get; set; } = 15;

    /// <summary>Grace period between the decision and the shutdown command; cancelled if power returns.</summary>
    public int ShutdownDelaySeconds { get; set; }

    /// <summary>Program and arguments to shut the machine down; null uses the operating system default.</summary>
    public string? ShutdownCommand { get; set; }

    /// <summary>After starting the shutdown, ask the UPS to cut its output and restore it when power returns.</summary>
    public bool PowerOffUps { get; set; }

    /// <summary>The instant command for <see cref="PowerOffUps"/>: "shutdown.return" or "load.off.delay".</summary>
    public string PowerOffCommand { get; set; } = "shutdown.return";

    /// <summary>Delay in seconds written to ups.delay.shutdown before the power-off command, when supported.</summary>
    public int PowerOffDelaySeconds { get; set; } = 120;

    /// <summary>Do everything except the actual shutdown and power-off: log and notify only. For testing.</summary>
    public bool DryRun { get; set; }
}

public sealed class HistorySettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>One sample of each UPS every this many seconds.</summary>
    public int SampleIntervalSeconds { get; set; } = 30;

    public int RetentionDays { get; set; } = 90;

    /// <summary>Events older than this are deleted.</summary>
    public int EventRetentionDays { get; set; } = 365;

    /// <summary>The numeric variables sampled (when the UPS reports them).</summary>
    public List<string> Variables { get; set; } =
    [
        "battery.charge",
        "battery.runtime",
        "battery.voltage",
        "ups.load",
        "ups.realpower",
        "ups.power",
        "ups.temperature",
        "input.voltage",
        "input.frequency",
        "output.voltage",
    ];
}
