using System.Globalization;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Core.Security;

namespace NutHub.Web.Import;

/// <summary>What an import found: UPSes (secrets in clear text, encrypted when saved), NUT accounts, warnings.</summary>
internal sealed record NutImportResult(List<UpsConfig> Ups, List<NutUserConfig> Users, List<string> Warnings);

/// <summary>
/// Turns NUT's ups.conf and upsd.users into NutHub UPSes and NUT accounts. Only the drivers NutHub has are mapped;
/// every option that cannot be carried over is reported instead of being silently dropped.
/// </summary>
internal sealed class NutImporter(IDriverCatalog drivers, IPasswordHasher hasher)
{
    // Options every NUT driver understands that have no meaning here (process management, timing of upsdrvctl).
    private static readonly HashSet<string> IgnoredGeneral = new(StringComparer.OrdinalIgnoreCase)
    {
        "sdorder", "maxstartdelay", "maxretry", "retrydelay", "synchronous", "nolock", "user", "group", "debug_min",
        "LOCKFN", "chroot", "notransferoids", "pollfreq",
    };

    /// <param name="hashPasswords">False for a preview: hashing is deliberately slow and nothing is saved.</param>
    public NutImportResult Import(string? upsConf, string? upsdUsers, bool hashPasswords)
    {
        var warnings = new List<string>();
        var ups = string.IsNullOrWhiteSpace(upsConf) ? [] : ImportUps(upsConf, warnings);
        var users = string.IsNullOrWhiteSpace(upsdUsers) ? [] : ImportUsers(upsdUsers, warnings, hashPasswords);
        return new NutImportResult(ups, users, warnings);
    }

    // ----- ups.conf -----

    private List<UpsConfig> ImportUps(string text, List<string> warnings)
    {
        var result = new List<UpsConfig>();
        foreach (NutConfSection section in NutConfParser.ReadSections(text).Sections)
        {
            if (!NutFormat.IsValidUpsName(section.Name))
            {
                warnings.Add($"ups.conf line {section.Line}: '{section.Name}' is not a valid UPS name; skipped.");
                continue;
            }

            if (result.Any(u => string.Equals(u.Name, section.Name, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"ups.conf line {section.Line}: [{section.Name}] appears twice; the second one is skipped.");
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (NutConfLine line in section.Lines)
            {
                var (key, v) = NutConfParser.KeyValues(line);
                values[key] = v.Count == 0 ? "" : string.Join(' ', v);
            }

            UpsConfig? ups = MapUps(section.Name, values, warnings);
            if (ups is not null)
            {
                result.Add(ups);
            }
        }

        return result;
    }

    private UpsConfig? MapUps(string name, Dictionary<string, string> nut, List<string> warnings)
    {
        string nutDriver = nut.GetValueOrDefault("driver", "").Trim();
        string port = nut.GetValueOrDefault("port", "").Trim();
        var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "driver", "port", "desc" };
        string driver;

        void Map(string nutKey, string key, Func<string, string?>? convert = null)
        {
            if (nut.TryGetValue(nutKey, out string? value))
            {
                used.Add(nutKey);
                string? converted = convert is null ? value : convert(value);
                if (converted is not null)
                {
                    mapped[key] = converted;
                }
                else
                {
                    warnings.Add($"[{name}]: the value '{value}' of {nutKey} cannot be used; ignored.");
                }
            }
        }

        void Flag(string nutKey, string key)
        {
            if (nut.ContainsKey(nutKey))
            {
                used.Add(nutKey);
                mapped[key] = "true";
            }
        }

        switch (nutDriver.ToLowerInvariant())
        {
            case "usbhid-ups":
                driver = "usbhid";
                Map("vendorid", "vendorId");
                Map("productid", "productId");
                Map("serial", "serial");
                Map("product", "product");
                // NUT matches its subdriver option as a regular expression on names like "APC HID 0.100".
                Map("subdriver", "subdriver", HidSubdriverId);
                Map("offdelay", "offDelay");
                Map("ondelay", "onDelay");
                Flag("onlinedischarge", "onlineDischarge");
                Flag("onlinedischarge_calibration", "onlineDischargeCalibration");
                break;
            case "nutdrv_qx" or "blazer_ser" or "blazer_usb":
                driver = "megatec";
                bool usb = nutDriver.Equals("blazer_usb", StringComparison.OrdinalIgnoreCase) ||
                           (nutDriver.Equals("nutdrv_qx", StringComparison.OrdinalIgnoreCase) &&
                            (port.Equals("auto", StringComparison.OrdinalIgnoreCase) || nut.ContainsKey("vendorid") ||
                             nut.ContainsKey("subdriver")));
                mapped["transport"] = usb ? "usb" : "serial";
                if (!usb)
                {
                    mapped["port"] = port;
                }

                Map("vendorid", "vendorId");
                Map("productid", "productId");
                Map("serial", "serial");
                Map("subdriver", "usbSubdriver");
                Map("protocol", "protocol");
                Map("ondelay", "onDelay");
                Map("offdelay", "offDelay");
                Map("cablepower", "cablePower");
                Map("runtimecal", "runtimeCal");
                Map("chargetime", "chargeTime");
                Map("idleload", "idleLoad");
                Map("default.battery.voltage.high", "batteryVoltageHigh");
                Map("default.battery.voltage.low", "batteryVoltageLow");
                Map("default.battery.voltage.nominal", "batteryVoltageNominal");
                Map("override.battery.voltage.high", "batteryVoltageHigh");
                Map("override.battery.voltage.low", "batteryVoltageLow");
                Map("override.battery.voltage.nominal", "batteryVoltageNominal");
                Map("battery.packs", "batteryPacks");
                Map("default.battery.packs", "batteryPacks");
                Flag("ignoresab", "ignoreSab");
                Flag("norating", "noRating");
                Flag("novendor", "noVendor");
                break;
            case "apcsmart":
                driver = "apcsmart";
                mapped["transport"] = "serial";
                mapped["port"] = port;
                Map("sdtype", "shutdownType");
                Map("awd", "wakeUpDelay");
                Map("cshdelay", "csDelay");
                break;
            case "snmp-ups":
                driver = "snmp";
                SplitHostPort(port, "host", "port", mapped);
                Map("community", "community");
                Map("snmp_version", "version", v => v.ToLowerInvariant() switch
                {
                    "v1" => "1",
                    "v2c" => "2c",
                    "v3" => "3",
                    _ => null,
                });
                Map("mibs", "mibs", v => v.ToLowerInvariant());
                Map("secName", "secName");
                Map("secLevel", "secLevel");
                Map("authPassword", "authPassword");
                Map("privPassword", "privPassword");
                Map("authProtocol", "authProtocol");
                Map("privProtocol", "privProtocol");
                Map("snmp_retries", "retries");
                Map("snmp_timeout", "timeoutMs", v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s > 0
                    ? (s * 1000).ToString(CultureInfo.InvariantCulture)
                    : null);
                break;
            case "dummy-ups" when port.Contains('@'):
                // Repeater mode: "ups@host[:port]".
                driver = "nut";
                int at = port.IndexOf('@');
                mapped["upsName"] = port[..at];
                SplitHostPort(port[(at + 1)..], "host", "port", mapped);
                break;
            case "apcupsd-ups":
                driver = "apcupsd";
                SplitHostPort(port, "host", "port", mapped);
                break;
            default:
                warnings.Add(nutDriver.Length == 0
                    ? $"[{name}]: no driver is set; skipped."
                    : nutDriver.Equals("dummy-ups", StringComparison.OrdinalIgnoreCase)
                        ? $"[{name}]: dummy-ups with a simulation file is not supported (use the simulated driver); skipped."
                        : $"[{name}]: the NUT driver {nutDriver} has no NutHub equivalent; skipped.");
                return null;
        }

        IUpsDriverFactory? factory = drivers.Find(driver);
        if (factory is null)
        {
            warnings.Add($"[{name}]: the driver '{driver}' is not available in this build; skipped.");
            return null;
        }

        var ups = new UpsConfig
        {
            Name = name,
            Driver = factory.Id,
            Description = nut.TryGetValue("desc", out string? desc) && !string.IsNullOrWhiteSpace(desc) ? desc.Trim() : null,
        };

        foreach (var (key, value) in nut)
        {
            if (used.Contains(key) || IgnoredGeneral.Contains(key))
            {
                continue;
            }

            if (key.StartsWith("override.", StringComparison.OrdinalIgnoreCase) && key.Length > 9 && value.Length > 0)
            {
                ups.Overrides[key[9..]] = value;
            }
            else if (key.Equals("ignorelb", StringComparison.OrdinalIgnoreCase))
            {
                ups.LowBattery.IgnoreDeviceFlag = true;
            }
            else if (key.Equals("pollinterval", StringComparison.OrdinalIgnoreCase) &&
                     double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0)
            {
                ups.PollIntervalSeconds = Math.Clamp(seconds, 0.5, 300);
            }
            else
            {
                warnings.Add($"[{name}]: the option {key} is not supported; ignored.");
            }
        }

        // Keep only what the NutHub driver accepts, so the import never produces a UPS its driver refuses.
        foreach (var (key, value) in mapped)
        {
            DriverOption? option = factory.Options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));
            if (option is null)
            {
                warnings.Add($"[{name}]: the {factory.Id} driver has no option {key}; ignored.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (option.Type == DriverOptionType.Choice && option.Choices is { Count: > 0 } choices &&
                !choices.Any(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"[{name}]: '{value}' is not a valid {option.Label}; ignored.");
                continue;
            }

            ups.Options[option.Key] = value;
        }

        foreach (DriverOption option in factory.Options.Where(o => o.Required))
        {
            if (!ups.Options.ContainsKey(option.Key) && Admin.DriverOptionValidator.IsVisible(factory, option, ups.Options))
            {
                warnings.Add($"[{name}]: {option.Label} is required; set it after the import.");
            }
        }

        return ups;
    }

    private static void SplitHostPort(string value, string hostKey, string portKey, Dictionary<string, string> target)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return;
        }

        // "[::1]:3493", "host:3493", "host", "::1"
        if (value.StartsWith('[') && value.IndexOf(']') is > 0 and var close)
        {
            target[hostKey] = value[1..close];
            if (close + 2 < value.Length && value[close + 1] == ':')
            {
                target[portKey] = value[(close + 2)..];
            }

            return;
        }

        int colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(':') == colon)
        {
            target[hostKey] = value[..colon];
            target[portKey] = value[(colon + 1)..];
        }
        else
        {
            target[hostKey] = value;
        }
    }

    // ----- upsd.users -----

    private List<NutUserConfig> ImportUsers(string text, List<string> warnings, bool hashPasswords)
    {
        var result = new List<NutUserConfig>();
        foreach (NutConfSection section in NutConfParser.ReadSections(text).Sections)
        {
            string name = section.Name;
            // Dots only ("..") would make an account the panel can never address: its URL is normalised away.
            if (name.Length is 0 or > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@') ||
                name.All(c => c == '.'))
            {
                warnings.Add($"upsd.users line {section.Line}: '{name}' is not a valid account name; skipped.");
                continue;
            }

            if (result.Any(u => u.Name == name))
            {
                warnings.Add($"upsd.users line {section.Line}: [{name}] appears twice; the second one is skipped.");
                continue;
            }

            var user = new NutUserConfig { Name = name };
            string? password = null;
            foreach (NutConfLine line in section.Lines)
            {
                var (key, values) = NutConfParser.KeyValues(line);
                switch (key.ToLowerInvariant())
                {
                    case "password":
                        password = values.Count > 0 ? values[0] : null;
                        break;
                    case "actions":
                        foreach (string action in values)
                        {
                            string upper = action.ToUpperInvariant();
                            if (upper is "SET" or "FSD")
                            {
                                if (!user.Actions.Contains(upper))
                                {
                                    user.Actions.Add(upper);
                                }
                            }
                            else
                            {
                                warnings.Add($"[{name}]: the action {action} is not supported; ignored.");
                            }
                        }

                        break;
                    case "instcmds":
                        foreach (string command in values)
                        {
                            string c = command.Equals("all", StringComparison.OrdinalIgnoreCase) ? "ALL" : command;
                            if (!user.InstantCommands.Contains(c, StringComparer.OrdinalIgnoreCase))
                            {
                                user.InstantCommands.Add(c);
                            }
                        }

                        break;
                    case "upsmon":
                        string type = values.Count > 0 ? values[0].ToLowerInvariant() : "";
                        if (type is "primary" or "master")
                        {
                            user.Monitor = NutMonitorRole.Primary;
                        }
                        else if (type is "secondary" or "slave")
                        {
                            user.Monitor = NutMonitorRole.Secondary;
                        }
                        else
                        {
                            warnings.Add($"[{name}]: unknown upsmon type '{type}'; ignored.");
                        }

                        break;
                    case "allowfrom":
                        warnings.Add($"[{name}]: allowfrom is obsolete in NUT and ignored.");
                        break;
                    default:
                        warnings.Add($"[{name}]: the setting {key} is not supported; ignored.");
                        break;
                }
            }

            if (string.IsNullOrEmpty(password))
            {
                warnings.Add($"[{name}]: no password; skipped.");
                continue;
            }

            user.PasswordHash = hashPasswords ? hasher.Hash(password) : "";
            result.Add(user);
        }

        return result;
    }

    private static readonly (string Prefix, string Id)[] HidSubdrivers =
    [
        ("apc", "apc"), ("mge", "mge"), ("eaton", "mge"), ("cyberpower", "cps"), ("cps", "cps"),
        ("tripp", "tripplite"), ("belkin", "belkin"), ("liebert", "liebert"), ("powercom", "powercom"),
        ("delta", "delta"), ("salicru", "salicru"), ("openups", "openups"), ("idowell", "idowell"),
        ("arduino", "arduino"), ("ever", "ever"), ("ecoflow", "ecoflow"), ("legrand", "legrand"),
        ("powervar", "powervar"),
    ];

    /// <summary>"APC HID", "CyberPower", "MGE" -> the NutHub subdriver id, or null (automatic) when unknown.</summary>
    private static string? HidSubdriverId(string nutValue)
    {
        string value = new string(nutValue.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        foreach (var (prefix, id) in HidSubdrivers)
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                return id;
            }
        }

        return null;
    }
}
