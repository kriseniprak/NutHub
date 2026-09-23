using System.Globalization;
using NutHub.Core.Drivers;

namespace NutHub.Web.Admin;

/// <summary>
/// Checks UPS options against the driver's <see cref="DriverOption"/> list, so the panel can show which field is
/// wrong before the driver even starts. Errors are keyed "options.&lt;key&gt;".
/// </summary>
internal static class DriverOptionValidator
{
    public static void Validate(IUpsDriverFactory factory, IReadOnlyDictionary<string, string> options,
                                ISet<string> keptSecrets, IDictionary<string, string> errors)
    {
        foreach (DriverOption option in factory.Options)
        {
            string field = "options." + option.Key;
            string? value = Get(options, option.Key);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (option.Required && IsVisible(factory, option, options))
                {
                    errors.TryAdd(field, $"{option.Label} is required.");
                }

                continue;
            }

            if (keptSecrets.Contains(option.Key))
            {
                continue; // Stored encrypted, validated when it was set.
            }

            string? problem = Check(option, value.Trim());
            if (problem is not null)
            {
                errors.TryAdd(field, problem);
            }
        }

        if (errors.Count > 0)
        {
            return; // The fields above are wrong already; the driver would only repeat it.
        }

        // What the option list cannot express: the driver reads the values itself, so "0463:ffff" in a USB id, or a
        // combination it refuses, is caught here instead of leaving behind a UPS that fails as soon as it starts.
        try
        {
            factory.ValidateOptions(new DriverOptionReader(options));
        }
        catch (DriverConfigurationException ex)
        {
            if (ex.OptionKey is { } secret && keptSecrets.Contains(secret))
            {
                return; // Stored encrypted: the reader sees the protected text, not the value that was checked.
            }

            errors.TryAdd(ex.OptionKey is { } key ? "options." + key : "driver", ex.Message);
        }
    }

    /// <summary>Whether the panel shows an option given the other values (hidden options are not required).</summary>
    public static bool IsVisible(IUpsDriverFactory factory, DriverOption option, IReadOnlyDictionary<string, string> options)
    {
        if (option.VisibleWhen is not { } condition)
        {
            return true;
        }

        string? other = Get(options, condition.Key);
        if (string.IsNullOrWhiteSpace(other))
        {
            other = factory.Options.FirstOrDefault(o => string.Equals(o.Key, condition.Key, StringComparison.OrdinalIgnoreCase))
                ?.Default;
        }

        return other is not null && condition.Values.Any(v => string.Equals(v, other.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string? Check(DriverOption option, string value)
    {
        switch (option.Type)
        {
            case DriverOptionType.Integer:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
                {
                    return $"{option.Label} must be a whole number.";
                }

                return CheckRange(option, whole);
            case DriverOptionType.Decimal:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
                    !double.IsFinite(number))
                {
                    return $"{option.Label} must be a number.";
                }

                return CheckRange(option, number);
            case DriverOptionType.Port:
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) &&
                       port is >= 1 and <= 65535
                    ? null
                    : $"{option.Label} must be a port number between 1 and 65535.";
            case DriverOptionType.Boolean:
                return value.ToLowerInvariant() is "true" or "false" or "yes" or "no" or "on" or "off" or "1" or "0"
                    ? null
                    : $"{option.Label} must be true or false.";
            case DriverOptionType.Choice when option.Choices is { Count: > 0 } choices:
                return choices.Any(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase))
                    ? null
                    : $"{option.Label} must be one of: {string.Join(", ", choices.Select(c => c.Value))}.";
            case DriverOptionType.Host:
                return value.Length <= 253 && !value.Any(char.IsWhiteSpace) ? null : $"{option.Label} is not a valid host.";
            default:
                return value.Length <= 1024 ? null : $"{option.Label} is too long.";
        }
    }

    private static string? CheckRange(DriverOption option, double value)
    {
        if ((option.Min is { } min && value < min) || (option.Max is { } max && value > max))
        {
            string lo = option.Min?.ToString(CultureInfo.InvariantCulture) ?? "-∞";
            string hi = option.Max?.ToString(CultureInfo.InvariantCulture) ?? "∞";
            return $"{option.Label} must be between {lo} and {hi}.";
        }

        return null;
    }

    private static string? Get(IReadOnlyDictionary<string, string> options, string key)
    {
        foreach (var (k, v) in options)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }
}
