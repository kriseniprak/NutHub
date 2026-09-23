using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Security;
using NutHub.Web.Api;
using NutHub.Web.Api.Dto;

namespace NutHub.Web.Admin;

/// <summary>
/// Converts between <see cref="UpsConfig"/> and <see cref="UpsConfigDto"/> without ever exposing a secret option:
/// secrets are listed in <c>secretsSet</c>; on writes an absent secret keeps its value, "" clears it, anything else
/// replaces it.
/// </summary>
internal sealed class UpsConfigMapper(IDriverCatalog drivers, ISecretProtector secrets)
{
    public UpsConfigDto ToDto(UpsConfig ups)
    {
        ISet<string> secretKeys = SecretKeys(ups.Driver);
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        var set = new List<string>();
        foreach (var (key, value) in ups.Options.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            // Also hide encrypted values of a driver this build does not know: they can only be secrets.
            if (secretKeys.Contains(key) || secrets.IsProtected(value))
            {
                if (!string.IsNullOrEmpty(value))
                {
                    set.Add(key);
                }
            }
            else
            {
                options[key] = value;
            }
        }

        return new UpsConfigDto(
            ups.Name,
            ups.Description,
            ups.Driver,
            ups.Enabled,
            ups.PollIntervalSeconds,
            options,
            set,
            ups.Overrides.OrderBy(o => o.Key, StringComparer.Ordinal).ToDictionary(o => o.Key, o => (string?)o.Value),
            new LowBatteryDto(ups.LowBattery.ChargePercent, ups.LowBattery.RuntimeSeconds, ups.LowBattery.IgnoreDeviceFlag));
    }

    /// <summary>
    /// Builds the configuration of a UPS from a request body, on top of <paramref name="existing"/> for an update
    /// (members absent from the body keep their value). Option problems are added to <paramref name="errors"/>.
    /// </summary>
    public UpsConfig FromDto(UpsConfigDto dto, UpsConfig? existing, IDictionary<string, string> errors)
    {
        string driver = dto.Driver?.Trim() ?? existing?.Driver ?? "";
        IUpsDriverFactory? factory = drivers.Find(driver);
        if (factory is not null)
        {
            driver = factory.Id;
        }

        var result = new UpsConfig
        {
            Name = dto.Name?.Trim() is { Length: > 0 } name ? name : existing?.Name ?? "",
            Description = dto.Description is null
                ? existing?.Description
                : string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
            Driver = driver,
            Enabled = dto.Enabled ?? existing?.Enabled ?? true,
            PollIntervalSeconds = dto.PollIntervalSeconds ?? existing?.PollIntervalSeconds ?? 2,
            LowBattery = dto.LowBattery is { } lb
                ? new LowBatteryPolicy
                {
                    ChargePercent = lb.ChargePercent,
                    RuntimeSeconds = lb.RuntimeSeconds,
                    IgnoreDeviceFlag = lb.IgnoreDeviceFlag,
                }
                : existing?.LowBattery is { } old
                    ? new LowBatteryPolicy
                    {
                        ChargePercent = old.ChargePercent,
                        RuntimeSeconds = old.RuntimeSeconds,
                        IgnoreDeviceFlag = old.IgnoreDeviceFlag,
                    }
                    : new LowBatteryPolicy(),
        };

        if (string.IsNullOrEmpty(result.Driver))
        {
            errors.TryAdd("driver", "Choose a driver.");
        }

        var keptSecrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MergeOptions(dto, existing, factory, result.Options, keptSecrets);

        if (dto.Overrides is not null)
        {
            foreach (var (key, value) in dto.Overrides)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrEmpty(value))
                {
                    result.Overrides[key.Trim()] = value;
                }
            }
        }
        else if (existing is not null)
        {
            foreach (var (key, value) in existing.Overrides)
            {
                result.Overrides[key] = value;
            }
        }

        if (factory is not null)
        {
            DriverOptionValidator.Validate(factory, result.Options, keptSecrets, errors);
        }

        return result;
    }

    private void MergeOptions(UpsConfigDto dto, UpsConfig? existing, IUpsDriverFactory? factory,
                              Dictionary<string, string> target, ISet<string> keptSecrets)
    {
        ISet<string> secretKeys = SecretKeys(factory);
        if (dto.Options is null)
        {
            // Absent: keep every stored option, secrets of the (possibly new) driver included.
            foreach (var (key, value) in existing?.Options ?? [])
            {
                target[key] = value;
                if (secretKeys.Contains(key) || secrets.IsProtected(value))
                {
                    keptSecrets.Add(key);
                }
            }

            return;
        }

        foreach (var (rawKey, value) in dto.Options)
        {
            string key = CanonicalKey(factory, rawKey.Trim());
            if (key.Length == 0)
            {
                continue;
            }

            if (secretKeys.Contains(key))
            {
                if (value is null)
                {
                    CopyStored(existing, key, target, keptSecrets);
                }
                else if (value.Length > 0)
                {
                    target[key] = value; // Clear text; the configuration store encrypts it.
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                target[key] = value.Trim();
            }
        }

        // Secrets absent from the body keep their stored value.
        foreach (var (key, value) in existing?.Options ?? [])
        {
            bool isSecret = secretKeys.Contains(key) || (factory is null && secrets.IsProtected(value));
            if (isSecret && !dto.Options.Keys.Any(k => string.Equals(k.Trim(), key, StringComparison.OrdinalIgnoreCase)))
            {
                CopyStored(existing, key, target, keptSecrets);
            }
        }
    }

    private static void CopyStored(UpsConfig? existing, string key, Dictionary<string, string> target, ISet<string> kept)
    {
        if (existing is null)
        {
            return;
        }

        foreach (var (k, v) in existing.Options)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v))
            {
                target[key] = v;
                kept.Add(key);
            }
        }
    }

    private static string CanonicalKey(IUpsDriverFactory? factory, string key) =>
        factory?.Options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase))?.Key ?? key;

    private ISet<string> SecretKeys(string driver) => SecretKeys(drivers.Find(driver));

    private static ISet<string> SecretKeys(IUpsDriverFactory? factory) =>
        new HashSet<string>(DriverCatalog.SecretKeys(factory), StringComparer.OrdinalIgnoreCase);
}
