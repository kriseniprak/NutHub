using System.Globalization;
using System.Text.RegularExpressions;
using NutHub.Core.Model;

namespace NutHub.Drivers.Net.Apcupsd;

/// <summary>The NUT view of one apcupsd status.</summary>
/// <param name="Variables">NUT variables, without driver.*.</param>
/// <param name="CommunicationLost">apcupsd itself cannot reach the UPS: its values are old.</param>
/// <param name="Status">The raw STATUS field, for messages.</param>
/// <param name="Version">apcupsd's VERSION field.</param>
/// <param name="UpsName">apcupsd's UPSNAME field.</param>
internal sealed record ApcupsdReading(
    IReadOnlyDictionary<string, string> Variables,
    bool CommunicationLost,
    string? Status,
    string? Version,
    string? UpsName);

/// <summary>
/// Translates the fields of an apcupsd status ("KEY : value" lines, as printed by apcaccess) into NUT variables.
/// Field names follow apcupsd's src/lib/apcstatus.c; the mapping follows NUT drivers/apcupsd-ups.h, with units
/// converted to NUT's (minutes of TIMELEFT and MINTIMEL become seconds) and the text after the numbers
/// ("230.0 Volts", "100.0 Percent") dropped.
/// </summary>
internal static partial class ApcupsdStatusMapper
{
    private enum Kind
    {
        /// <summary>A number, kept in its unit.</summary>
        Number,

        /// <summary>A number of minutes, published in seconds.</summary>
        Minutes,

        /// <summary>A temperature; converted to Celsius when apcupsd reports Fahrenheit.</summary>
        Temperature,

        /// <summary>Text kept as is.</summary>
        Text,

        /// <summary>The first word of a date ("2005-05-04" of "2005-05-04 12:00:00").</summary>
        Date,
    }

    // Mapping from NUT drivers/apcupsd-ups.h (nut_data), plus a few fields that NUT names have for.
    private static readonly (string Field, string Variable, Kind Kind)[] Map =
    [
        ("BCHARGE", "battery.charge", Kind.Number),
        ("TIMELEFT", "battery.runtime", Kind.Minutes),
        ("MBATTCHG", "battery.charge.low", Kind.Number),
        ("MINTIMEL", "battery.runtime.low", Kind.Minutes),
        ("MINTIMELEFT", "battery.runtime.low", Kind.Minutes),
        ("RETPCT", "battery.charge.restart", Kind.Number),
        ("BATTV", "battery.voltage", Kind.Number),
        ("NOMBATTV", "battery.voltage.nominal", Kind.Number),
        ("BATTDATE", "battery.date", Kind.Date),
        ("BADBATTS", "battery.packs.bad", Kind.Number),
        ("LOADPCT", "ups.load", Kind.Number),
        ("ITEMP", "ups.temperature", Kind.Temperature),
        ("NOMPOWER", "ups.realpower.nominal", Kind.Number),
        ("NOMAPNT", "ups.power.nominal", Kind.Number),
        ("SERIALNO", "ups.serial", Kind.Text),
        ("MANDATE", "ups.mfr.date", Kind.Date),
        ("UPSNAME", "ups.id", Kind.Text),
        ("DWAKE", "ups.delay.start", Kind.Number),
        ("DSHUTD", "ups.delay.shutdown", Kind.Number),
        ("LINEV", "input.voltage", Kind.Number),
        ("MAXLINEV", "input.voltage.maximum", Kind.Number),
        ("MINLINEV", "input.voltage.minimum", Kind.Number),
        ("NOMINV", "input.voltage.nominal", Kind.Number),
        ("LINEFREQ", "input.frequency", Kind.Number),
        ("LOTRANS", "input.transfer.low", Kind.Number),
        ("HITRANS", "input.transfer.high", Kind.Number),
        ("LASTXFER", "input.transfer.reason", Kind.Text),
        ("SENSE", "input.sensitivity", Kind.Text),
        ("OUTPUTV", "output.voltage", Kind.Number),
        ("NOMOUTV", "output.voltage.nominal", Kind.Number),
        ("OUTCURNT", "output.current", Kind.Number),
        ("AMBTEMP", "ambient.temperature", Kind.Temperature),
        ("HUMIDITY", "ambient.humidity", Kind.Number),
    ];

    /// <summary>
    /// Bits of STATFLAG (apcupsd include/defines.h, UPS_*), used when STATUS does not carry the flags itself:
    /// apcupsd replaces the whole STATUS by "SHUTTING DOWN" or "COMMLOST" when those apply.
    /// </summary>
    private const long FlagCalibration = 0x1, FlagTrim = 0x2, FlagBoost = 0x4, FlagOnline = 0x8, FlagOnBattery = 0x10,
                       FlagOverload = 0x20, FlagBatteryLow = 0x40, FlagReplaceBattery = 0x80, FlagCommLost = 0x100,
                       FlagShutdown = 0x200;

    /// <summary>Splits "KEY : value" lines into fields; keys are upper case, later duplicates win.</summary>
    public static Dictionary<string, string> ParseFields(IEnumerable<string> lines)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string key = line[..colon].Trim().ToUpperInvariant();
            string value = line[(colon + 1)..].Trim();
            if (key.Length > 0 && key.Length <= 16)
            {
                fields[key] = value;
            }
        }

        return fields;
    }

    public static ApcupsdReading ToReading(IReadOnlyDictionary<string, string> fields)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, variable, kind) in Map)
        {
            if (fields.TryGetValue(field, out string? raw) && Convert(raw, kind) is { } value)
            {
                vars[variable] = value;
            }
        }

        string? model = Text(fields, "MODEL") ?? Text(fields, "APCMODEL");
        if (model is not null)
        {
            vars["ups.model"] = model;
            if (IsApcModel(model) || (Text(fields, "APCMODEL") is { } apcModel && IsApcModel(apcModel)))
            {
                vars["ups.mfr"] = "APC";
            }
        }

        if (Text(fields, "FIRMWARE") is { } firmware)
        {
            // Smart-UPS report "UPS 09.3 / ID=18": the part after the slash is the auxiliary firmware.
            int slash = firmware.IndexOf('/');
            if (slash > 0)
            {
                vars["ups.firmware"] = firmware[..slash].Trim();
                if (firmware[(slash + 1)..].Trim() is { Length: > 0 } aux)
                {
                    vars["ups.firmware.aux"] = aux;
                }
            }
            else
            {
                vars["ups.firmware"] = firmware;
            }
        }

        string? selfTest = Text(fields, "SELFTEST");
        if (selfTest is not null)
        {
            vars["ups.test.result"] = DescribeSelfTest(selfTest);
        }

        string? status = Text(fields, "STATUS");
        long? statFlag = ParseStatFlag(Text(fields, "STATFLAG"));
        bool commLost = IsCommLost(status, statFlag);
        if (!commLost && BuildStatus(status, statFlag, selfTest, out bool noBattery) is { Length: > 0 } nutStatus)
        {
            vars["ups.status"] = nutStatus;
            if (noBattery)
            {
                vars["ups.alarm"] = "No battery installed";
            }
        }

        return new ApcupsdReading(vars, commLost, status, Text(fields, "VERSION"), Text(fields, "UPSNAME"));
    }

    /// <summary>Extracts the number at the start of "230.0 Volts", "  24.0 Percent Load Capacity"; null for "N/A".</summary>
    internal static double? LeadingNumber(string? text)
    {
        if (text is null)
        {
            return null;
        }

        Match m = LeadingNumberRegex().Match(text.Trim());
        return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : null;
    }

    private static string? Convert(string raw, Kind kind)
    {
        switch (kind)
        {
            case Kind.Number:
                return LeadingNumber(raw) is { } n ? NutFormat.Number(n, 2) : null;
            case Kind.Minutes:
                return LeadingNumber(raw) is { } minutes ? NutFormat.Number(Math.Max(0, minutes) * 60, 0) : null;
            case Kind.Temperature:
                if (LeadingNumber(raw) is not { } t)
                {
                    return null;
                }

                string trimmed = raw.Trim();
                string unit = trimmed[LeadingNumberRegex().Match(trimmed).Length..].Trim();
                if (unit.StartsWith('F'))
                {
                    t = (t - 32) * 5 / 9;
                }

                return NutFormat.Number(t, 1);
            case Kind.Date:
                string date = raw.Trim().Split(' ', 2)[0];
                return date.Length > 0 && !IsNotAvailable(date) ? date : null;
            default:
                string text = raw.Trim();
                return text.Length > 0 && !IsNotAvailable(text) ? text : null;
        }
    }

    private static string? Text(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? v) && v.Trim() is { Length: > 0 } t && !IsNotAvailable(t) ? t : null;

    private static bool IsNotAvailable(string text) => text.Equals("N/A", StringComparison.OrdinalIgnoreCase);

    private static bool IsCommLost(string? status, long? statFlag)
    {
        if (status is not null)
        {
            string upper = status.ToUpperInvariant();
            // SLAVEDOWN: an apcupsd slave lost its master, so it knows nothing of the UPS either.
            if (upper.Contains("COMMLOST", StringComparison.Ordinal) || upper.Contains("SLAVEDOWN", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        return statFlag is { } flags && (flags & FlagCommLost) != 0;
    }

    /// <summary>Builds ups.status from STATUS, falling back to STATFLAG for what STATUS leaves out.</summary>
    private static string BuildStatus(string? status, long? statFlag, string? selfTest, out bool noBattery)
    {
        noBattery = false;
        var tokens = new List<string>();
        void Add(string token)
        {
            if (!tokens.Contains(token))
            {
                tokens.Add(token);
            }
        }

        string upper = status?.ToUpperInvariant() ?? string.Empty;
        bool shuttingDown = upper.Contains("SHUTTING DOWN", StringComparison.Ordinal);
        if (shuttingDown)
        {
            // apcupsd is shutting its host down: for NUT clients that is a forced shutdown.
            Add("FSD");
        }

        if (status is not null && !shuttingDown)
        {
            foreach (string word in upper.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                switch (word)
                {
                    case "ONLINE": Add("OL"); break;
                    case "ONBATT": Add("OB"); break;
                    case "LOWBATT": Add("LB"); break;
                    case "REPLACEBATT": Add("RB"); break;
                    case "CAL": Add("CAL"); break;
                    case "TRIM": Add("TRIM"); break;
                    case "BOOST": Add("BOOST"); break;
                    case "OVERLOAD": Add("OVER"); break;
                    case "SELFTEST": Add("TEST"); break;
                    case "NOBATT": noBattery = true; break;
                }
            }
        }

        // STATFLAG fills in what STATUS does not say: the flags hidden by "SHUTTING DOWN", or everything when STATUS
        // is missing or carries no line / battery state.
        if (statFlag is { } flags && !tokens.Contains("OL") && !tokens.Contains("OB"))
        {
            if ((flags & FlagShutdown) != 0) Add("FSD");
            if ((flags & FlagOnline) != 0) Add("OL");
            if ((flags & FlagOnBattery) != 0) Add("OB");
            if ((flags & FlagBatteryLow) != 0) Add("LB");
            if ((flags & FlagReplaceBattery) != 0) Add("RB");
            if ((flags & FlagCalibration) != 0) Add("CAL");
            if ((flags & FlagTrim) != 0) Add("TRIM");
            if ((flags & FlagBoost) != 0) Add("BOOST");
            if ((flags & FlagOverload) != 0) Add("OVER");

            // The battery-present bit is not set by every apcupsd version, so its absence proves nothing here;
            // STATUS says NOBATT when it matters.
        }

        if (selfTest is not null && selfTest.Trim().Equals("IP", StringComparison.OrdinalIgnoreCase))
        {
            Add("TEST");
        }

        if (noBattery)
        {
            Add("ALARM");
        }

        return string.Join(' ', tokens);
    }

    private static long? ParseStatFlag(string? text)
    {
        if (text is null)
        {
            return null;
        }

        string word = text.Split(' ', 2)[0];
        if (word.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            word = word[2..];
        }

        return long.TryParse(word, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long v) ? v : null;
    }

    /// <summary>SELFTEST codes of apcupsd, in the words NUT drivers use for ups.test.result.</summary>
    private static string DescribeSelfTest(string code) => code.Trim().ToUpperInvariant() switch
    {
        "OK" => "Done and passed",
        "NO" => "No test initiated",
        "BT" => "Done and error (battery capacity)",
        "NG" => "Done and error (overload)",
        "WN" => "Done and warning",
        "IP" => "In progress",
        "??" => "Unknown",
        _ => code.Trim(),
    };

    private static bool IsApcModel(string model) => ApcModelRegex().IsMatch(model);

    [GeneratedRegex(@"^[+-]?(\d+(\.\d*)?|\.\d+)")]
    private static partial Regex LeadingNumberRegex();

    // APC product families and the model codes printed by apcupsd for them (BX1400U, SMT1500I, SUA1000...).
    [GeneratedRegex(@"\bAPC\b|Back-?UPS|Smart-?UPS|Symmetra|Matrix-UPS|^(BX|BR|BE|BK|BN|BVK|BVX|SMT|SMX|SMC|SUA|SURT|SRT|SUM|SC|SU)\d",
                    RegexOptions.IgnoreCase)]
    private static partial Regex ApcModelRegex();
}
