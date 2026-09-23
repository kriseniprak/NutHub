using NutHub.Core.Drivers;
using NutHub.Drivers.Snmp.Mibs;
using NutHub.Drivers.Snmp.Transport;

namespace NutHub.Drivers.Snmp;

/// <summary>
/// The options of the "snmp" driver and their validation. Names follow NUT's snmp-ups where they exist
/// (community, secName, secLevel, authProtocol, authPassword, privProtocol, privPassword, mibs).
/// </summary>
internal static class SnmpOptions
{
    private static readonly string[] AuthLevels = ["authNoPriv", "authPriv"];

    public static IReadOnlyList<DriverOption> All { get; } =
    [
        new()
        {
            Key = "host", Label = "Address of the network card", Type = DriverOptionType.Host, Required = true,
            Help = "Host name or IP address of the UPS network management card.",
        },
        new() { Key = "port", Label = "UDP port", Type = DriverOptionType.Port, Default = "161" },
        new()
        {
            Key = "version", Label = "SNMP version", Type = DriverOptionType.Choice, Default = "2c",
            Choices = [new("1", "SNMP v1"), new("2c", "SNMP v2c"), new("3", "SNMP v3 (user-based security)")],
        },
        new()
        {
            Key = "community", Label = "Community", Type = DriverOptionType.Secret, Default = "public",
            Help = "The read community configured on the card.", VisibleWhen = new("version", ["1", "2c"]),
        },
        new()
        {
            Key = "writeCommunity", Label = "Write community", Type = DriverOptionType.Secret, Advanced = true,
            Help = "Used for instant commands and variable writes when the card has a separate read-write community " +
                   "(often \"private\"). Defaults to the read community.",
            VisibleWhen = new("version", ["1", "2c"]),
        },
        new()
        {
            Key = "secName", Label = "User name", VisibleWhen = new("version", ["3"]),
            Help = "The SNMPv3 user (securityName) configured on the card.",
        },
        new()
        {
            Key = "secLevel", Label = "Security level", Type = DriverOptionType.Choice, Default = "noAuthNoPriv",
            VisibleWhen = new("version", ["3"]),
            Choices =
            [
                new("noAuthNoPriv", "No authentication, no encryption"),
                new("authNoPriv", "Authentication, no encryption"),
                new("authPriv", "Authentication and encryption"),
            ],
        },
        new()
        {
            Key = "authProtocol", Label = "Authentication protocol", Type = DriverOptionType.Choice, Default = "MD5",
            VisibleWhen = new("secLevel", AuthLevels),
            Help = "SHA-224 is not available.",
            Choices =
            [
                new("MD5", "MD5"), new("SHA", "SHA-1"), new("SHA256", "SHA-256"), new("SHA384", "SHA-384"),
                new("SHA512", "SHA-512"),
            ],
        },
        new()
        {
            Key = "authPassword", Label = "Authentication password", Type = DriverOptionType.Secret,
            VisibleWhen = new("secLevel", AuthLevels), Help = "At least 8 characters.",
        },
        new()
        {
            Key = "privProtocol", Label = "Privacy (encryption) protocol", Type = DriverOptionType.Choice, Default = "DES",
            VisibleWhen = new("secLevel", ["authPriv"]),
            Choices = [new("DES", "DES"), new("AES", "AES-128"), new("AES192", "AES-192"), new("AES256", "AES-256")],
        },
        new()
        {
            Key = "privPassword", Label = "Privacy password", Type = DriverOptionType.Secret,
            VisibleWhen = new("secLevel", ["authPriv"]), Help = "At least 8 characters.",
        },
        new()
        {
            Key = "mibs", Label = "MIB", Type = DriverOptionType.Choice, Default = MibCatalog.Auto,
            Help = "Which mapping to use. \"Automatic\" recognises the card from its sysObjectID and the objects it answers.",
            Choices =
            [
                new(MibCatalog.Auto, "Automatic"),
                .. MibCatalog.All.Select(m => new DriverOptionChoice(m.Name, m.DisplayName)),
            ],
        },
        new()
        {
            Key = "timeoutMs", Label = "Timeout (ms)", Type = DriverOptionType.Integer, Default = "1000", Min = 100,
            Max = 30000, Advanced = true, Help = "How long to wait for each answer before asking again.",
        },
        new()
        {
            Key = "retries", Label = "Retries", Type = DriverOptionType.Integer, Default = "3", Min = 0, Max = 10,
            Advanced = true, Help = "How many times a request is sent again after a timeout.",
        },
    ];

    /// <summary>Reads and validates the options; every error names the option at fault.</summary>
    /// <exception cref="DriverConfigurationException">An option is missing, malformed or inconsistent.</exception>
    public static SnmpSettings Parse(DriverOptionReader read)
    {
        string host = read.GetRequiredString("host");
        if (host.Any(char.IsWhiteSpace) || Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown)
        {
            throw new DriverConfigurationException($"'{host}' is not a valid host name or IP address.", "host");
        }

        SnmpProtocolVersion version = ParseVersion(read.GetString("version", "2c")!);
        string mib = read.GetString("mibs", MibCatalog.Auto)!;
        if (!string.Equals(mib, MibCatalog.Auto, StringComparison.OrdinalIgnoreCase))
        {
            mib = MibCatalog.Find(mib)?.Name
                  ?? throw new DriverConfigurationException(
                      $"Unknown MIB '{mib}'. Use auto or one of: {string.Join(", ", MibCatalog.All.Select(m => m.Name))}.",
                      "mibs");
        }
        else
        {
            mib = MibCatalog.Auto;
        }

        var settings = new SnmpSettings
        {
            Host = host.Trim('[', ']'),
            Port = read.GetPort("port", 161),
            Version = version,
            Mib = mib,
            Timeout = TimeSpan.FromMilliseconds(read.GetInt("timeoutMs", 1000, 100, 30000)),
            Retries = read.GetInt("retries", 3, 0, 10),
        };

        if (version != SnmpProtocolVersion.V3)
        {
            string community = read.GetString("community", "public")!;
            return settings with
            {
                Community = community,
                WriteCommunity = read.GetString("writeCommunity", community)!,
            };
        }

        string user = read.GetString("secName")
                      ?? throw new DriverConfigurationException("SNMPv3 needs a user name.", "secName");
        if (user.Length > 32)
        {
            throw new DriverConfigurationException("The SNMPv3 user name is limited to 32 characters.", "secName");
        }

        SnmpSecurityLevel level = read.GetChoice("secLevel", "noAuthNoPriv", "noAuthNoPriv", "authNoPriv", "authPriv") switch
        {
            "authNoPriv" => SnmpSecurityLevel.AuthNoPriv,
            "authPriv" => SnmpSecurityLevel.AuthPriv,
            _ => SnmpSecurityLevel.NoAuthNoPriv,
        };
        settings = settings with { SecurityName = user, SecurityLevel = level };

        if (level != SnmpSecurityLevel.NoAuthNoPriv)
        {
            settings = settings with
            {
                AuthProtocol = read.GetChoice("authProtocol", "MD5", "MD5", "SHA", "SHA256", "SHA384", "SHA512") switch
                {
                    "SHA" => SnmpAuthProtocol.Sha1,
                    "SHA256" => SnmpAuthProtocol.Sha256,
                    "SHA384" => SnmpAuthProtocol.Sha384,
                    "SHA512" => SnmpAuthProtocol.Sha512,
                    _ => SnmpAuthProtocol.Md5,
                },
                AuthPassword = Password(read, "authPassword", "authentication"),
            };
        }

        if (level == SnmpSecurityLevel.AuthPriv)
        {
            settings = settings with
            {
                PrivProtocol = read.GetChoice("privProtocol", "DES", "DES", "AES", "AES192", "AES256") switch
                {
                    "AES" => SnmpPrivProtocol.Aes128,
                    "AES192" => SnmpPrivProtocol.Aes192,
                    "AES256" => SnmpPrivProtocol.Aes256,
                    _ => SnmpPrivProtocol.Des,
                },
                PrivPassword = Password(read, "privPassword", "privacy"),
            };
        }

        // Builds the providers once, so an algorithm this system lacks is reported now, naming the option.
        _ = SnmpSecurity.CreatePrivacyProvider(settings);
        return settings;
    }

    private static SnmpProtocolVersion ParseVersion(string text) => text.Trim().ToLowerInvariant() switch
    {
        "1" or "v1" => SnmpProtocolVersion.V1,
        "2c" or "v2c" or "2" => SnmpProtocolVersion.V2c,
        "3" or "v3" => SnmpProtocolVersion.V3,
        _ => throw new DriverConfigurationException("The option 'version' must be one of: 1, 2c, 3.", "version"),
    };

    private static string Password(DriverOptionReader read, string key, string what)
    {
        string? password = read.GetString(key);
        if (password is null)
        {
            throw new DriverConfigurationException($"The {what} password is required for this security level.", key);
        }

        if (password.Length < 8)
        {
            throw new DriverConfigurationException(
                $"The {what} password must be at least 8 characters long (RFC 3414).", key);
        }

        return password;
    }
}
