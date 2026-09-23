using NutHub.Drivers.Hid.Tests.Support;
using NutHub.Hidraw;
using NutHub.Hidraw.Testing;

namespace NutHub.Drivers.Hid.Tests.Transport;

/// <summary>
/// Enumeration of /dev/hidraw* on a fake system: what the node's ioctls answer, what sysfs adds when it is mounted,
/// and the nodes that cannot be opened or vanish. Every test also checks that the requests matched their buffers and
/// that no descriptor was left open.
/// </summary>
public sealed class HidrawEnumeratorTests : IDisposable
{
    private readonly List<FakeHidrawSystem> _systems = [];

    [Fact]
    public void Identifies_the_eaton_from_its_node_and_sysfs()
    {
        FakeHidrawSystem system = NewSystem();
        system.Plug(0, HidrawFakes.Eaton());

        HidrawNode node = Assert.Single(HidrawEnumerator.List(system));

        Assert.Equal("/dev/hidraw0", node.Path);
        Assert.True(node.IsUsb);
        Assert.Equal(0x0463, node.VendorId);
        Assert.Equal(0xffff, node.ProductId);
        Assert.Equal("EATON Ellipse PRO", node.Name);
        Assert.Equal("usb-0000:00:14.0-1/input0", node.Phys);
        Assert.Equal("G364P10048", node.Uniq);
        Assert.Equal("EATON", node.Manufacturer);
        Assert.Equal("Ellipse PRO", node.Product);
        Assert.Equal("G364P10048", node.Serial);
        Assert.Equal(0x0100, node.ReleaseNumberBcd);
        Assert.Equal(0, node.OpenErrno);
        AssertClean(system);
    }

    [Fact]
    public void The_node_alone_is_enough_without_sysfs()
    {
        FakeHidrawSystem system = NewSystem(sysfs: false);
        system.Plug(0, HidrawFakes.Eaton());

        HidrawNode node = Assert.Single(HidrawEnumerator.List(system));

        Assert.True(node.IsUsb);
        Assert.Equal(0x0463, node.VendorId);
        Assert.Equal(0xffff, node.ProductId);
        Assert.Equal("EATON Ellipse PRO", node.Name);
        Assert.Null(node.Manufacturer);
        Assert.Null(node.Product);
        Assert.Equal("G364P10048", node.Serial);
        Assert.Equal(0, node.ReleaseNumberBcd);
        AssertClean(system);
    }

    [Fact]
    public void Kernels_without_the_uniq_request_take_the_serial_from_sysfs()
    {
        FakeHidrawSystem withSysfs = NewSystem();
        withSysfs.Plug(0, HidrawFakes.Eaton(uniqSupported: false));
        FakeHidrawSystem withoutSysfs = NewSystem(sysfs: false);
        withoutSysfs.Plug(0, HidrawFakes.Eaton(uniqSupported: false));

        HidrawNode node = Assert.Single(HidrawEnumerator.List(withSysfs));
        HidrawNode bare = Assert.Single(HidrawEnumerator.List(withoutSysfs));

        Assert.Equal("G364P10048", node.Uniq);   // HID_UNIQ from the uevent
        Assert.Equal("G364P10048", node.Serial);
        Assert.Null(bare.Uniq);
        Assert.Null(bare.Serial);
        AssertClean(withSysfs);
        AssertClean(withoutSysfs);
    }

    [Fact]
    public void A_node_the_process_may_not_open_is_identified_by_sysfs()
    {
        FakeHidrawSystem system = NewSystem();
        system.Plug(0, HidrawFakes.Eaton(openErrno: Errno.EAcces));

        HidrawNode node = Assert.Single(HidrawEnumerator.List(system));

        Assert.True(node.AccessDenied);
        Assert.Equal(Errno.EAcces, node.OpenErrno);
        Assert.True(node.IsUsb);
        Assert.Equal(0x0463, node.VendorId);
        Assert.Equal(0xffff, node.ProductId);
        Assert.Equal("EATON Ellipse PRO", node.Name);   // HID_NAME
        Assert.Equal("Ellipse PRO", node.Product);
        Assert.Equal("G364P10048", node.Serial);
        AssertClean(system);
    }

    [Fact]
    public void A_node_the_process_may_not_open_is_still_listed_without_sysfs()
    {
        FakeHidrawSystem system = NewSystem(sysfs: false);
        system.Plug(0, HidrawFakes.Eaton(openErrno: Errno.EPerm));

        HidrawNode node = Assert.Single(HidrawEnumerator.List(system));

        Assert.True(node.AccessDenied);
        Assert.Equal(0, node.BusType);
        Assert.Equal(0, node.VendorId);
        Assert.Null(node.Name);
        AssertClean(system);
    }

    [Fact]
    public void Nodes_that_vanish_and_other_entries_are_left_out()
    {
        FakeHidrawSystem system = NewSystem();
        system.Plug(2, HidrawFakes.Eaton());
        system.Plug(10, HidrawFakes.Keyboard());

        // Listed by /dev but gone before the open; and names that are not hidraw nodes.
        system.OtherEntries.AddRange(["/dev/hidraw5", "/dev/hidraw", "/dev/hidrawx1"]);

        IReadOnlyList<HidrawNode> nodes = HidrawEnumerator.List(system);

        Assert.Equal(["/dev/hidraw2", "/dev/hidraw10"], nodes.Select(n => n.Path));
        AssertClean(system);
    }

    [Fact]
    public void Other_buses_are_reported_with_their_bus_type()
    {
        FakeHidrawSystem system = NewSystem();
        system.Plug(0, HidrawFakes.Bluetooth());

        HidrawNode node = Assert.Single(HidrawEnumerator.List(system));

        Assert.False(node.IsUsb);
        Assert.Equal(5, node.BusType);
        Assert.Null(node.Manufacturer);   // no USB device above it in sysfs
        AssertClean(system);
    }

    [Fact]
    public void Sysfs_of_another_device_is_ignored()
    {
        // A node renamed on its way into a container: /dev/hidraw0 is the keyboard, the host's hidraw0 the UPS.
        FakeHidrawSystem system = NewSystem();
        system.Plug(0, HidrawFakes.Eaton());
        system.Plug(0, HidrawFakes.Keyboard(), sysfs: false);

        HidrawNode node = Assert.Single(HidrawEnumerator.List(system));

        Assert.Equal(0x046d, node.VendorId);
        Assert.Equal("Logitech USB Keyboard", node.Name);
        Assert.Null(node.Manufacturer);
        Assert.Null(node.Serial);
        AssertClean(system);
    }

    [Theory]
    [InlineData("0003:00000463:0000FFFF", 3, 0x0463, 0xffff)]
    [InlineData("0005:0000046D:0000B342", 5, 0x046d, 0xb342)]
    public void Parses_hid_ids(string value, int bus, int vendor, int product)
    {
        Assert.True(HidrawSysfs.TryParseHidId(value, out int b, out int v, out int p));
        Assert.Equal((bus, vendor, product), (b, v, p));
        Assert.False(HidrawSysfs.TryParseHidId("0003:0463", out _, out _, out _));
    }

    public void Dispose()
    {
        foreach (FakeHidrawSystem system in _systems)
        {
            system.Dispose();
        }
    }

    private FakeHidrawSystem NewSystem(bool sysfs = true)
    {
        var system = new FakeHidrawSystem(sysfs);
        _systems.Add(system);
        return system;
    }

    private static void AssertClean(FakeHidrawSystem system)
    {
        Assert.Empty(system.Violations);
        Assert.Equal(0, system.OpenDescriptors);
    }
}
