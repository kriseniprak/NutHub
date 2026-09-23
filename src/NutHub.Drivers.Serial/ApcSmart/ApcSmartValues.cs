using System.Globalization;
using System.Text.RegularExpressions;
using NutHub.Core.Model;
using NutHub.Drivers.Serial.Common;

namespace NutHub.Drivers.Serial.ApcSmart;

/// <summary>An APC variable as found on the connected UPS: present or not, writable or not, its accepted values.</summary>
internal sealed class ApcVariable
{
    public ApcVariable(ApcVariableDef definition)
    {
        Definition = definition;
        Name = definition.Name;
    }

    public ApcVariableDef Definition { get; }

    /// <summary>The NUT name; SPM models report output rather than input frequency through 'F'.</summary>
    public string Name { get; set; }

    public char Command => Definition.Command;

    public bool Is(ApcVarFlags flag) => (Definition.Flags & flag) != 0;

    /// <summary>The UPS supports this variable (APC_PRESENT).</summary>
    public bool Present { get; set; }

    /// <summary>The UPS lets it be changed (APC_RW): strings always, others when the capability string lists them.</summary>
    public bool Writable { get; set; }

    /// <summary>The values the UPS cycles through with '-' (APC_ENUM).</summary>
    public List<string> EnumValues { get; } = [];

    public bool IsEnum => EnumValues.Count > 0;

    /// <summary>How many sub-values a packed variable had last time.</summary>
    public int PackCount { get; set; }
}

/// <summary>Conversion of APC values to NUT values (NUT convert_data and apc_dstate_setinfo).</summary>
internal static partial class ApcSmartValues
{
    /// <summary>The fixed length of the two writable strings (APC_STRLEN).</summary>
    public const int StringLength = 8;

    private const int MaxPackedFields = 4;

    /// <summary>
    /// Converts a raw value: minutes and hours to seconds, the transfer reason to text, and numbers without their
    /// leading zeros ("023.5" to "23.5", "020" to "20") so NUT clients see plain numbers.
    /// </summary>
    public static string Convert(ApcFormat format, string raw) => format switch
    {
        ApcFormat.Hours => (CText.StrToL(raw) * 3600).ToString(CultureInfo.InvariantCulture),
        ApcFormat.Minutes => (CText.StrToL(raw) * 60).ToString(CultureInfo.InvariantCulture),
        ApcFormat.Reason => ApcSmartTables.TransferReason(raw),
        ApcFormat.Percent or ApcFormat.Volt or ApcFormat.Amp or ApcFormat.Celsius or ApcFormat.Dec or ApcFormat.Seconds
            when PlainNumber().IsMatch(raw) && CText.TryStrToD(raw, out double number) => NutFormat.Number(number, 2),
        _ => raw,
    };

    /// <summary>Stores a reply, spreading packed values over ambient.1.*, ambient.2.*...</summary>
    public static void Store(ApcVariable variable, string raw, IDictionary<string, string> values)
    {
        if (!variable.Is(ApcVarFlags.Pack))
        {
            values[variable.Name] = Convert(variable.Definition.Format, raw);
            return;
        }

        string[] parts = raw.Split(',');
        int count = Math.Min(parts.Length, MaxPackedFields);
        for (int i = count; i < variable.PackCount; i++)
        {
            values.Remove(PackedName(variable.Name, i));
        }

        variable.PackCount = count;
        for (int i = 0; i < count; i++)
        {
            values[PackedName(variable.Name, i)] = parts[i].Length == 0 ? "N/A" : Convert(variable.Definition.Format, parts[i]);
        }
    }

    /// <summary>Removes a variable the UPS stopped supporting.</summary>
    public static void Remove(ApcVariable variable, IDictionary<string, string> values)
    {
        if (!variable.Is(ApcVarFlags.Pack))
        {
            values.Remove(variable.Name);
            return;
        }

        for (int i = 0; i < variable.PackCount; i++)
        {
            values.Remove(PackedName(variable.Name, i));
        }

        variable.PackCount = 0;
    }

    /// <summary>"ambient.0.humidity" with index 1 becomes "ambient.2.humidity".</summary>
    internal static string PackedName(string name, int index) =>
        name.Replace(".0.", "." + (index + 1).ToString(CultureInfo.InvariantCulture) + ".", StringComparison.Ordinal);

    public static bool Matches(string? pattern, string value) =>
        pattern is null || Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// ups.status from the status register (NUT ups_status_set). A register of 0 means the UPS is off (it answers but
    /// feeds nothing).
    /// </summary>
    public static string FormatStatus(int status)
    {
        if (status == 0)
        {
            return "OFF";
        }

        var parts = new List<string>(4);
        if ((status & ApcStatusBits.Calibrating) != 0)
        {
            parts.Add("CAL");
        }

        if ((status & ApcStatusBits.Trim) != 0)
        {
            parts.Add("TRIM");
        }

        if ((status & ApcStatusBits.Boost) != 0)
        {
            parts.Add("BOOST");
        }

        if ((status & ApcStatusBits.Online) != 0)
        {
            parts.Add("OL");
        }

        if ((status & ApcStatusBits.OnBattery) != 0)
        {
            parts.Add("OB");
        }

        if ((status & ApcStatusBits.Overload) != 0)
        {
            parts.Add("OVER");
        }

        if ((status & ApcStatusBits.LowBattery) != 0)
        {
            parts.Add("LB");
        }

        if ((status & ApcStatusBits.ReplaceBattery) != 0)
        {
            parts.Add("RB");
        }

        return string.Join(' ', parts);
    }

    /// <summary>Parses the hex status register of 'Q' ("08", "50"); false for anything else.</summary>
    public static bool TryParseStatus(string text, out int status)
    {
        status = 0;
        return StatusRegister().IsMatch(text) &&
               int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out status);
    }

    /// <summary>Applies an alert character to the status register, as NUT's alert_handler does.</summary>
    public static int ApplyAlert(int status, char alert) => alert switch
    {
        '!' => (status & ~ApcStatusBits.Online) | ApcStatusBits.OnBattery,
        '$' => (status & ~ApcStatusBits.OnBattery) | ApcStatusBits.Online,
        '%' => status | ApcStatusBits.LowBattery,
        '+' => status & ~ApcStatusBits.LowBattery,
        '#' => status | ApcStatusBits.ReplaceBattery,
        '?' => status | ApcStatusBits.Overload,
        '=' => status & ~ApcStatusBits.Overload,
        _ => status,
    };

    [GeneratedRegex("^-?[0-9]+(\\.[0-9]+)?$")]
    private static partial Regex PlainNumber();

    [GeneratedRegex("^[0-9A-Fa-f]{1,2}$")]
    private static partial Regex StatusRegister();
}

/// <summary>The bits of the 'Q' status register.</summary>
internal static class ApcStatusBits
{
    public const int Calibrating = 1 << 0;
    public const int Trim = 1 << 1;
    public const int Boost = 1 << 2;
    public const int Online = 1 << 3;
    public const int OnBattery = 1 << 4;
    public const int Overload = 1 << 5;
    public const int LowBattery = 1 << 6;
    public const int ReplaceBattery = 1 << 7;
}
