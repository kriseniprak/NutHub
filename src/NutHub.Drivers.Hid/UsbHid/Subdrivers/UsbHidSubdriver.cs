using NutHub.Drivers.Hid.Descriptors;
using NutHub.Drivers.Hid.Transport;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

/// <summary>A USB vendor/product pair a subdriver knows, with the NUT hook that tunes it for that model.</summary>
internal sealed record UsbDeviceId(int VendorId, int ProductId, string? Hook = null);

/// <summary>How well a subdriver's id table knows a device (NUT is_usb_device_supported).</summary>
internal enum UsbSupport
{
    NotSupported,

    /// <summary>The vendor is known but not the product: claimed only when the user named the product id.</summary>
    PossiblySupported,

    Supported,
}

/// <summary>Device behaviours a subdriver may impose (the NUT usbhid-ups globals its hooks set).</summary>
internal sealed class SubdriverQuirks
{
    /// <summary>False for models whose interrupt pipe carries a proprietary protocol (NUT "pollonly").</summary>
    public bool UseInterruptPipe { get; set; } = true;

    /// <summary>Values come only from input reports; the device answers no feature request (NUT interrupt_only).</summary>
    public bool InterruptOnly { get; set; }

    /// <summary>Ask for feature reports with a larger buffer than declared (NUT max_report_size, buggy APC firmwares).</summary>
    public bool LargeReportBuffer { get; set; }

    /// <summary>Delay before reporting LB/RB while calibrating on line power (NUT lbrb_log_delay_sec).</summary>
    public int LowReplaceDelaySeconds { get; set; }
}

/// <summary>
/// The vendor-specific part of the usbhid driver (NUT subdriver_t): which devices it claims, its usage names,
/// its HID to NUT mapping table and conversion quirks, how it formats manufacturer/model/serial and which
/// defects of the report descriptor it repairs. One instance serves one device connection, so conversion state
/// (scaling factors learnt from the device, model type...) lives in instance fields.
/// </summary>
internal abstract class UsbHidSubdriver
{
    private HidUsageTable? _usages;
    private IReadOnlyList<HidMapping>? _mappings;

    /// <summary>The value of the "subdriver" option: "apc", "mge"...</summary>
    public abstract string Id { get; }

    /// <summary>For the option list: "APC".</summary>
    public abstract string DisplayName { get; }

    /// <summary>The NUT subdriver name and version this port follows, published as driver.version.data.</summary>
    public abstract string Version { get; }

    public SubdriverQuirks Quirks { get; } = new();

    /// <summary>Vendor names first, then the standard Power Device and Battery System names.</summary>
    public HidUsageTable Usages => _usages ??= HidUsageTable.Combine(VendorUsages, HidUsageTable.Standard);

    public IReadOnlyList<HidMapping> Mappings => _mappings ??= CreateMappings();

    /// <summary>The ids of the devices this subdriver knows.</summary>
    public abstract IReadOnlyList<UsbDeviceId> SupportedDevices { get; }

    protected virtual HidUsageTable VendorUsages => HidUsageTable.Empty;

    /// <summary>
    /// Whether this subdriver takes the device (NUT claim). <paramref name="productIdGiven"/> is true when the user
    /// set the product id option, which makes NUT accept devices of a known vendor with an unknown product.
    /// </summary>
    public virtual bool Claim(HidDeviceInfo device, bool productIdGiven) => Check(device) switch
    {
        UsbSupport.Supported => true,
        UsbSupport.PossiblySupported => productIdGiven,
        _ => false,
    };

    /// <summary>Called once the device is chosen: applies the hook of its id entry (NUT usb_device_id_t.fun).</summary>
    public void Attach(HidDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        UsbDeviceId? entry = SupportedDevices.FirstOrDefault(d => d.VendorId == device.VendorId && d.ProductId == device.ProductId);
        if (entry?.Hook is { } hook)
        {
            ApplyHook(hook, device);
        }

        OnAttached(device);
    }

    public UsbSupport Check(HidDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        UsbSupport result = UsbSupport.NotSupported;
        foreach (UsbDeviceId id in SupportedDevices)
        {
            if (id.VendorId != device.VendorId)
            {
                continue;
            }

            if (id.ProductId == device.ProductId)
            {
                return UsbSupport.Supported;
            }

            result = UsbSupport.PossiblySupported;
        }

        return result;
    }

    /// <summary>ups.mfr (NUT format_mfr).</summary>
    public virtual string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context) => device.Manufacturer;

    /// <summary>ups.model (NUT format_model).</summary>
    public virtual string? FormatModel(HidDeviceInfo device, IHidConversionContext context) => device.Product;

    /// <summary>ups.serial (NUT format_serial).</summary>
    public virtual string? FormatSerial(HidDeviceInfo device, IHidConversionContext context) => device.Serial;

    /// <summary>Repairs known defects of the descriptor; true when something was changed (NUT fix_report_desc).</summary>
    public virtual bool FixReportDescriptor(HidDeviceInfo device, HidReportDescriptor descriptor) => false;

    protected abstract HidMapping[] CreateMappings();

    /// <summary>Applies a NUT device hook by name (e.g. "disable_interrupt_pipe").</summary>
    protected virtual void ApplyHook(string hook, HidDeviceInfo device)
    {
    }

    protected virtual void OnAttached(HidDeviceInfo device)
    {
    }

    protected static bool Contains(string? text, string value) =>
        text is not null && text.Contains(value, StringComparison.Ordinal);
}
