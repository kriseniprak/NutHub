using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Drivers.Hid.Tests.Support;
using NutHub.Drivers.Hid.Transport;
using NutHub.Drivers.Hid.UsbHid;
using NutHub.Hidraw;
using NutHub.Hidraw.Testing;

namespace NutHub.Drivers.Hid.Tests.UsbHid;

/// <summary>
/// The whole usbhid driver on the native hidraw layer, over a fake Linux whose nodes answer with the descriptors and
/// reports of real Eaton UPSes: the path a container without libudev takes.
/// </summary>
public sealed class HidrawDriverTests : IDisposable
{
    private static readonly UsbHidTimings Fast = new()
    {
        RetryDelay = TimeSpan.FromMilliseconds(20),
        InterruptWait = TimeSpan.FromMilliseconds(20),
        MinimumReadTimeout = TimeSpan.FromSeconds(10),
        CommandTimeout = TimeSpan.FromSeconds(5),
        ResetAfterFailures = 2,
        ResetInterval = TimeSpan.FromMinutes(1),
    };

    private readonly FakeHidrawSystem _system = new();

    [Fact]
    public async Task Reads_an_eaton_5sc_through_hidraw()
    {
        _system.Plug(0, HidrawFakes.Eaton(RealDeviceDump.Eaton5Sc750, product: "Eaton 5SC"));
        await using RunningDriver run = Start(HidrawFakes.Source(_system));

        await run.Context.WaitUntilAsync(c => c.Updates.Count >= 1, "the first reading");

        IReadOnlyDictionary<string, string> vars = run.Context.Last!.Variables;
        Assert.Equal("OL", vars["ups.status"]);
        Assert.Equal("100", vars["battery.charge"]);
        Assert.Equal("1772", vars["battery.runtime"]);
        Assert.Equal("EATON", vars["ups.mfr"]);
        Assert.Equal(HidrawFakes.EatonSerial, vars["ups.serial"]);
        Assert.Empty(_system.Violations);
    }

    [Fact]
    public async Task Reads_an_eaton_3s_through_hidraw_without_sysfs()
    {
        using var bare = new FakeHidrawSystem(sysfs: false);
        bare.Plug(0, HidrawFakes.Eaton(RealDeviceDump.Eaton3S700, product: "Eaton 3S"));
        await using RunningDriver run = Start(HidrawFakes.Source(bare));

        await run.Context.WaitUntilAsync(c => c.Updates.Count >= 1, "the first reading");

        IReadOnlyDictionary<string, string> vars = run.Context.Last!.Variables;
        Assert.Equal("OL CHRG", vars["ups.status"]);
        Assert.Equal("92", vars["battery.charge"]);
        Assert.Equal("2737", vars["battery.runtime"]);
        Assert.Empty(bare.Violations);
    }

    [Fact]
    public async Task Reconnects_when_the_ups_comes_back_on_another_node()
    {
        FakeHidrawDevice first = _system.Plug(0, HidrawFakes.Eaton());
        await using RunningDriver run = Start(HidrawFakes.Source(_system));
        await run.Context.WaitUntilAsync(c => c.Updates.Count >= 1, "the first reading");

        _system.Unplug(first);
        await run.Context.WaitUntilAsync(c => c.Events.Any(e => e.StartsWith("disconnected: ", StringComparison.Ordinal)), "the disconnection");
        int published = run.Context.Updates.Count;
        FakeHidrawDevice second = HidrawFakes.Eaton();
        second.Features[0x06] = [0x06, 0x2a, 0x10, 0x0e, 0x00, 0x00];   // 42 %, 3600 s
        _system.Plug(4, second);
        await run.Context.WaitUntilAsync(c => c.Updates.Count > published && c.Last!.Variables["battery.charge"] == "42", "the reconnection");

        Assert.Equal("3600", run.Context.Last!.Variables["battery.runtime"]);
        Assert.Contains(run.Context.Events, e => e.StartsWith("disconnected: Communication with the UPS was lost", StringComparison.Ordinal));
        Assert.Empty(_system.Violations);
    }

    [Fact]
    public async Task Reports_the_permission_problem()
    {
        _system.Plug(0, HidrawFakes.Eaton(openErrno: Errno.EAcces));
        await using RunningDriver run = Start(HidrawFakes.Source(_system));

        // Never connected: the problem is a "connecting" detail.
        await run.Context.WaitUntilAsync(c => c.Events.Any(e => e.Contains("EACCES", StringComparison.Ordinal)), "the permission message");

        string failure = run.Context.Events.First(e => e.Contains("EACCES", StringComparison.Ordinal));
        Assert.StartsWith("connecting: ", failure, StringComparison.Ordinal);
        Assert.Contains("/dev/hidraw0", failure, StringComparison.Ordinal);
        Assert.Empty(run.Context.Updates);
    }

    [Fact]
    public async Task A_ups_that_answers_nothing_any_more_has_its_usb_device_reset()
    {
        // The UPS is there and sysfs describes it, but the node answers EIO to every open: a hung device, which
        // only re-enumeration brings back.
        FakeHidrawDevice eaton = _system.Plug(0, HidrawFakes.Eaton(openErrno: Errno.EIo));
        await using RunningDriver run = Start(HidrawFakes.Source(_system));

        await run.Context.WaitUntilAsync(_ => Volatile.Read(ref eaton.UsbResets) >= 1, "the USB reset");
        Assert.Equal(1, Volatile.Read(ref eaton.UsbResets)); // and not once per failed attempt
        Assert.Empty(_system.Violations);
    }

    [Fact]
    public async Task The_usb_reset_can_be_turned_off()
    {
        FakeHidrawDevice eaton = _system.Plug(0, HidrawFakes.Eaton(openErrno: Errno.EIo));
        await using RunningDriver run = Start(HidrawFakes.Source(_system), new UsbHidSettings { UsbReset = false });

        await run.Context.WaitUntilAsync(_ => Volatile.Read(ref eaton.Opens) >= 5, "five failed attempts");

        Assert.Equal(0, Volatile.Read(ref eaton.UsbResets));
    }

    public void Dispose() => _system.Dispose();

    private static RunningDriver Start(IHidDeviceSource source, UsbHidSettings? settings = null)
    {
        var driver = new UsbHidDriver("test", settings ?? new UsbHidSettings(), source, NullLogger.Instance, TimeProvider.System, Fast);
        var context = new RecordingDriverContext(TimeSpan.FromMilliseconds(30));
        var cts = new CancellationTokenSource();
        Task task = Task.Run(() => driver.RunAsync(context, cts.Token));
        return new RunningDriver(driver, context, cts, task);
    }

    private sealed record RunningDriver(UsbHidDriver Driver, RecordingDriverContext Context, CancellationTokenSource Cts, Task Task)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Cts.CancelAsync();
            await Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Driver.DisposeAsync();
            Cts.Dispose();
        }
    }
}
