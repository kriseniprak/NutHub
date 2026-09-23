using System.Globalization;
using NutHub.Core.Configuration;
using NutHub.Core.Model;

namespace NutHub.Services.HostProtection;

/// <summary>
/// What the host protection remembers about one UPS between evaluations. Instants are read from the monotonic clock
/// (time elapsed since the service started), so a change of the system time is neither lost communication nor time
/// on battery.
/// </summary>
internal sealed class UpsTracker
{
    /// <summary>Since when the UPS has been on battery without interruption (as far as the data shows).</summary>
    public TimeSpan? OnBatterySince { get; set; }

    /// <summary>Whether the last fresh data showed the UPS on battery.</summary>
    public bool LastSeenOnBattery { get; set; }

    /// <summary>When fresh data was last seen; null if never.</summary>
    public TimeSpan? LastAvailableAt { get; set; }

    /// <summary>Why the last fresh data made the UPS critical; null when it did not.</summary>
    public string? LastCriticalReason { get; set; }

    /// <summary>Records what the latest snapshot shows.</summary>
    public void Observe(UpsSnapshot? snapshot, TimeSpan now)
    {
        if (snapshot is null || !snapshot.IsAvailable)
        {
            // No fresh data: keep what was last known (upsmon keeps the last status until DEADTIME).
            return;
        }

        LastAvailableAt = now;
        LastSeenOnBattery = snapshot.Has(UpsStatusFlags.OnBattery);
        OnBatterySince = LastSeenOnBattery ? OnBatterySince ?? now : null;
    }
}

/// <summary>
/// Decides whether a UPS is critical, i.e. can no longer be counted on to power the machine. Follows upsmon's
/// is_ups_critical() and recalc() (clients/upsmon.c): FSD, OB+LB (not while calibrating), and a UPS that stopped
/// answering while on battery (DEADTIME); plus the optional charge, runtime and time-on-battery thresholds of NutHub.
/// A UPS that is unreachable while last seen on line power, or never seen at all, is assumed healthy, as upsmon does.
/// </summary>
internal static class CriticalRules
{
    /// <summary>Decides, and records the verdict of fresh data in <paramref name="tracker"/>.</summary>
    /// <param name="now">The monotonic clock, as in <see cref="UpsTracker"/>.</param>
    /// <returns>Why the UPS is critical, or null when it is healthy.</returns>
    public static string? Evaluate(HostProtectionSettings settings, string name, UpsSnapshot? snapshot,
                                   UpsTracker tracker, TimeSpan now)
    {
        bool fresh = snapshot is { IsAvailable: true };
        if (fresh)
        {
            tracker.LastCriticalReason = EvaluateFresh(settings, name, snapshot!, tracker, now);
        }

        // FSD is set by the server itself, so it counts even when the device data is stale.
        if (settings.OnForcedShutdown && snapshot is not null &&
            (snapshot.ForcedShutdown || snapshot.Has(UpsStatusFlags.ForcedShutdown)))
        {
            return $"a forced shutdown (FSD) is set on {name}";
        }

        if (!fresh)
        {
            if (tracker.LastSeenOnBattery && tracker.LastAvailableAt is { } lastSeen)
            {
                TimeSpan silent = now - lastSeen;
                if (silent > TimeSpan.FromSeconds(Math.Max(0, settings.CommunicationLostOnBatterySeconds)))
                {
                    return string.Create(CultureInfo.InvariantCulture,
                        $"communication with {name} was lost while it was on battery ({silent.TotalSeconds:0} s ago)");
                }

                // Within the dead time the last known state stands, as upsmon keeps the last status: a driver
                // restart (a configuration save) must not cancel the grace period and start it again.
                return tracker.LastCriticalReason;
            }

            return null;
        }

        return tracker.LastCriticalReason;
    }

    private static string? EvaluateFresh(HostProtectionSettings settings, string name, UpsSnapshot snapshot,
                                         UpsTracker tracker, TimeSpan now)
    {
        // A runtime calibration drains the battery on purpose; upsmon does not declare it critical either. A real
        // outage shows once it is over (the UPS stays on battery), and lost communication still counts.
        if (!snapshot.Has(UpsStatusFlags.OnBattery) || snapshot.Has(UpsStatusFlags.Calibrating))
        {
            return null;
        }

        if (settings.OnLowBattery && snapshot.Has(UpsStatusFlags.LowBattery))
        {
            return $"{name} is on battery and the battery is low";
        }

        if (settings.BatteryChargeBelow is { } chargeLimit && snapshot.GetNumber("battery.charge") is { } charge &&
            charge <= chargeLimit)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{name} is on battery with {charge:0.#}% charge (limit {chargeLimit:0.#}%)");
        }

        if (settings.RuntimeBelowSeconds is { } runtimeLimit && snapshot.GetNumber("battery.runtime") is { } runtime &&
            runtime <= runtimeLimit)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{name} is on battery with {runtime:0} s of runtime left (limit {runtimeLimit} s)");
        }

        if (settings.OnBatteryLongerThanSeconds is { } longest && tracker.OnBatterySince is { } since &&
            now - since > TimeSpan.FromSeconds(longest))
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{name} has been on battery for {(now - since).TotalSeconds:0} s (limit {longest} s)");
        }

        return null;
    }
}
