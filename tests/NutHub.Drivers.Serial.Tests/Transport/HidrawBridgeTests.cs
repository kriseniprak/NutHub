using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Drivers.Serial.Transport;
using NutHub.Hidraw;
using NutHub.Hidraw.Testing;

namespace NutHub.Drivers.Serial.Tests.Transport;

/// <summary>
/// The USB-serial bridges over the native hidraw layer, on a fake Linux: the report sizes read from the descriptor,
/// the listing, and a Q1 exchange with a Cypress bridge whose reports carry no report id.
/// </summary>
public sealed class HidrawBridgeTests : IDisposable
{
    /// <summary>A Cypress-style bridge: vendor-defined collection, 8-byte input and output reports, no report ids.</summary>
    private static readonly byte[] CypressDescriptor =
    [
        0x06, 0xA0, 0xFF,   // Usage Page (vendor 0xFFA0)
        0x09, 0x01,         // Usage (1)
        0xA1, 0x01,         // Collection (Application)
        0x09, 0x02,         //   Usage (2)
        0x15, 0x00,         //   Logical Minimum (0)
        0x26, 0xFF, 0x00,   //   Logical Maximum (255)
        0x75, 0x08,         //   Report Size (8)
        0x95, 0x08,         //   Report Count (8)
        0x81, 0x02,         //   Input (Data, Variable)
        0x09, 0x03,         //   Usage (3)
        0x91, 0x02,         //   Output (Data, Variable)
        0xC0,               // End Collection
    ];

    private readonly FakeHidrawSystem _system = new();

    [Fact]
    public void Measures_reports_without_report_ids()
    {
        HidReportLengths lengths = HidReportLengths.Measure(CypressDescriptor);

        Assert.Equal(new HidReportLengths(9, 9, false), lengths);
    }

    [Fact]
    public void Measures_numbered_reports_and_restores_pushed_globals()
    {
        byte[] descriptor =
        [
            0x06, 0x00, 0xFF, 0x09, 0x01, 0xA1, 0x01,
            0x85, 0x01, 0x75, 0x08, 0x95, 0x04, 0x81, 0x02,   // input report 1: 4 bytes
            0xA4,                                             // Push
            0x85, 0x02, 0x95, 0x10, 0x91, 0x02,               // output report 2: 16 bytes
            0xB4,                                             // Pop: report 1, 4 x 8 bits again
            0x81, 0x02,                                       // input report 1 grows to 8 bytes
            0xB1, 0x02,                                       // a feature item counts for neither
            0xC0,
        ];

        Assert.Equal(new HidReportLengths(9, 17, true), HidReportLengths.Measure(descriptor));
        Assert.Equal(new HidReportLengths(0, 0, false), HidReportLengths.Measure([]));
    }

    [Fact]
    public void Lists_usb_devices_with_their_ids_serial_and_report_lengths()
    {
        _system.Plug(0, Cypress());
        _system.Plug(1, new FakeHidrawDevice { BusType = 5, VendorId = 0x0665, ProductId = 0x5161, Descriptor = CypressDescriptor });
        var backend = new HidrawBridgeBackend(_system);

        HidBridgeDevice device = Assert.Single(backend.GetDevices(0x0665, null));

        Assert.Equal("/dev/hidraw0", device.Path);
        Assert.Equal("0665:5161", device.UsbId);
        Assert.Equal("CY0001", backend.GetSerialNumber(device));
        Assert.Equal((9, 9), backend.GetReportLengths(device));
        Assert.Empty(backend.GetDevices(0x0001, null));
        Assert.Equal(0, _system.OpenDescriptors);
    }

    [Fact]
    public async Task Talks_q1_through_a_cypress_bridge()
    {
        FakeHidrawDevice bridge = _system.Plug(0, Cypress());
        var received = new List<byte>();
        bridge.OnWrite = (device, report) =>
        {
            // Output reports: report id 0, then 8 bytes of the serial stream.
            received.AddRange(report.AsSpan(1).ToArray().TakeWhile(b => b != 0));
            if (received.Contains((byte)'\r'))
            {
                byte[] reply = Encoding.ASCII.GetBytes("(226.0 195.0 226.0 014 49.9 27.5 30.0 00001001\r");
                for (int offset = 0; offset < reply.Length; offset += 8)
                {
                    byte[] chunk = new byte[8];
                    reply.AsSpan(offset, Math.Min(8, reply.Length - offset)).CopyTo(chunk);
                    _system.QueueInput(device, chunk);
                }
            }
        };

        var transport = new HidBridgeTransport(new UsbBridgeSettings(0x0665, 0x5161, null, null), TimeProvider.System,
                                               NullLogger.Instance, new HidrawBridgeBackend(_system));
        await transport.OpenAsync(CancellationToken.None);
        await transport.WriteAsync(Encoding.ASCII.GetBytes("Q1\r"), CancellationToken.None);
        TransportRead read = await transport.ReadUntilAsync(new[] { (byte)'\r' }, TimeSpan.FromSeconds(5), 256, CancellationToken.None);
        await transport.CloseAsync();

        Assert.Equal(UsbBridgeKind.Cypress, transport.Kind);
        Assert.True(read.IsComplete);
        Assert.Equal("(226.0 195.0 226.0 014 49.9 27.5 30.0 00001001", Encoding.ASCII.GetString(read.Payload));
        Assert.Equal(9, Assert.Single(bridge.OutputWrites).Length);
        Assert.Equal(0, _system.OpenDescriptors);
        Assert.Empty(_system.Violations);
    }

    [Fact]
    public async Task A_node_without_permission_is_reported_as_such()
    {
        FakeHidrawDevice bridge = Cypress();
        bridge.OpenErrno = Errno.EAcces;
        _system.Plug(0, bridge);
        var transport = new HidBridgeTransport(new UsbBridgeSettings(0x0665, 0x5161, null, null), TimeProvider.System,
                                               NullLogger.Instance, new HidrawBridgeBackend(_system));

        var error = await Assert.ThrowsAsync<TransportException>(() => transport.OpenAsync(CancellationToken.None));

        Assert.Equal(TransportErrorKind.AccessDenied, error.Kind);
        Assert.Contains("/dev/hidraw0", error.Message, StringComparison.Ordinal);
        Assert.Contains("EACCES", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_node_the_device_cgroup_refuses_asks_for_the_device_to_be_passed()
    {
        FakeHidrawDevice bridge = Cypress();
        bridge.OpenErrno = Errno.EPerm;
        _system.Plug(0, bridge);
        var transport = new HidBridgeTransport(new UsbBridgeSettings(0x0665, 0x5161, null, null), TimeProvider.System,
                                               NullLogger.Instance, new HidrawBridgeBackend(_system));

        var error = await Assert.ThrowsAsync<TransportException>(() => transport.OpenAsync(CancellationToken.None));

        Assert.Equal(TransportErrorKind.AccessDenied, error.Kind);
        Assert.Contains("EPERM", error.Message, StringComparison.Ordinal);
        Assert.Contains("--device /dev/hidraw0", error.Message, StringComparison.Ordinal);
        Assert.Contains("group_add", HidBridgeTransport.LinuxAccessDenied("0665:5161", "/dev/hidraw0", Errno.EAcces, inContainer: true).Message,
                        StringComparison.Ordinal);
        Assert.Contains("udev rule", HidBridgeTransport.LinuxAccessDenied("0665:5161", "/dev/hidraw0", Errno.EAcces, inContainer: false).Message,
                        StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unplugged_bridge_loses_the_connection()
    {
        FakeHidrawDevice bridge = _system.Plug(0, Cypress());
        var transport = new HidBridgeTransport(new UsbBridgeSettings(0x0665, 0x5161, null, null), TimeProvider.System,
                                               NullLogger.Instance, new HidrawBridgeBackend(_system));
        await transport.OpenAsync(CancellationToken.None);

        _system.Unplug(bridge);

        var error = await Assert.ThrowsAsync<TransportException>(() => transport.WriteAsync(Encoding.ASCII.GetBytes("Q1\r"), CancellationToken.None));
        Assert.Equal(TransportErrorKind.ConnectionLost, error.Kind);
        await transport.CloseAsync();
        Assert.Equal(0, _system.OpenDescriptors);
    }

    public void Dispose() => _system.Dispose();

    private static FakeHidrawDevice Cypress() => new()
    {
        VendorId = 0x0665,
        ProductId = 0x5161,
        Name = "INNO TECH USB to Serial",
        Uniq = "CY0001",
        Descriptor = CypressDescriptor,
        Manufacturer = "INNO TECH",
        Product = "USB to Serial",
        Serial = "CY0001",
    };
}
