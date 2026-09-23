using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Drivers.Hid.Tests.Support;
using NutHub.Drivers.Hid.Transport;
using NutHub.Drivers.Hid.UsbHid;
using NutHub.Hidraw;
using NutHub.Hidraw.Testing;
using static NutHub.Drivers.Hid.Tests.Support.DescriptorBuilder;

namespace NutHub.Drivers.Hid.Tests.Transport;

/// <summary>
/// The hidraw device source and its connections on a fake system: discovery of the Eaton that HidSharp could not see
/// in a container, the permission cases, the buffer conventions shared with the HidSharp connection, and how errno
/// values become the exceptions the driver acts on.
/// </summary>
public sealed class HidrawDeviceSourceTests : IDisposable
{
    private readonly FakeHidrawSystem _system = new();

    [Fact]
    public void Discovery_lists_the_eaton_and_nothing_else()
    {
        _system.Plug(0, HidrawFakes.Eaton());
        _system.Plug(1, HidrawFakes.Keyboard());
        _system.Plug(2, HidrawFakes.Bluetooth());
        HidrawDeviceSource source = HidrawFakes.Source(_system);

        DiscoveredDevice found = Assert.Single(UsbHidDeviceLocator.Discover(source, NullLogger.Instance, CancellationToken.None));

        Assert.Equal("EATON Ellipse PRO (USB 0463:ffff)", found.Title);
        Assert.Equal("Serial number G364P10048 · /dev/hidraw0", found.Detail);
        Assert.Equal("0463", found.Options["vendorId"]);
        Assert.Equal("ffff", found.Options["productId"]);
        Assert.Equal("G364P10048", found.Options["serial"]);
        Assert.False(found.Options.ContainsKey("devicePath"));
        Assert.DoesNotContain(source.GetDevices(), d => d.Path == "/dev/hidraw2");
        Assert.Empty(_system.Violations);
        Assert.Equal(0, _system.OpenDescriptors);
    }

    [Fact]
    public void Discovery_works_without_sysfs()
    {
        using var bare = new FakeHidrawSystem(sysfs: false);
        bare.Plug(0, HidrawFakes.Eaton());

        DiscoveredDevice found = Assert.Single(UsbHidDeviceLocator.Discover(HidrawFakes.Source(bare), NullLogger.Instance, CancellationToken.None));

        Assert.Equal("EATON Ellipse PRO (USB 0463:ffff)", found.Title);
        Assert.Equal("G364P10048", found.Options["serial"]);
    }

    [Fact]
    public void A_node_without_permission_reads_its_descriptor_from_sysfs_and_refuses_to_open()
    {
        _system.Plug(0, HidrawFakes.Eaton(openErrno: Errno.EAcces));
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        HidDeviceInfo info = HidrawFakes.Info(source, "/dev/hidraw0");

        byte[] descriptor = source.GetReportDescriptor(info);
        var denied = Assert.Throws<UnauthorizedAccessException>(() => source.Open(info));
        UsbHidLocateResult located = UsbHidDeviceLocator.Locate(source, new UsbHidSettings(), NullLogger.Instance);
        DiscoveredDevice found = Assert.Single(UsbHidDeviceLocator.Discover(source, NullLogger.Instance, CancellationToken.None));

        // Discovery shows the UPS with what is wrong, so the panel can say "permission denied".
        Assert.Equal("EATON Ellipse PRO (USB 0463:ffff)", found.Title);
        Assert.Contains("open() of /dev/hidraw0 returned EACCES", found.Detail, StringComparison.Ordinal);
        Assert.Equal("0463", found.Options["vendorId"]);
        Assert.Equal(RealDeviceDump.Load(RealDeviceDump.Eaton5Sc750).Descriptor, descriptor);
        Assert.Contains("EACCES", denied.Message, StringComparison.Ordinal);
        Assert.NotNull(located.Candidate);   // the driver then reports the permission problem when it opens it
        if (OperatingSystem.IsLinux())
        {
            Assert.StartsWith("Permission denied on /dev/hidraw0 (USB 0463:ffff)", UsbHidErrors.AccessDenied(info, denied), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_node_nothing_can_identify_is_listed_with_the_permission_problem()
    {
        using var bare = new FakeHidrawSystem(sysfs: false);
        bare.Plug(0, HidrawFakes.Eaton(openErrno: Errno.EAcces));
        HidrawDeviceSource source = HidrawFakes.Source(bare);

        DiscoveredDevice found = Assert.Single(UsbHidDeviceLocator.Discover(source, NullLogger.Instance, CancellationToken.None));

        Assert.Equal("Unidentified HID device (/dev/hidraw0)", found.Title);
        Assert.Contains("/dev/hidraw0", found.Detail, StringComparison.Ordinal);
        Assert.Contains("EACCES", found.Detail, StringComparison.Ordinal);
        Assert.Equal("/dev/hidraw0", found.Options["devicePath"]);
        Assert.False(found.Options.ContainsKey("vendorId"));
    }

    [Fact]
    public void The_descriptor_falls_back_to_sysfs_when_the_ioctl_fails()
    {
        _system.Plug(0, HidrawFakes.Eaton(descriptorIoctlFails: true));
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        HidDeviceInfo info = HidrawFakes.Info(source, "/dev/hidraw0");

        using IHidConnection connection = source.Open(info);

        Assert.Equal(RealDeviceDump.Load(RealDeviceDump.Eaton5Sc750).Descriptor, source.GetReportDescriptor(info));
        Assert.Equal(source.GetReportDescriptor(info), connection.GetReportDescriptor());
    }

    [Fact]
    public void A_renumbered_ups_is_found_again_by_its_ids_and_serial()
    {
        FakeHidrawDevice ups = _system.Plug(0, HidrawFakes.Eaton());
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        var settings = new UsbHidSettings { VendorId = 0x0463, ProductId = 0xffff, Serial = HidrawFakes.EatonSerial };
        Assert.Equal("/dev/hidraw0", UsbHidDeviceLocator.Locate(source, settings, NullLogger.Instance).Candidate?.Device.Path);

        _system.Unplug(ups);
        _system.Plug(0, HidrawFakes.Keyboard());
        _system.Plug(3, HidrawFakes.Eaton());

        Assert.Equal("/dev/hidraw3", UsbHidDeviceLocator.Locate(source, settings, NullLogger.Instance).Candidate?.Device.Path);
    }

    [Fact]
    public void Feature_reports_keep_the_report_id_and_the_requested_length()
    {
        FakeHidrawDevice ups = _system.Plug(0, HidrawFakes.Eaton());
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        using IHidConnection connection = source.Open(HidrawFakes.Info(source, "/dev/hidraw0"));

        byte[] report = connection.GetFeature(0x06, 6);
        byte[] shortest = connection.GetFeature(0x08, 1);
        connection.SetFeature([0x08, 0x1e]);

        Assert.Equal(new byte[] { 0x06, 0x64, 0xec, 0x06, 0x00, 0x00 }, report);
        Assert.Equal(new byte[] { 0x08, 0x14 }, shortest);   // at least 2 bytes, as with HidSharp
        Assert.Equal(new byte[] { 0x08, 0x1e }, Assert.Single(ups.FeatureWrites));
        Assert.Contains(_system.Ioctls, i => i == (HidrawIoctl.Current.GetFeature(6), 6));
        Assert.Contains(_system.Ioctls, i => i == (HidrawIoctl.Current.SetFeature(2), 2));
        Assert.Empty(_system.Violations);
    }

    [Fact]
    public void Input_reports_start_with_the_report_id()
    {
        FakeHidrawDevice ups = _system.Plug(0, HidrawFakes.Eaton());
        FakeHidrawDevice plain = _system.Plug(1, HidrawFakes.WithoutReportIds());
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        using IHidConnection numbered = source.Open(HidrawFakes.Info(source, "/dev/hidraw0"));
        using IHidConnection unnumbered = source.Open(HidrawFakes.Info(source, "/dev/hidraw1"));
        var buffer = new byte[64];

        _system.QueueInput(ups, 0x06, 0x63, 0xe0, 0x06, 0x00, 0x00);
        int numberedLength = numbered.ReadInput(buffer, TimeSpan.FromSeconds(5));
        byte[] numberedReport = buffer[..numberedLength];
        _system.QueueInput(plain, 42);
        int unnumberedLength = unnumbered.ReadInput(buffer, TimeSpan.FromSeconds(5));

        Assert.Equal(new byte[] { 0x06, 0x63, 0xe0, 0x06, 0x00, 0x00 }, numberedReport);
        Assert.Equal(new byte[] { 0x00, 42 }, buffer[..unnumberedLength]);
        Assert.Equal(2, unnumbered.MaxInputReportLength);
        Assert.True(numbered.MaxInputReportLength > 1);
    }

    [Fact]
    public void Input_reports_that_carry_no_id_get_one_even_when_feature_reports_are_numbered()
    {
        // The kernel numbers input reports on their own (report_enum.numbered per report type): a device whose input
        // items come before the first Report ID sends them without an id byte, whatever its feature reports do.
        byte[] descriptor = new DescriptorBuilder()
            .UsagePage(0x84).Usage("UPS").Collection(Application)
            .Collection("PowerSummary", Physical)
            .Value("RemainingCapacity", 8, 0, 100, input: true)
            .ReportId(7)
            .Value("RemainingCapacity", 8, 0, 100)
            .EndCollection()
            .EndCollection()
            .ToArray();
        FakeHidrawDevice ups = _system.Plug(0, new FakeHidrawDevice
        {
            VendorId = SyntheticUps.VendorId,
            ProductId = SyntheticUps.ProductId,
            Name = "Acme UPS",
            Descriptor = descriptor,
        });
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        using IHidConnection connection = source.Open(HidrawFakes.Info(source, "/dev/hidraw0"));
        var buffer = new byte[64];

        _system.QueueInput(ups, 77);
        int length = connection.ReadInput(buffer, TimeSpan.FromSeconds(5));

        Assert.Equal(new byte[] { 0x00, 77 }, buffer[..length]);
        Assert.Equal(2, connection.MaxInputReportLength);
    }

    [Fact]
    public void Reading_input_times_out_with_nothing()
    {
        _system.Plug(0, HidrawFakes.Eaton());
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        using IHidConnection connection = source.Open(HidrawFakes.Info(source, "/dev/hidraw0"));

        Assert.Equal(0, connection.ReadInput(new byte[64], TimeSpan.FromMilliseconds(30)));
    }

    [Theory]
    [InlineData(Errno.EPipe)]
    [InlineData(Errno.ETimedOut)]
    [InlineData(Errno.EProto)]
    public void A_refused_report_is_an_io_error_while_the_device_is_present(int errno)
    {
        FakeHidrawDevice ups = _system.Plug(0, HidrawFakes.Eaton());
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        using IHidConnection connection = source.Open(HidrawFakes.Info(source, "/dev/hidraw0"));
        ups.FeatureErrors[0x06] = errno;

        var error = Assert.Throws<IOException>(() => connection.GetFeature(0x06, 6));

        Assert.IsNotType<HidDeviceLostException>(error);
        Assert.Contains(Errno.Name(errno), error.Message, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x00, 0x01, 0x00, 0x01 }, connection.GetFeature(0x01, 6));
    }

    [Fact]
    public void Enodev_from_a_report_request_means_unplugged()
    {
        FakeHidrawDevice ups = _system.Plug(0, HidrawFakes.Eaton());
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        using IHidConnection connection = source.Open(HidrawFakes.Info(source, "/dev/hidraw0"));
        ups.FeatureErrors[0x06] = Errno.ENoDev;

        Assert.Throws<HidDeviceLostException>(() => connection.GetFeature(0x06, 6));
        Assert.Throws<HidDeviceLostException>(() => connection.SetFeature([0x06, 0x00, 0x00, 0x00, 0x00, 0x00]));
    }

    [Theory]
    [InlineData(Errno.EIo)]
    [InlineData(Errno.ENoEnt)]
    [InlineData(Errno.ENxIo)]
    [InlineData(108)]   // ESHUTDOWN
    public void Transfer_errors_while_the_node_still_answers_are_not_an_unplug(int errno)
    {
        // Some host controllers report a failed control transfer as EIO; hidraw itself says ENODEV once the device is
        // gone, so a reconnection on EIO would only cost a reconnection loop with a firmware that fails one report.
        FakeHidrawDevice ups = _system.Plug(0, HidrawFakes.Eaton());
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        using IHidConnection connection = source.Open(HidrawFakes.Info(source, "/dev/hidraw0"));
        ups.FeatureErrors[0x06] = errno;

        var read = Assert.Throws<IOException>(() => connection.GetFeature(0x06, 6));
        var write = Assert.Throws<IOException>(() => connection.SetFeature([0x06, 0x00, 0x00, 0x00, 0x00, 0x00]));

        Assert.IsNotType<HidDeviceLostException>(read);
        Assert.IsNotType<HidDeviceLostException>(write);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x00, 0x01, 0x00, 0x01 }, connection.GetFeature(0x01, 6));
        _system.Unplug(ups);
        Assert.Throws<HidDeviceLostException>(() => connection.GetFeature(0x06, 6));
    }

    [Theory]
    [InlineData(new byte[] { 0x06 })]   // the report id alone
    [InlineData(new byte[] { })]        // an empty data stage
    public void A_feature_answer_without_data_is_an_error_and_not_a_report_of_zeros(byte[] answer)
    {
        FakeHidrawDevice ups = _system.Plug(0, HidrawFakes.Eaton());
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        using IHidConnection connection = source.Open(HidrawFakes.Info(source, "/dev/hidraw0"));
        ups.Features[0x06] = answer;

        var error = Assert.Throws<IOException>(() => connection.GetFeature(0x06, 6));

        Assert.IsNotType<HidDeviceLostException>(error);
        Assert.Contains("no data", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sysfs_of_another_device_gives_neither_strings_nor_descriptor()
    {
        // /dev/hidraw0 in the container is the UPS, the host's hidraw0 (whose sysfs entry the container sees) a keyboard.
        _system.Plug(0, HidrawFakes.Keyboard());
        _system.Plug(0, HidrawFakes.Eaton(), sysfs: false);
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        HidDeviceInfo info = HidrawFakes.Info(source, "/dev/hidraw0");

        using (IHidConnection connection = source.Open(info))
        {
            Assert.Null(connection.GetIndexedString(4));
        }

        // Without the descriptor ioctl, the keyboard's descriptor must not stand in for the UPS's.
        _system.Plug(0, HidrawFakes.Eaton(descriptorIoctlFails: true), sysfs: false);
        var error = Assert.Throws<IOException>(() => source.Open(info));
        Assert.IsNotType<HidDeviceLostException>(error);
        Assert.Throws<IOException>(() => source.GetReportDescriptor(info));
        Assert.Equal(0, _system.OpenDescriptors);
    }

    [Fact]
    public void Linux_permission_messages_name_the_fix_for_the_errno_and_the_container()
    {
        var ups = new HidDeviceInfo("/dev/hidraw0", 0x0463, 0xffff, "EATON", "Ellipse PRO", HidrawFakes.EatonSerial);
        var unknown = new HidDeviceInfo("/dev/hidraw0", 0, 0, null, null, null);

        string cgroup = UsbHidErrors.LinuxAccessDenied(ups, "open() of /dev/hidraw0 returned EPERM", inContainer: true);
        string container = UsbHidErrors.LinuxAccessDenied(ups, "open() of /dev/hidraw0 returned EACCES", inContainer: true);
        string host = UsbHidErrors.LinuxAccessDenied(ups, "open() of /dev/hidraw0 returned EACCES", inContainer: false);
        string unidentified = UsbHidErrors.LinuxAccessDenied(unknown, "open() of /dev/hidraw0 returned EACCES", inContainer: true);

        Assert.StartsWith("Permission denied on /dev/hidraw0 (USB 0463:ffff) (open() of /dev/hidraw0 returned EPERM).", cgroup, StringComparison.Ordinal);
        Assert.Contains("--device /dev/hidraw0", cgroup, StringComparison.Ordinal);
        Assert.Contains("running as root does not help", cgroup, StringComparison.Ordinal);
        Assert.Contains("group_add", container, StringComparison.Ordinal);
        Assert.Contains("run the container as root", container, StringComparison.Ordinal);
        Assert.Contains("udevadm control --reload-rules", host, StringComparison.Ordinal);
        Assert.StartsWith("Permission denied on /dev/hidraw0 (open()", unidentified, StringComparison.Ordinal);
    }

    [Fact]
    public void Permission_errors_are_unauthorized_access()
    {
        FakeHidrawDevice ups = _system.Plug(0, HidrawFakes.Eaton());
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        using IHidConnection connection = source.Open(HidrawFakes.Info(source, "/dev/hidraw0"));
        ups.FeatureErrors[0x06] = Errno.EAcces;

        Assert.Throws<UnauthorizedAccessException>(() => connection.GetFeature(0x06, 6));
    }

    [Fact]
    public void An_unplugged_device_is_lost_for_every_call()
    {
        FakeHidrawDevice ups = _system.Plug(0, HidrawFakes.Eaton());
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        HidDeviceInfo info = HidrawFakes.Info(source, "/dev/hidraw0");
        IHidConnection connection = source.Open(info);

        _system.Unplug(ups);

        Assert.Throws<HidDeviceLostException>(() => connection.GetFeature(0x06, 6));
        Assert.Throws<HidDeviceLostException>(() => connection.ReadInput(new byte[64], TimeSpan.FromSeconds(5)));
        Assert.Throws<HidDeviceLostException>(() => source.Open(info));
        Assert.Throws<HidDeviceLostException>(() => source.GetReportDescriptor(info));
        Assert.Empty(source.GetDevices());
        connection.Dispose();
        Assert.Equal(0, _system.OpenDescriptors);
    }

    [Fact]
    public void Opening_a_node_now_used_by_another_device_fails_as_lost()
    {
        FakeHidrawDevice ups = _system.Plug(0, HidrawFakes.Eaton());
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        HidDeviceInfo info = HidrawFakes.Info(source, "/dev/hidraw0");
        _system.Unplug(ups);
        _system.Plug(0, HidrawFakes.Keyboard());

        Assert.Throws<HidDeviceLostException>(() => source.Open(info));
        Assert.Equal(0, _system.OpenDescriptors);
    }

    [Fact]
    public void Disposing_closes_the_node()
    {
        _system.Plug(0, HidrawFakes.Eaton());
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        IHidConnection connection = source.Open(HidrawFakes.Info(source, "/dev/hidraw0"));
        Assert.Equal(1, _system.OpenDescriptors);

        connection.Dispose();
        connection.Dispose();

        Assert.Equal(0, _system.OpenDescriptors);
        Assert.Throws<ObjectDisposedException>(() => connection.GetFeature(0x06, 6));
        Assert.Empty(_system.Violations);
    }

    [Fact]
    public void Indexed_strings_come_from_the_usb_string_reader()
    {
        _system.Plug(0, HidrawFakes.Eaton());
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        using IHidConnection connection = source.Open(HidrawFakes.Info(source, "/dev/hidraw0"));

        Assert.Equal("PbAc", connection.GetIndexedString(4));
        Assert.Null(connection.GetIndexedString(5));
        Assert.Null(connection.GetIndexedString(0));
    }

    [Theory]
    [InlineData("/dev/hidraw0", "/dev/hidraw0", true)]
    [InlineData("/sys/devices/pci0000:00/0000:00:14.0/usb1/1-1/1-1:1.0/0003:0463:FFFF.0001/hidraw/hidraw0", "/dev/hidraw0", true)]
    [InlineData("/dev/hidraw0", "/sys/devices/pci0000:00/0000:00:14.0/usb1/1-1/1-1:1.0/0003:0463:FFFF.0001/hidraw/hidraw0", true)]
    [InlineData("/dev/hidraw1", "/dev/hidraw10", false)]
    [InlineData("/sys/devices/virtual/hidraw0", "/dev/hidraw0", false)]
    public void Device_paths_saved_with_either_layer_match(string configured, string actual, bool expected)
    {
        Assert.Equal(expected, UsbHidDeviceLocator.SamePath(configured, actual));
    }

    public void Dispose() => _system.Dispose();
}
