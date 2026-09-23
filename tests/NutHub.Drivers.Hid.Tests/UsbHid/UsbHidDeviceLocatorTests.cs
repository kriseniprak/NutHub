using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Drivers.Hid.Tests.Support;
using NutHub.Drivers.Hid.Transport;
using NutHub.Drivers.Hid.UsbHid;
using NutHub.Drivers.Hid.UsbHid.Subdrivers;
using static NutHub.Drivers.Hid.Tests.Support.DescriptorBuilder;

namespace NutHub.Drivers.Hid.Tests.UsbHid;

public sealed class UsbHidDeviceLocatorTests
{
    private static readonly byte[] Keyboard = new DescriptorBuilder()
        .UsagePage(0x01).Usage(0x06).Collection(Application)
        .UsagePage(0x07).UsageMinimum(0xE0).UsageMaximum(0xE7)
        .LogicalMinimum(0).LogicalMaximum(1).ReportSize(1).ReportCount(8).Input()
        .EndCollection()
        .ToArray();

    /// <summary>A vendor-defined collection, as UPSes expose next to their Power Device one on Windows.</summary>
    private static readonly byte[] VendorCollection = new DescriptorBuilder()
        .UsagePage(0xFF00).Usage(0x01).Collection(Application)
        .ReportId(0x40).Usage(0x02).LogicalMinimum(0).LogicalMaximum(255).ReportSize(8).ReportCount(8).Feature()
        .EndCollection()
        .ToArray();

    [Theory]
    [InlineData(null, null, null, null, null, true)]
    [InlineData(0x1234, null, null, null, null, true)]
    [InlineData(0x1234, 0x5678, "sn0001", null, null, true)] // serial numbers compare without regard to case
    [InlineData(0x051d, null, null, null, null, false)]
    [InlineData(null, 0x0002, null, null, null, false)]
    [InlineData(null, null, "SN0002", null, null, false)]
    [InlineData(null, null, null, "ups 1000", null, true)]
    [InlineData(null, null, null, "Back-UPS", null, false)]
    [InlineData(null, null, null, null, "/dev/hidraw7", true)]
    [InlineData(null, null, null, null, "/dev/hidraw8", false)]
    public void Matching_uses_every_criterion_set(int? vendor, int? product, string? serial, string? name, string? path, bool expected)
    {
        var settings = new UsbHidSettings { VendorId = vendor, ProductId = product, Serial = serial, Product = name, DevicePath = path };

        Assert.Equal(expected, UsbHidDeviceLocator.Matches(SyntheticUps.Info(), settings));
    }

    [Fact]
    public void Picks_the_ups_among_other_hid_devices()
    {
        var keyboard = new FakeHidDevice(new HidDeviceInfo("/dev/hidraw0", 0x046d, 0xc31c, "Logitech", "Keyboard", null), Keyboard);
        var source = new FakeHidDeviceSource(keyboard, SyntheticUps.Create());

        UsbHidLocateResult result = UsbHidDeviceLocator.Locate(source, new UsbHidSettings(), NullLogger.Instance);

        Assert.Equal("/dev/hidraw7", result.Candidate?.Device.Path);
        Assert.Equal("generic", result.Candidate?.Subdriver.Id);
    }

    [Fact]
    public void Prefers_the_power_device_collection_of_a_known_ups()
    {
        // Windows lists one entry per top-level collection; both have the ids of an APC Back-UPS.
        var vendor = new FakeHidDevice(new HidDeviceInfo(@"\\?\hid#vid_051d&pid_0002&col01", 0x051d, 0x0002, "APC", "Back-UPS", "X"), VendorCollection);
        var power = new FakeHidDevice(new HidDeviceInfo(@"\\?\hid#vid_051d&pid_0002&col02", 0x051d, 0x0002, "APC", "Back-UPS", "X"),
                                      SyntheticUps.Descriptor);
        var source = new FakeHidDeviceSource(vendor, power);

        UsbHidLocateResult result = UsbHidDeviceLocator.Locate(source, new UsbHidSettings(), NullLogger.Instance);

        Assert.Equal(power.Info.Path, result.Candidate?.Device.Path);
        Assert.Equal("apc", result.Candidate?.Subdriver.Id);
    }

    [Fact]
    public void A_known_ups_with_only_vendor_collections_is_still_accepted()
    {
        var vendor = new FakeHidDevice(new HidDeviceInfo("/dev/hidraw3", 0x051d, 0x0002, "APC", "Back-UPS", "X"), VendorCollection);

        UsbHidLocateResult result = UsbHidDeviceLocator.Locate(new FakeHidDeviceSource(vendor), new UsbHidSettings(), NullLogger.Instance);

        Assert.Equal("apc", result.Candidate?.Subdriver.Id);
    }

    [Fact]
    public void A_forced_subdriver_does_not_grab_a_keyboard()
    {
        var keyboard = new FakeHidDevice(new HidDeviceInfo("/dev/hidraw0", 0x046d, 0xc31c, "Logitech", "Keyboard", null), Keyboard);

        UsbHidLocateResult result = UsbHidDeviceLocator.Locate(new FakeHidDeviceSource(keyboard), new UsbHidSettings { Subdriver = "apc" },
                                                               NullLogger.Instance);

        Assert.Null(result.Candidate);
        Assert.Contains("No USB UPS was found", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Explains_when_nothing_matches()
    {
        var settings = new UsbHidSettings { VendorId = 0x051d, Serial = "ABC" };

        UsbHidLocateResult result = UsbHidDeviceLocator.Locate(new FakeHidDeviceSource(SyntheticUps.Create()), settings, NullLogger.Instance);

        Assert.Null(result.Candidate);
        Assert.StartsWith("No USB UPS matching vendor id 051d, serial number 'ABC' was found.", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Explains_a_device_that_is_not_a_ups()
    {
        var keyboard = new FakeHidDevice(new HidDeviceInfo("/dev/hidraw0", 0x046d, 0xc31c, "Logitech", "Keyboard", null), Keyboard);

        UsbHidLocateResult result = UsbHidDeviceLocator.Locate(new FakeHidDeviceSource(keyboard), new UsbHidSettings { VendorId = 0x046d },
                                                               NullLogger.Instance);

        Assert.Contains("does not look like a UPS", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Explains_missing_permissions_with_the_platform_remedy()
    {
        FakeHidDevice device = SyntheticUps.Create();
        device.DenyAccess = true;

        UsbHidLocateResult result = UsbHidDeviceLocator.Locate(new FakeHidDeviceSource(device), new UsbHidSettings { VendorId = SyntheticUps.VendorId },
                                                               NullLogger.Instance);

        Assert.Null(result.Candidate);
        string expected = OperatingSystem.IsWindows() ? "'winbattery' driver" : OperatingSystem.IsLinux() ? "99-nuthub-ups.rules" : "denied";
        Assert.Contains(expected, result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_lists_upses_and_merges_collections()
    {
        var keyboard = new FakeHidDevice(new HidDeviceInfo("/dev/hidraw0", 0x046d, 0xc31c, "Logitech", "Keyboard", null), Keyboard);
        var apcVendor = new FakeHidDevice(new HidDeviceInfo("/dev/hidraw1", 0x051d, 0x0002, "American Power Conversion",
                                                            "Back-UPS XS 1400U  FW:926.T1 .I USB FW:T1", "3B1527X15613"), VendorCollection);
        var apcPower = new FakeHidDevice(apcVendor.Info with { Path = "/dev/hidraw2" }, SyntheticUps.Descriptor);
        FakeHidDevice generic = SyntheticUps.Create();
        var source = new FakeHidDeviceSource(keyboard, apcVendor, apcPower, generic);

        IReadOnlyList<DiscoveredDevice> found = UsbHidDeviceLocator.Discover(source, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(2, found.Count);
        DiscoveredDevice apc = found[0];
        Assert.Equal("American Power Conversion Back-UPS XS 1400U (USB 051d:0002)", apc.Title);
        Assert.Equal("Serial number 3B1527X15613 · /dev/hidraw2", apc.Detail);
        Assert.Equal("back-ups-xs-1400u", apc.SuggestedName);
        Assert.Equal("051d", apc.Options["vendorId"]);
        Assert.Equal("0002", apc.Options["productId"]);
        Assert.Equal("3B1527X15613", apc.Options["serial"]);
        Assert.False(apc.Options.ContainsKey("devicePath"));
        Assert.Equal("Acme UPS 1000 (USB 1234:5678)", found[1].Title);
        Assert.Equal("acme-ups-1000", found[1].SuggestedName);
    }

    [Fact]
    public void Discovery_tells_identical_upses_apart_by_path_and_reports_permissions()
    {
        FakeHidDevice first = SyntheticUps.Create("/dev/hidraw4", serial: null);
        FakeHidDevice second = SyntheticUps.Create("/dev/hidraw5", serial: null);
        var locked = new FakeHidDevice(new HidDeviceInfo("/dev/hidraw6", 0x0764, 0x0501, "CPS", "CP1500PFCLCD", "CXX"), []) { DenyAccess = true };
        var source = new FakeHidDeviceSource(first, second, locked);

        IReadOnlyList<DiscoveredDevice> found = UsbHidDeviceLocator.Discover(source, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(["/dev/hidraw4", "/dev/hidraw5"], found.Take(2).Select(d => d.Options["devicePath"]));
        DiscoveredDevice cps = found[2];
        Assert.Equal("CPS CP1500PFCLCD (USB 0764:0501)", cps.Title);
        Assert.Contains("denied", cps.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0x051d, 0x0002, "apc")]
    [InlineData(0x0463, 0xffff, "mge")]
    [InlineData(0x0764, 0x0501, "cps")]
    [InlineData(0x09ae, 0x3016, "tripplite")]
    [InlineData(0x050d, 0x0980, "belkin")]
    [InlineData(0x0d9f, 0x0004, "powercom")]
    public void Subdrivers_claim_their_devices(int vendor, int product, string expected)
    {
        var info = new HidDeviceInfo("/dev/hidraw0", vendor, product, null, null, null);

        Assert.Equal(expected, SubdriverCatalog.Select(info, isPowerDevice: true, "auto", productIdGiven: false)?.Id);
    }

    [Fact]
    public void Unknown_devices_get_the_generic_subdriver_only_when_they_are_power_devices()
    {
        HidDeviceInfo info = SyntheticUps.Info();

        Assert.Equal("generic", SubdriverCatalog.Select(info, isPowerDevice: true, "auto", productIdGiven: false)?.Id);
        Assert.Null(SubdriverCatalog.Select(info, isPowerDevice: false, "auto", productIdGiven: false));
    }
}
