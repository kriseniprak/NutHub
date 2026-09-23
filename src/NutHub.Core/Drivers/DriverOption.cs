using System.Globalization;

namespace NutHub.Core.Drivers;

/// <summary>The input type of a driver option, which decides the control the web panel shows.</summary>
public enum DriverOptionType
{
    /// <summary>Single-line text.</summary>
    String,

    /// <summary>Whole number, optionally bounded by <see cref="DriverOption.Min"/> / <see cref="DriverOption.Max"/>.</summary>
    Integer,

    /// <summary>Decimal number.</summary>
    Decimal,

    /// <summary>A switch; stored as "true" / "false".</summary>
    Boolean,

    /// <summary>One of <see cref="DriverOption.Choices"/>.</summary>
    Choice,

    /// <summary>Secret text: encrypted in the configuration file, never sent back to the browser.</summary>
    Secret,

    /// <summary>Host name or IP address.</summary>
    Host,

    /// <summary>TCP/UDP port 1-65535.</summary>
    Port,

    /// <summary>Serial device: "COM3", "/dev/ttyUSB0". The panel offers the ports it finds.</summary>
    SerialPort,

    /// <summary>A path on the NutHub machine.</summary>
    FilePath,
}

/// <summary>A value of a <see cref="DriverOptionType.Choice"/> option.</summary>
public sealed record DriverOptionChoice(string Value, string Label);

/// <summary>
/// One configuration option of a driver. Keys are camelCase ("port", "baudRate", "community") and are stored in
/// <c>UpsConfig.Options</c> as strings.
/// </summary>
public sealed record DriverOption
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public DriverOptionType Type { get; init; } = DriverOptionType.String;

    public bool Required { get; init; }

    /// <summary>The value used when the option is not set; shown as placeholder.</summary>
    public string? Default { get; init; }

    /// <summary>Help text under the field.</summary>
    public string? Help { get; init; }

    public IReadOnlyList<DriverOptionChoice>? Choices { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    /// <summary>Hidden under "Advanced" in the panel.</summary>
    public bool Advanced { get; init; }

    /// <summary>
    /// Show this option only when another option has one of the given values, e.g. SNMPv3 fields only when
    /// "version" is "3": <c>VisibleWhen = ("version", ["3"])</c>.
    /// </summary>
    public DriverOptionCondition? VisibleWhen { get; init; }

    public bool IsSecret => Type == DriverOptionType.Secret;
}

/// <summary>Visibility condition of a driver option.</summary>
public sealed record DriverOptionCondition(string Key, IReadOnlyList<string> Values);

/// <summary>
/// Typed, validated reads of driver options. Every failure throws <see cref="DriverConfigurationException"/> naming
/// the option, so the web panel can show which field is wrong.
/// </summary>
public readonly struct DriverOptionReader(IReadOnlyDictionary<string, string> options)
{
    public string? GetString(string key, string? defaultValue = null)
    {
        foreach (var (k, v) in options)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(v) ? defaultValue : v.Trim();
            }
        }

        return defaultValue;
    }

    public string GetRequiredString(string key) =>
        GetString(key) ?? throw new DriverConfigurationException($"The option '{key}' is required.", key);

    public int GetInt(string key, int defaultValue, int min = int.MinValue, int max = int.MaxValue)
    {
        string? text = GetString(key);
        if (text is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new DriverConfigurationException($"The option '{key}' must be a whole number.", key);
        }

        if (value < min || value > max)
        {
            throw new DriverConfigurationException($"The option '{key}' must be between {min} and {max}.", key);
        }

        return value;
    }

    public double GetDouble(string key, double defaultValue, double min = double.MinValue, double max = double.MaxValue)
    {
        string? text = GetString(key);
        if (text is null)
        {
            return defaultValue;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new DriverConfigurationException($"The option '{key}' must be a number.", key);
        }

        if (value < min || value > max)
        {
            throw new DriverConfigurationException(
                $"The option '{key}' must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.",
                key);
        }

        return value;
    }

    public bool GetBool(string key, bool defaultValue)
    {
        string? text = GetString(key);
        if (text is null)
        {
            return defaultValue;
        }

        return text.ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw new DriverConfigurationException($"The option '{key}' must be true or false.", key),
        };
    }

    /// <summary>Reads a choice option, checking it against the allowed values (case-insensitive).</summary>
    public string GetChoice(string key, string defaultValue, params string[] allowed)
    {
        string value = GetString(key, defaultValue)!;
        foreach (string a in allowed)
        {
            if (string.Equals(a, value, StringComparison.OrdinalIgnoreCase))
            {
                return a;
            }
        }

        throw new DriverConfigurationException(
            $"The option '{key}' must be one of: {string.Join(", ", allowed)}.", key);
    }

    public int GetPort(string key, int defaultValue) => GetInt(key, defaultValue, 1, 65535);

    /// <summary>Reads a hexadecimal number such as a USB vendor id ("051d", "0x051D").</summary>
    public int? GetHex(string key)
    {
        string? text = GetString(key);
        if (text is null)
        {
            return null;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value) || value < 0)
        {
            throw new DriverConfigurationException($"The option '{key}' must be a hexadecimal number.", key);
        }

        return value;
    }
}
