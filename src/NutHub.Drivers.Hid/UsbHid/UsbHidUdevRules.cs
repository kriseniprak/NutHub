using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NutHub.Drivers.Hid.UsbHid.Subdrivers;

namespace NutHub.Drivers.Hid.UsbHid;

/// <summary>
/// The udev rules that let a NutHub service running without root open the UPSes the usbhid driver knows, the
/// counterpart of NUT's nut-usbups.rules. They cover the hidraw node (reports) and the USB device node (string
/// descriptors such as the battery chemistry, which hidraw cannot fetch). Packages install the output as
/// /etc/udev/rules.d/99-nuthub-ups.rules; generating it keeps the file in step with the subdriver tables.
/// </summary>
public static partial class UsbHidUdevRules
{
    /// <summary>Vendors whose every product is a power device: one rule per vendor also covers future models.</summary>
    private static readonly int[] UpsOnlyVendors = [0x051d, 0x0463, 0x0764, 0x09ae, 0x10af, 0x0d9f];

    /// <param name="group">The group of the NutHub service account, which receives read and write access.</param>
    public static string Generate(string group = "nuthub")
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!GroupNameRegex().IsMatch(group))
        {
            throw new ArgumentException("The group must be a valid Linux group name.", nameof(group));
        }

        var sb = new StringBuilder();
        sb.Append("# NutHub: access for the NutHub service to USB UPSes (hidraw reports and USB string descriptors).\n");
        sb.Append("# Generated from the usbhid subdriver tables; reload with 'udevadm control --reload-rules && udevadm trigger'.\n");
        sb.Append("ACTION!=\"add|change\", GOTO=\"nuthub_ups_end\"\n\n");
        foreach (int vendor in UpsOnlyVendors)
        {
            AppendRules(sb, vendor, product: null, group);
        }

        var pairs = SubdriverCatalog.Ids
            .Select(SubdriverCatalog.Create)
            .SelectMany(s => s!.SupportedDevices)
            .Select(d => (d.VendorId, d.ProductId))
            .Where(d => !UpsOnlyVendors.Contains(d.VendorId))
            .Distinct()
            .OrderBy(d => d.VendorId)
            .ThenBy(d => d.ProductId);
        foreach (var (vendor, product) in pairs)
        {
            AppendRules(sb, vendor, product, group);
        }

        sb.Append("\nLABEL=\"nuthub_ups_end\"\n");
        return sb.ToString();
    }

    private static void AppendRules(StringBuilder sb, int vendor, int? product, string group)
    {
        string vid = vendor.ToString("x4", CultureInfo.InvariantCulture);
        string pid = product is int p ? $", ATTRS{{idProduct}}==\"{p.ToString("x4", CultureInfo.InvariantCulture)}\"" : "";
        string pidOwn = product is int q ? $", ATTR{{idProduct}}==\"{q.ToString("x4", CultureInfo.InvariantCulture)}\"" : "";
        sb.Append(CultureInfo.InvariantCulture,
                  $"SUBSYSTEM==\"hidraw\", ATTRS{{idVendor}}==\"{vid}\"{pid}, MODE=\"0660\", GROUP=\"{group}\"\n");
        sb.Append(CultureInfo.InvariantCulture,
                  $"SUBSYSTEM==\"usb\", ENV{{DEVTYPE}}==\"usb_device\", ATTR{{idVendor}}==\"{vid}\"{pidOwn}, MODE=\"0660\", GROUP=\"{group}\"\n");
    }

    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$")]
    private static partial Regex GroupNameRegex();
}
