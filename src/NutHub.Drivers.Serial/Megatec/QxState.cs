using NutHub.Drivers.Serial.Common;

namespace NutHub.Drivers.Serial.Megatec;

/// <summary>The status bits nutdrv_qx tracks (status_bit_t in NUT drivers/nutdrv_qx.h), plus ECO.</summary>
[Flags]
internal enum QxStatusBits
{
    None = 0,
    Online = 1 << 0,
    LowBattery = 1 << 1,
    ReplaceBattery = 1 << 2,
    Charging = 1 << 3,
    Discharging = 1 << 4,
    Bypass = 1 << 5,
    Calibrating = 1 << 6,
    Off = 1 << 7,
    Overload = 1 << 8,
    Trim = 1 << 9,
    Boost = 1 << 10,
    ShutdownImminent = 1 << 11,
    Eco = 1 << 12,
}

/// <summary>
/// What the driver knows about one UPS during a connection: the variables read so far (NUT's dstate), the status bits
/// and alarms of the current poll, and a few values the parsers share. Rebuilt on every reconnection.
/// </summary>
internal sealed class QxState
{
    private static readonly (string Token, QxStatusBits Bit)[] Tokens =
    [
        ("OL", QxStatusBits.Online),
        ("LB", QxStatusBits.LowBattery),
        ("RB", QxStatusBits.ReplaceBattery),
        ("CHRG", QxStatusBits.Charging),
        ("DISCHRG", QxStatusBits.Discharging),
        ("BYPASS", QxStatusBits.Bypass),
        ("CAL", QxStatusBits.Calibrating),
        ("OFF", QxStatusBits.Off),
        ("OVER", QxStatusBits.Overload),
        ("TRIM", QxStatusBits.Trim),
        ("BOOST", QxStatusBits.Boost),
        ("FSD", QxStatusBits.ShutdownImminent),
        ("ECO", QxStatusBits.Eco),
    ];

    /// <summary>Variables in NUT naming, as read from the UPS; values not refreshed by a poll keep their last reading.</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>Values without a NUT variable (QX_FLAG_NONUT items).</summary>
    public Dictionary<string, string> Internal { get; } = new(StringComparer.Ordinal);

    public QxStatusBits Status { get; set; }

    /// <summary>The alarms of the current poll, in the order they were raised.</summary>
    public List<string> Alarms { get; } = [];

    /// <summary>The battery voltage as the UPS reported it this poll, before any multiplication by the packs.</summary>
    public double? RawBatteryVoltage { get; set; }

    /// <summary>Battery packs known or detected (1 until the battery estimation decides otherwise).</summary>
    public double BatteryPacks { get; set; } = 1;

    /// <summary>Multiply the published battery.voltage by <see cref="BatteryPacks"/>.</summary>
    public bool BatteryVoltageReportsOnePack { get; set; }

    /// <summary>An instant command or variable write succeeded: re-read the semi-static items at the next poll.</summary>
    public bool DataChanged { get; set; }

    /// <summary>Protocol-specific switches the parsers set while reading (e.g. BestUPS's inverted bypass bit).</summary>
    public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);

    public string? Get(string name) => Values.TryGetValue(name, out string? value) ? value : null;

    public double? Number(string name) => CText.TryStrToD(Get(name), out double value) ? value : null;

    public bool Has(QxStatusBits bit) => (Status & bit) != 0;

    /// <summary>Starts a new poll: no status bits, no alarms (NUT status_init/alarm_init).</summary>
    public void BeginWalk()
    {
        Status = QxStatusBits.None;
        Alarms.Clear();
        RawBatteryVoltage = null;
    }

    /// <summary>
    /// Applies a status change as NUT's update_status does: "LB" sets a bit, "!LB" clears it; unknown tokens are ignored.
    /// Returns false for an unknown token.
    /// </summary>
    public bool UpdateStatus(string change)
    {
        bool clear = change.StartsWith('!');
        string token = clear ? change[1..] : change;
        foreach (var (t, bit) in Tokens)
        {
            if (string.Equals(t, token, StringComparison.OrdinalIgnoreCase))
            {
                Status = clear ? Status & ~bit : Status | bit;
                return true;
            }
        }

        return false;
    }

    public void AddAlarm(string alarm)
    {
        if (!string.IsNullOrWhiteSpace(alarm) && !Alarms.Contains(alarm))
        {
            Alarms.Add(alarm);
        }
    }

    /// <summary>
    /// ups.status in the order of NUT's ups_status_set: OL or OB first, then the other conditions; ALARM in front when an
    /// alarm is active (as NUT's status_commit does).
    /// </summary>
    public string FormatStatus(bool alarmActive)
    {
        var parts = new List<string>(6);
        if (alarmActive)
        {
            parts.Add("ALARM");
        }

        parts.Add(Has(QxStatusBits.Online) ? "OL" : "OB");
        AddIf(parts, QxStatusBits.Discharging, "DISCHRG");
        AddIf(parts, QxStatusBits.Charging, "CHRG");
        AddIf(parts, QxStatusBits.LowBattery, "LB");
        AddIf(parts, QxStatusBits.Overload, "OVER");
        AddIf(parts, QxStatusBits.ReplaceBattery, "RB");
        AddIf(parts, QxStatusBits.Trim, "TRIM");
        AddIf(parts, QxStatusBits.Boost, "BOOST");
        AddIf(parts, QxStatusBits.Bypass, "BYPASS");
        AddIf(parts, QxStatusBits.Off, "OFF");
        AddIf(parts, QxStatusBits.Calibrating, "CAL");
        AddIf(parts, QxStatusBits.Eco, "ECO");
        AddIf(parts, QxStatusBits.ShutdownImminent, "FSD");
        return string.Join(' ', parts);
    }

    /// <summary>The alarms of this poll plus those NUT derives from the status (ups_alarm_set).</summary>
    public IReadOnlyList<string> EffectiveAlarms()
    {
        var alarms = new List<string>(Alarms);
        if (Has(QxStatusBits.ReplaceBattery))
        {
            alarms.Add("Replace battery!");
        }

        if (Has(QxStatusBits.ShutdownImminent))
        {
            alarms.Add("Shutdown imminent!");
        }

        return alarms;
    }

    private void AddIf(List<string> parts, QxStatusBits bit, string token)
    {
        if (Has(bit))
        {
            parts.Add(token);
        }
    }
}
