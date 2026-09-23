using System.Globalization;
using System.Text.RegularExpressions;
using NutHub.Core.Model;

namespace NutHub.Drivers.Hid.WinBattery;

/// <summary>Turns what the Windows battery class reports into NUT variables.</summary>
internal static partial class WinBatteryMapper
{
    public static Dictionary<string, string> BuildVariables(BatteryDevice battery, BatteryReading reading)
    {
        ArgumentNullException.ThrowIfNull(battery);
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ups.status"] = Status(reading.PowerState),
            ["driver.version.data"] = "Windows battery class",
        };

        if (Charge(battery, reading.Capacity) is double charge)
        {
            vars["battery.charge"] = NutFormat.Number(charge, 0);
        }

        if (reading.EstimatedSeconds != BatteryReading.Unknown)
        {
            vars["battery.runtime"] = reading.EstimatedSeconds.ToString(CultureInfo.InvariantCulture);
        }

        if (reading.Voltage is not BatteryReading.Unknown and > 0)
        {
            vars["battery.voltage"] = NutFormat.Number(reading.Voltage / 1000.0);
        }

        SetIfPresent(vars, "ups.mfr", battery.Manufacturer);
        SetIfPresent(vars, "ups.model", battery.Name);
        SetIfPresent(vars, "ups.serial", battery.Serial);
        SetIfPresent(vars, "battery.type", battery.Chemistry);
        if (battery.ManufactureDate is DateOnly date)
        {
            vars["battery.mfr.date"] = date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        }

        // HidBatt names its batteries after the HID device: "\\?\hid#vid_051d&pid_0002#...".
        Match ids = UsbIdsRegex().Match(battery.Path);
        if (ids.Success)
        {
            vars["ups.vendorid"] = ids.Groups[1].Value.ToLowerInvariant();
            vars["ups.productid"] = ids.Groups[2].Value.ToLowerInvariant();
        }

        return vars;
    }

    /// <summary>
    /// OL when Windows has line power, OB otherwise; DISCHRG and CHRG from the battery flags; LB when Windows
    /// considers the battery critical (it is about to shut down on its own).
    /// </summary>
    public static string Status(uint powerState)
    {
        var tokens = new List<string>(4) { (powerState & BatteryReading.OnLine) != 0 ? "OL" : "OB" };
        if ((powerState & BatteryReading.Discharging) != 0)
        {
            tokens.Add("DISCHRG");
        }
        else if ((powerState & BatteryReading.Charging) != 0)
        {
            tokens.Add("CHRG");
        }

        if ((powerState & BatteryReading.Critical) != 0)
        {
            tokens.Add("LB");
        }

        return string.Join(' ', tokens);
    }

    /// <summary>The charge in percent: the capacity itself for relative batteries, else its share of the full charge.</summary>
    public static double? Charge(BatteryDevice battery, uint capacity)
    {
        if (capacity == BatteryReading.Unknown)
        {
            return null;
        }

        if (battery.IsRelative)
        {
            // Relative batteries report on a 0..FullChargedCapacity scale, which is 100 on every known UPS.
            double scale = battery.FullChargedCapacity is > 0 and not BatteryReading.Unknown ? battery.FullChargedCapacity : 100;
            return Math.Clamp(Math.Round(capacity * 100.0 / scale), 0, 100);
        }

        if (battery.FullChargedCapacity is 0 or BatteryReading.Unknown)
        {
            return null;
        }

        return Math.Clamp(Math.Round(capacity * 100.0 / battery.FullChargedCapacity), 0, 100);
    }

    private static void SetIfPresent(Dictionary<string, string> vars, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            vars[name] = value.Trim();
        }
    }

    [GeneratedRegex(@"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsbIdsRegex();
}
