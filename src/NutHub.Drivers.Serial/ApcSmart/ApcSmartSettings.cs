using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.ApcSmart;

/// <summary>The validated options of one APC Smart UPS.</summary>
internal sealed record ApcSmartSettings
{
    public required TransportSettings Transport { get; init; }

    /// <summary>How long to wait for a reply (NUT: 3 s).</summary>
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// What shutdown.return without a parameter does, with NUT's sdtype numbering: 0 soft hibernate on battery and hard
    /// hibernate on line power, 1 soft then hard hibernate, 2 instant power-off, 3 delayed power-off, 4 the "CS" trick,
    /// 5 hard hibernate.
    /// </summary>
    public int ShutdownType { get; init; }

    /// <summary>Additional wake-up delay of the hard hibernate command, in 6-minute units, 1 to 3 digits (NUT awd).</summary>
    public string WakeUpDelay { get; init; } = "000";

    /// <summary>Delay between "simulate power failure" and the soft hibernate of the CS trick (NUT cshdelay).</summary>
    public TimeSpan CsDelay { get; init; } = TimeSpan.FromSeconds(3.5);
}
