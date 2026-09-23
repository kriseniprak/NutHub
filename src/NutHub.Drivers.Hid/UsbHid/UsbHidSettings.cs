namespace NutHub.Drivers.Hid.UsbHid;

/// <summary>The validated options of one usbhid UPS.</summary>
internal sealed record UsbHidSettings
{
    public int? VendorId { get; init; }

    public int? ProductId { get; init; }

    public string? Serial { get; init; }

    /// <summary>Case-insensitive substring of the USB product string.</summary>
    public string? Product { get; init; }

    /// <summary>Exact platform device path, for machines with identical UPSes without serial numbers.</summary>
    public string? DevicePath { get; init; }

    /// <summary>
    /// Re-enumerate the USB device when it stops answering (Linux only). Kept on: a UPS that hangs while it stays
    /// plugged in is otherwise only recovered by unplugging it.
    /// </summary>
    public bool UsbReset { get; init; } = true;

    /// <summary>"auto" or a subdriver id.</summary>
    public string Subdriver { get; init; } = "auto";

    /// <summary>
    /// "On line and discharging" means on battery (CyberPower UT series and others; NUT onlinedischarge /
    /// onlinedischarge_onbattery).
    /// </summary>
    public bool OnlineDischargeOnBattery { get; init; }

    /// <summary>"On line and discharging" means calibrating (some APC models; NUT onlinedischarge_calibration).</summary>
    public bool OnlineDischargeCalibration { get; init; }

    /// <summary>Initial ups.delay.start, seconds (NUT ondelay); null keeps the subdriver default.</summary>
    public int? OnDelay { get; init; }

    /// <summary>Initial ups.delay.shutdown, seconds (NUT offdelay); null keeps the subdriver default.</summary>
    public int? OffDelay { get; init; }

    /// <summary>Whether any option narrows the device choice down.</summary>
    public bool HasDeviceCriteria =>
        VendorId is not null || ProductId is not null || Serial is not null || Product is not null || DevicePath is not null;
}
