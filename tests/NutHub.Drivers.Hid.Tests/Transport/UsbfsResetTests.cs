using NutHub.Drivers.Hid.Tests.Support;
using NutHub.Drivers.Hid.Transport;
using NutHub.Hidraw;
using NutHub.Hidraw.Testing;

namespace NutHub.Drivers.Hid.Tests.Transport;

/// <summary>
/// The USB reset: what the driver falls back to when a UPS stays plugged in but stops answering. It must find the
/// usbfs node of the hidraw device, ask the kernel for USBDEVFS_RESET, and explain itself when it cannot.
/// </summary>
public sealed class UsbfsResetTests : IDisposable
{
    private readonly FakeHidrawSystem _system = new();

    [Fact]
    public void The_usb_device_behind_a_node_is_reset()
    {
        FakeHidrawDevice eaton = _system.Plug(0, HidrawFakes.Eaton());
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        HidDeviceInfo device = Assert.Single(source.GetDevices());

        Assert.True(source.TryReset(device, out string detail), detail);

        Assert.Equal(eaton.UsbfsPath, detail);
        Assert.Equal(1, eaton.UsbResets);
        Assert.Contains(_system.Ioctls, i => i.Request == UsbfsReset.Request);
        Assert.Empty(_system.Violations);
        Assert.Equal(0, _system.OpenDescriptors);
    }

    [Fact]
    public void Without_sysfs_there_is_no_usbfs_node_to_reset()
    {
        using var bare = new FakeHidrawSystem(sysfs: false);
        bare.Plug(0, HidrawFakes.Eaton(), sysfs: false);
        HidrawDeviceSource source = HidrawFakes.Source(bare);
        HidDeviceInfo device = Assert.Single(source.GetDevices());

        Assert.False(source.TryReset(device, out string detail));

        Assert.Contains("/dev/hidraw0", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(bare.Ioctls, i => i.Request == UsbfsReset.Request);
    }

    [Fact]
    public void A_device_the_process_may_not_open_says_what_to_do_about_it()
    {
        FakeHidrawDevice eaton = _system.Plug(0, HidrawFakes.Eaton());
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        HidDeviceInfo device = Assert.Single(source.GetDevices());
        eaton.UsbfsOpenErrno = Errno.EAcces;

        Assert.False(source.TryReset(device, out string detail));

        Assert.Contains("189", detail, StringComparison.Ordinal);
        Assert.Equal(0, eaton.UsbResets);
    }

    [Fact]
    public void A_kernel_that_refuses_the_reset_is_reported()
    {
        FakeHidrawDevice eaton = _system.Plug(0, HidrawFakes.Eaton());
        HidrawDeviceSource source = HidrawFakes.Source(_system);
        HidDeviceInfo device = Assert.Single(source.GetDevices());
        eaton.UsbResetErrno = Errno.ENoDev;

        Assert.False(source.TryReset(device, out string detail));

        Assert.Contains("refused", detail, StringComparison.Ordinal);
        Assert.Equal(0, _system.OpenDescriptors);
    }

    public void Dispose() => _system.Dispose();
}
