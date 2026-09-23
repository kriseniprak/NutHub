namespace NutHub.Core.Model;

/// <summary>
/// The standard tokens of the NUT "ups.status" variable, as flags.
/// </summary>
[Flags]
public enum UpsStatusFlags : long
{
    None = 0,

    /// <summary>OL: on line (mains) power.</summary>
    Online = 1L << 0,

    /// <summary>OB: on battery.</summary>
    OnBattery = 1L << 1,

    /// <summary>LB: low battery.</summary>
    LowBattery = 1L << 2,

    /// <summary>HB: high battery.</summary>
    HighBattery = 1L << 3,

    /// <summary>RB: the battery needs to be replaced.</summary>
    ReplaceBattery = 1L << 4,

    /// <summary>CHRG: the battery is charging.</summary>
    Charging = 1L << 5,

    /// <summary>DISCHRG: the battery is discharging.</summary>
    Discharging = 1L << 6,

    /// <summary>BYPASS: UPS bypass circuit is active, no battery protection is available.</summary>
    Bypass = 1L << 7,

    /// <summary>CAL: the UPS is calibrating its runtime.</summary>
    Calibrating = 1L << 8,

    /// <summary>OFF: the UPS is offline and is not supplying power to the load.</summary>
    Off = 1L << 9,

    /// <summary>OVER: the UPS is overloaded.</summary>
    Overload = 1L << 10,

    /// <summary>TRIM: the UPS is trimming incoming voltage.</summary>
    Trim = 1L << 11,

    /// <summary>BOOST: the UPS is boosting incoming voltage.</summary>
    Boost = 1L << 12,

    /// <summary>FSD: forced shutdown, set by the server when a primary client (or an operator) asks for it.</summary>
    ForcedShutdown = 1L << 13,

    /// <summary>ALARM: the UPS has an active alarm, see "ups.alarm".</summary>
    Alarm = 1L << 14,

    /// <summary>TEST: the UPS is running a self test.</summary>
    Test = 1L << 15,

    /// <summary>ECO: the UPS is in ECO / high-efficiency mode.</summary>
    Eco = 1L << 16,
}

/// <summary>
/// Parsing and formatting of "ups.status".
/// </summary>
public static class UpsStatus
{
    private static readonly (string Token, UpsStatusFlags Flag)[] Tokens =
    [
        ("FSD", UpsStatusFlags.ForcedShutdown),
        ("OL", UpsStatusFlags.Online),
        ("OB", UpsStatusFlags.OnBattery),
        ("LB", UpsStatusFlags.LowBattery),
        ("HB", UpsStatusFlags.HighBattery),
        ("RB", UpsStatusFlags.ReplaceBattery),
        ("CHRG", UpsStatusFlags.Charging),
        ("DISCHRG", UpsStatusFlags.Discharging),
        ("BYPASS", UpsStatusFlags.Bypass),
        ("CAL", UpsStatusFlags.Calibrating),
        ("OFF", UpsStatusFlags.Off),
        ("OVER", UpsStatusFlags.Overload),
        ("TRIM", UpsStatusFlags.Trim),
        ("BOOST", UpsStatusFlags.Boost),
        ("ALARM", UpsStatusFlags.Alarm),
        ("TEST", UpsStatusFlags.Test),
        ("ECO", UpsStatusFlags.Eco),
    ];

    /// <summary>Splits a status string into its tokens (upper case, no duplicates, original order).</summary>
    public static IReadOnlyList<string> SplitTokens(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return [];
        }

        var result = new List<string>();
        foreach (string part in status.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string token = part.ToUpperInvariant();
            if (!result.Contains(token))
            {
                result.Add(token);
            }
        }

        return result;
    }

    /// <summary>The known flags in a status string; unknown tokens are ignored.</summary>
    public static UpsStatusFlags Parse(string? status)
    {
        UpsStatusFlags flags = UpsStatusFlags.None;
        foreach (string token in SplitTokens(status))
        {
            flags |= FromToken(token);
        }

        return flags;
    }

    public static UpsStatusFlags FromToken(string token)
    {
        foreach (var (t, flag) in Tokens)
        {
            if (string.Equals(t, token, StringComparison.OrdinalIgnoreCase))
            {
                return flag;
            }
        }

        return UpsStatusFlags.None;
    }

    public static string? ToToken(UpsStatusFlags flag)
    {
        foreach (var (t, f) in Tokens)
        {
            if (f == flag)
            {
                return t;
            }
        }

        return null;
    }

    /// <summary>Formats flags as a status string in the canonical order (FSD first, as upsd does).</summary>
    public static string Format(UpsStatusFlags flags)
    {
        var parts = new List<string>();
        foreach (var (token, flag) in Tokens)
        {
            if ((flags & flag) != 0)
            {
                parts.Add(token);
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Adds and removes tokens from a status string, keeping any unknown tokens a driver reported.
    /// </summary>
    public static string Combine(string? status, UpsStatusFlags add, UpsStatusFlags remove = UpsStatusFlags.None)
    {
        var tokens = SplitTokens(status).ToList();
        foreach (var (token, flag) in Tokens)
        {
            if ((remove & flag) != 0)
            {
                tokens.Remove(token);
            }
        }

        // FSD always leads, like upsd prints it; the other added tokens go at the end.
        if ((add & UpsStatusFlags.ForcedShutdown) != 0 && !tokens.Contains("FSD"))
        {
            tokens.Insert(0, "FSD");
        }

        foreach (var (token, flag) in Tokens)
        {
            if (flag != UpsStatusFlags.ForcedShutdown && (add & flag) != 0 && !tokens.Contains(token))
            {
                tokens.Add(token);
            }
        }

        return string.Join(' ', tokens);
    }

    /// <summary>
    /// True when a client should shut down now: forced shutdown, or on battery with a low battery.
    /// </summary>
    public static bool IsCritical(UpsStatusFlags flags) =>
        (flags & UpsStatusFlags.ForcedShutdown) != 0 ||
        ((flags & UpsStatusFlags.OnBattery) != 0 && (flags & UpsStatusFlags.LowBattery) != 0);
}
