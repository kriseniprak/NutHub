using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Drivers.Hid.Transport;
using NutHub.Drivers.Hid.UsbHid;

namespace NutHub.Drivers.Hid.Tests.Support;

/// <summary>Opens a <see cref="HidUpsSession"/> on a fake device the way the driver does.</summary>
internal static class TestSessions
{
    public static (HidUpsSession Session, FakeHidConnection Connection) Open(
        FakeHidDevice device, UsbHidSettings? settings = null, TimeProvider? time = null)
    {
        var source = new FakeHidDeviceSource(device);
        settings ??= new UsbHidSettings();
        UsbHidLocateResult located = UsbHidDeviceLocator.Locate(source, settings, NullLogger.Instance);
        UsbHidCandidate candidate = located.Candidate ?? throw new InvalidOperationException(located.Problem);
        var connection = (FakeHidConnection)source.Open(candidate.Device);
        candidate.Subdriver.Attach(candidate.Device);
        var session = new HidUpsSession(connection, candidate.Descriptor, candidate.Subdriver, settings,
                                        time ?? TimeProvider.System, NullLogger.Instance);
        session.Initialize();
        return (session, connection);
    }

    /// <summary>A fake device answering with the reports of a real UPS dump.</summary>
    public static FakeHidDevice FromDump(RealDeviceDump dump, string manufacturer, string product, string? serial) =>
        new(new HidDeviceInfo("/dev/hidraw0", dump.VendorId, dump.ProductId, manufacturer, product, serial),
            dump.Descriptor, dump.Reports.Values, maxInputLength: 64);

    public static IReadOnlyDictionary<string, string> Variables(HidUpsSession session) => session.BuildUpdate().Variables;
}
