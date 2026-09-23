using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace NutHub.Hidraw.Testing;

/// <summary>
/// A hidraw device as the fake system presents it: what its node answers (drivers/hid/hidraw.c), what sysfs publishes
/// about it, and switches for the failures to test.
/// </summary>
internal sealed class FakeHidrawDevice
{
    public int BusType { get; init; } = HidrawIoctl.BusUsb;

    public int VendorId { get; init; }

    public int ProductId { get; init; }

    /// <summary>HIDIOCGRAWNAME and HID_NAME.</summary>
    public string? Name { get; init; }

    public string Phys { get; init; } = "usb-0000:00:14.0-1/input0";

    /// <summary>HIDIOCGRAWUNIQ and HID_UNIQ.</summary>
    public string? Uniq { get; init; }

    public byte[] Descriptor { get; init; } = [];

    /// <summary>The USB device's sysfs strings.</summary>
    public string? Manufacturer { get; init; }

    public string? Product { get; init; }

    public string? Serial { get; init; }

    public int ReleaseNumberBcd { get; init; } = 0x0100;

    /// <summary>What open() fails with (EACCES: a node only root may open); 0 opens.</summary>
    public int OpenErrno { get; set; }

    /// <summary>The usbfs node of the device, "/dev/bus/usb/001/003"; empty when sysfs was not created.</summary>
    public string UsbfsPath { get; set; } = "";

    /// <summary>Non-zero: opening the usbfs node fails with this errno; the node's own OpenErrno does not apply to it.</summary>
    public int UsbfsOpenErrno { get; set; }

    /// <summary>How many times the hidraw node was opened, successfully or not.</summary>
    public int Opens;

    /// <summary>Non-zero: USBDEVFS_RESET fails with this errno.</summary>
    public int UsbResetErrno { get; set; }

    /// <summary>How many times the device was reset through usbfs.</summary>
    public int UsbResets;

    /// <summary>False: a kernel before 5.6, which does not know HIDIOCGRAWUNIQ.</summary>
    public bool UniqSupported { get; init; } = true;

    /// <summary>True: HIDIOCGRDESCSIZE fails, so the descriptor can only come from sysfs.</summary>
    public bool DescriptorIoctlFails { get; init; }

    /// <summary>Feature reports by report id, report id first (0 for devices without report ids).</summary>
    public ConcurrentDictionary<byte, byte[]> Features { get; } = new();

    /// <summary>Feature report ids whose requests fail, with the errno (EPIPE: the firmware stalls the request).</summary>
    public ConcurrentDictionary<byte, int> FeatureErrors { get; } = new();

    /// <summary>Every HIDIOCSFEATURE buffer, in order.</summary>
    public ConcurrentQueue<byte[]> FeatureWrites { get; } = new();

    /// <summary>Every write() buffer (output reports), in order.</summary>
    public ConcurrentQueue<byte[]> OutputWrites { get; } = new();

    /// <summary>Called for every output report, to make the device answer.</summary>
    public Action<FakeHidrawDevice, byte[]>? OnWrite { get; set; }

    /// <summary>"/dev/hidraw0" while plugged in.</summary>
    public string Path { get; internal set; } = "";

    public bool Present { get; internal set; }

    internal Queue<byte[]> Inputs { get; } = new();

    public void AddFeatures(IEnumerable<byte[]> reports)
    {
        foreach (byte[] report in reports)
        {
            Features[report[0]] = report;
        }
    }
}

/// <summary>
/// Linux as the hidraw layer sees it, in memory: device nodes that answer the hidraw ioctls, read, write and poll the
/// way drivers/hid/hidraw.c does, a sysfs tree in a temporary directory (its symbolic links are simulated, so the
/// tests run on Windows too), and unplugging. Requests whose encoded size or direction does not match the buffer
/// passed are recorded in <see cref="Violations"/>.
/// </summary>
internal sealed class FakeHidrawSystem : IHidrawSystem, IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, FakeHidrawDevice> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<int, FakeHidrawDevice> _open = [];
    private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);
    private readonly List<(uint Request, int Length)> _ioctls = [];
    private readonly List<string> _violations = [];
    private readonly string _root;
    private int _nextFd = 3;
    private int _nextHid;

    public FakeHidrawSystem(bool sysfs = true)
    {
        _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nuthub-hidraw-" + Guid.NewGuid().ToString("N"));
        SysfsRoot = System.IO.Path.Combine(_root, "sys");
        if (sysfs)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(SysfsRoot, "class", "hidraw"));
        }
    }

    public string SysfsRoot { get; }

    /// <summary>Other entries of /dev that match hidraw* ("/dev/hidraw" without a number...).</summary>
    public List<string> OtherEntries { get; } = [];

    public IReadOnlyList<(uint Request, int Length)> Ioctls
    {
        get
        {
            lock (_lock)
            {
                return [.. _ioctls];
            }
        }
    }

    public IReadOnlyList<string> Violations
    {
        get
        {
            lock (_lock)
            {
                return [.. _violations];
            }
        }
    }

    /// <summary>Descriptors opened and not closed yet.</summary>
    public int OpenDescriptors
    {
        get
        {
            lock (_lock)
            {
                return _open.Count;
            }
        }
    }

    /// <summary>Plugs a device in as /dev/hidraw<paramref name="number"/>, with its sysfs entries unless told otherwise.</summary>
    public FakeHidrawDevice Plug(int number, FakeHidrawDevice device, bool sysfs = true)
    {
        string path = "/dev/hidraw" + number.ToString(CultureInfo.InvariantCulture);
        lock (_lock)
        {
            device.Path = path;
            device.Present = true;
            _nodes[path] = device;
        }

        if (sysfs && Directory.Exists(SysfsRoot))
        {
            CreateSysfs(number, device);
        }

        return device;
    }

    /// <summary>
    /// Unplugs a device: its node and sysfs entry disappear, descriptors still open answer ENODEV to ioctls and
    /// write, EIO to read once the queue is empty, and POLLERR | POLLHUP to poll.
    /// </summary>
    public void Unplug(FakeHidrawDevice device)
    {
        lock (_lock)
        {
            device.Present = false;
            _nodes.Remove(device.Path);
            if (device.UsbfsPath.Length > 0)
            {
                _nodes.Remove(device.UsbfsPath);
            }

            _links.Remove(ClassEntry(System.IO.Path.GetFileName(device.Path)));
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Queues an input report the way the kernel stores it: report id first only for numbered reports.</summary>
    public void QueueInput(FakeHidrawDevice device, params byte[] report)
    {
        lock (_lock)
        {
            device.Inputs.Enqueue(report);
            Monitor.PulseAll(_lock);
        }
    }

    public IReadOnlyList<string> ListNodes()
    {
        lock (_lock)
        {
            return [.. _nodes.Keys, .. OtherEntries];
        }
    }

    public string? ResolveLink(string path)
    {
        lock (_lock)
        {
            return _links.GetValueOrDefault(path);
        }
    }

    public int Open(string path, out int errno)
    {
        lock (_lock)
        {
            if (!_nodes.TryGetValue(path, out FakeHidrawDevice? device))
            {
                errno = Errno.ENoEnt;
                return -1;
            }

            // The usbfs node is a different file: a hidraw node that answers EIO does not make it unopenable.
            bool usbfs = path == device.UsbfsPath;
            if (!usbfs)
            {
                Interlocked.Increment(ref device.Opens);
            }

            int failWith = usbfs ? device.UsbfsOpenErrno : device.OpenErrno;
            if (failWith != 0)
            {
                errno = failWith;
                return -1;
            }

            errno = 0;
            int fd = _nextFd++;
            _open[fd] = device;
            return fd;
        }
    }

    public void Close(int fd)
    {
        lock (_lock)
        {
            if (!_open.Remove(fd))
            {
                _violations.Add($"close of unknown descriptor {fd}");
            }
        }
    }

    public int Ioctl(int fd, uint request, Span<byte> argument, out int errno)
    {
        FakeHidrawDevice device;
        lock (_lock)
        {
            _ioctls.Add((request, argument.Length));
            if (!_open.TryGetValue(fd, out device!))
            {
                errno = 9; // EBADF
                _violations.Add($"ioctl on unknown descriptor {fd}");
                return -1;
            }
        }

        // usbfs, not hidraw: the reset of the whole USB device.
        if (request == UsbfsReset.Request)
        {
            if (!device.Present)
            {
                errno = Errno.ENoDev;
                return -1;
            }

            if (device.UsbResetErrno != 0)
            {
                errno = device.UsbResetErrno;
                return -1;
            }

            Interlocked.Increment(ref device.UsbResets);
            errno = 0;
            return 0;
        }

        HidrawIoctl layout = HidrawIoctl.Current;
        int number = (int)(request & 0xFF);
        int size = (int)((request >> 16) & ((1u << layout.SizeBits) - 1));
        uint direction = request >> (16 + layout.SizeBits);
        if (((request >> 8) & 0xFF) != 'H')
        {
            errno = Errno.ENotTy;
            return -1;
        }

        uint expected = number is 0x06 or 0x07 ? layout.Read | layout.Write : layout.Read;
        if (size != argument.Length || direction != expected)
        {
            lock (_lock)
            {
                _violations.Add($"ioctl 0x{request:x8}: size {size} and direction {direction} for a {argument.Length}-byte argument");
            }
        }

        // hidraw_ioctl checks the device before anything else.
        if (!device.Present)
        {
            errno = Errno.ENoDev;
            return -1;
        }

        errno = 0;
        switch (number)
        {
            case 0x01 when !device.DescriptorIoctlFails:
                MemoryMarshal.Write(argument, device.Descriptor.Length);
                return 0;
            case 0x02 when !device.DescriptorIoctlFails:
            {
                int length = MemoryMarshal.Read<int>(argument);
                if (length > HidrawIoctl.MaxDescriptorSize - 1)
                {
                    errno = Errno.EInval;
                    return -1;
                }

                device.Descriptor.AsSpan(0, Math.Min(length, device.Descriptor.Length)).CopyTo(argument[4..]);
                return 0;
            }

            case 0x03:
                var info = new HidrawDevInfo
                {
                    BusType = (uint)device.BusType,
                    Vendor = (short)device.VendorId,
                    Product = (short)device.ProductId,
                };
                MemoryMarshal.Write(argument, in info);
                return 0;
            case 0x04:
                return CopyString(device.Name ?? $"HID {device.VendorId:x4}:{device.ProductId:x4}", argument);
            case 0x05:
                return CopyString(device.Phys, argument);
            case 0x08 when device.UniqSupported:
                return CopyString(device.Uniq ?? "", argument);
            case 0x06:
                device.FeatureWrites.Enqueue(argument.ToArray());
                if (device.FeatureErrors.TryGetValue(argument[0], out int setError))
                {
                    errno = setError;
                    return -1;
                }

                device.Features[argument[0]] = argument.ToArray();
                return argument.Length;
            case 0x07:
            {
                byte id = argument[0];
                if (device.FeatureErrors.ContainsKey(id) || !device.Features.TryGetValue(id, out byte[]? report))
                {
                    errno = device.FeatureErrors.GetValueOrDefault(id, Errno.EPipe);
                    return -1;
                }

                int count = Math.Min(report.Length, argument.Length);
                report.AsSpan(0, count).CopyTo(argument);
                return count;
            }

            case 0x08:
                errno = Errno.ENotTy;
                return -1;
            default:
                errno = Errno.EInval;
                return -1;
        }
    }

    public int Read(int fd, Span<byte> buffer, out int errno)
    {
        lock (_lock)
        {
            FakeHidrawDevice device = _open[fd];
            if (device.Inputs.TryDequeue(out byte[]? report))
            {
                // hidraw_read copies at most the buffer length and drops the rest of the report.
                int count = Math.Min(report.Length, buffer.Length);
                report.AsSpan(0, count).CopyTo(buffer);
                errno = 0;
                return count;
            }

            errno = device.Present ? Errno.EAgain : Errno.EIo;
            return -1;
        }
    }

    public int Write(int fd, ReadOnlySpan<byte> buffer, out int errno)
    {
        FakeHidrawDevice device;
        byte[] report = buffer.ToArray();
        lock (_lock)
        {
            device = _open[fd];
            if (!device.Present)
            {
                errno = Errno.ENoDev;
                return -1;
            }
        }

        device.OutputWrites.Enqueue(report);
        device.OnWrite?.Invoke(device, report);
        errno = 0;
        return report.Length;
    }

    public int Poll(int fd, int timeoutMilliseconds, out PollEvents events, out int errno)
    {
        errno = 0;
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        lock (_lock)
        {
            FakeHidrawDevice device = _open[fd];
            while (true)
            {
                events = (device.Inputs.Count > 0 ? PollEvents.In : PollEvents.None) |
                         (device.Present ? PollEvents.None : PollEvents.Err | PollEvents.Hup);
                if (events != PollEvents.None)
                {
                    return 1;
                }

                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    return 0;
                }

                Monitor.Wait(_lock, TimeSpan.FromMilliseconds(remaining));
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary directory is harmless.
        }
    }

    /// <summary>The key the code under test resolves: Path.Combine(SysfsRoot, "class", "hidraw", name).</summary>
    private string ClassEntry(string name) => System.IO.Path.Combine(SysfsRoot, "class", "hidraw", name);

    /// <summary>
    /// .../usb1/1-N/1-N:1.0/0003:VVVV:PPPP.NNNN/hidraw/hidrawN for USB devices, .../virtual/misc/uhid/... for others.
    /// </summary>
    private void CreateSysfs(int number, FakeHidrawDevice device)
    {
        int hidNumber = Interlocked.Increment(ref _nextHid);
        string hidName = string.Create(CultureInfo.InvariantCulture,
            $"{device.BusType:X4}:{device.VendorId:X4}:{device.ProductId:X4}.{hidNumber:X4}");
        string parent;
        if (device.BusType == HidrawIoctl.BusUsb)
        {
            string usb = System.IO.Path.Combine(SysfsRoot, "devices", Entry("pci0000:00"), Entry("0000:00:14.0"), "usb1", $"1-{number + 1}");
            Directory.CreateDirectory(usb);
            File.WriteAllText(System.IO.Path.Combine(usb, "idVendor"), $"{device.VendorId:x4}\n");
            File.WriteAllText(System.IO.Path.Combine(usb, "idProduct"), $"{device.ProductId:x4}\n");
            File.WriteAllText(System.IO.Path.Combine(usb, "bcdDevice"), $"{device.ReleaseNumberBcd:x4}\n");
            File.WriteAllText(System.IO.Path.Combine(usb, "busnum"), "1");
            File.WriteAllText(System.IO.Path.Combine(usb, "devnum"), (number + 2).ToString(CultureInfo.InvariantCulture));
            device.UsbfsPath = string.Create(CultureInfo.InvariantCulture, $"/dev/bus/usb/001/{number + 2:D3}");
            lock (_lock)
            {
                _nodes[device.UsbfsPath] = device; // open()able like the node itself, for USBDEVFS_RESET
            }

            WriteOptional(usb, "manufacturer", device.Manufacturer);
            WriteOptional(usb, "product", device.Product);
            WriteOptional(usb, "serial", device.Serial);
            parent = System.IO.Path.Combine(usb, Entry($"1-{number + 1}:1.0"));
        }
        else
        {
            parent = System.IO.Path.Combine(SysfsRoot, "devices", "virtual", "misc", "uhid");
        }

        string hid = System.IO.Path.Combine(parent, Entry(hidName));
        string classDevice = System.IO.Path.Combine(hid, "hidraw", "hidraw" + number.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(classDevice);
        var uevent = new StringBuilder()
            .Append("DRIVER=hid-generic\n")
            .Append(CultureInfo.InvariantCulture, $"HID_ID={device.BusType:X4}:{device.VendorId:X8}:{device.ProductId:X8}\n")
            .Append(CultureInfo.InvariantCulture, $"HID_NAME={device.Name}\n")
            .Append(CultureInfo.InvariantCulture, $"HID_PHYS={device.Phys}\n")
            .Append(CultureInfo.InvariantCulture, $"HID_UNIQ={device.Uniq}\n")
            .Append(CultureInfo.InvariantCulture, $"MODALIAS=hid:b{device.BusType:X4}g0001v{device.VendorId:X8}p{device.ProductId:X8}\n");
        File.WriteAllText(System.IO.Path.Combine(hid, "uevent"), uevent.ToString());
        File.WriteAllBytes(System.IO.Path.Combine(hid, "report_descriptor"), device.Descriptor);
        File.WriteAllText(System.IO.Path.Combine(classDevice, "dev"), $"243:{number}\n");
        lock (_lock)
        {
            _links[ClassEntry("hidraw" + number.ToString(CultureInfo.InvariantCulture))] = classDevice;
        }
    }

    /// <summary>sysfs names contain colons, which Windows does not allow in file names; the code under test never reads them.</summary>
    private static string Entry(string name) => OperatingSystem.IsWindows() ? name.Replace(':', '_') : name;

    private static void WriteOptional(string directory, string name, string? value)
    {
        if (value is not null)
        {
            File.WriteAllText(System.IO.Path.Combine(directory, name), value + "\n");
        }
    }

    /// <summary>The kernel copies the string and its NUL, cut to the buffer; the result is the count copied.</summary>
    private static int CopyString(string value, Span<byte> argument)
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes(value), 0];
        int count = Math.Min(bytes.Length, argument.Length);
        bytes.AsSpan(0, count).CopyTo(argument);
        return count;
    }
}
