using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NutHub.Hidraw;

/// <summary>
/// An open hidraw node (Documentation/hid/hidraw.rst). It may be used from several threads: the descriptor is closed
/// only once the calls using it have returned, so a close from another thread never lets a call reach a descriptor
/// number the system has already given to another file. Calls on a disposed handle throw
/// <see cref="ObjectDisposedException"/>; failed I/O throws <see cref="HidrawException"/>.
/// </summary>
internal sealed class HidrawHandle : IDisposable
{
    private readonly IHidrawSystem _system;
    private readonly Descriptor _descriptor;

    private HidrawHandle(IHidrawSystem system, string path, int fd)
    {
        _system = system;
        Path = path;
        _descriptor = new Descriptor(system, fd);
    }

    /// <summary>"/dev/hidraw0".</summary>
    public string Path { get; }

    /// <summary>Opens a node read-write and non-blocking; null and the errno when that fails.</summary>
    public static HidrawHandle? TryOpen(IHidrawSystem system, string path, out int errno)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(path);
        int fd = system.Open(path, out errno);
        return fd < 0 ? null : new HidrawHandle(system, path, fd);
    }

    /// <summary>HIDIOCGRAWINFO: the bus type and USB ids. False and the errno when the kernel refuses.</summary>
    public bool TryGetInfo(out HidrawDevInfo info, out int errno)
    {
        info = default;
        return Ioctl(HidrawIoctl.Current.GetRawInfo, MemoryMarshal.AsBytes(new Span<HidrawDevInfo>(ref info)), out errno) >= 0;
    }

    /// <summary>
    /// HIDIOCGRAWNAME, HIDIOCGRAWPHYS or HIDIOCGRAWUNIQ (the request built for <see cref="HidrawIoctl.StringLength"/>):
    /// the string up to its NUL, null when the kernel refuses or the string is empty.
    /// </summary>
    public string? GetString(uint request)
    {
        Span<byte> buffer = stackalloc byte[HidrawIoctl.StringLength];
        buffer.Clear();
        int length = Ioctl(request, buffer, out _);
        if (length <= 0)
        {
            return null;
        }

        // The kernel copies the string with its NUL, cut to the buffer when longer.
        ReadOnlySpan<byte> text = buffer[..Math.Min(length, buffer.Length)];
        int end = text.IndexOf((byte)0);
        string value = Encoding.UTF8.GetString(end >= 0 ? text[..end] : text).Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>HIDIOCGRDESCSIZE then HIDIOCGRDESC; null and the errno when either fails.</summary>
    public byte[]? TryGetReportDescriptor(out int errno)
    {
        int size = 0;
        if (Ioctl(HidrawIoctl.Current.GetDescriptorSize, MemoryMarshal.AsBytes(new Span<int>(ref size)), out errno) < 0)
        {
            return null;
        }

        if (size <= 0 || size > HidrawIoctl.MaxDescriptorSize)
        {
            errno = Errno.EInval;
            return null;
        }

        var descriptor = new HidrawReportDescriptor { Size = (uint)size };
        if (Ioctl(HidrawIoctl.Current.GetDescriptor, MemoryMarshal.AsBytes(new Span<HidrawReportDescriptor>(ref descriptor)), out errno) < 0)
        {
            return null;
        }

        Span<byte> value = descriptor.Value;
        return value[..size].ToArray();
    }

    /// <summary>
    /// HIDIOCGFEATURE: <paramref name="report"/> holds the report id in byte 0 and receives the report, id first; its
    /// length is the length asked of the device. Returns the number of bytes the kernel copied back.
    /// </summary>
    public int GetFeature(Span<byte> report)
    {
        int result = Ioctl(HidrawIoctl.Current.GetFeature(report.Length), report, out int errno);
        return result >= 0 ? result : throw Failure(errno, "HIDIOCGFEATURE");
    }

    /// <summary>HIDIOCSFEATURE: sends a feature report, report id (0 without report ids) first.</summary>
    public void SetFeature(Span<byte> report)
    {
        if (Ioctl(HidrawIoctl.Current.SetFeature(report.Length), report, out int errno) < 0)
        {
            throw Failure(errno, "HIDIOCSFEATURE");
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMilliseconds"/> for an input report and reads it: the number of bytes, 0
    /// when none arrived. The report id comes first only for devices that use report ids. Throws when the device is
    /// gone.
    /// </summary>
    public int Read(Span<byte> buffer, int timeoutMilliseconds)
    {
        int fd = Enter();
        try
        {
            int ready = _system.Poll(fd, timeoutMilliseconds, out PollEvents events, out int errno);
            if (ready < 0)
            {
                throw Failure(errno, "poll");
            }

            if (ready == 0)
            {
                return 0;
            }

            // A removed device reports POLLERR | POLLHUP; reports still queued are read first.
            if ((events & PollEvents.In) == 0)
            {
                throw Failure((events & PollEvents.Nval) != 0 ? Errno.EInval : Errno.ENoDev, "poll");
            }

            int read = _system.Read(fd, buffer, out errno);
            if (read < 0)
            {
                // Another reader may have taken the report between poll and read.
                return errno == Errno.EAgain ? 0 : throw Failure(errno, "read");
            }

            return read;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Sends an output report, report id (0 without report ids) first: on the interrupt OUT endpoint when the device
    /// has one, else as a SET_REPORT control request.
    /// </summary>
    public void Write(ReadOnlySpan<byte> report)
    {
        int fd = Enter();
        try
        {
            if (_system.Write(fd, report, out int errno) < 0)
            {
                throw Failure(errno, "write");
            }
        }
        finally
        {
            Exit();
        }
    }

    public void Dispose() => _descriptor.Dispose();

    private int Ioctl(uint request, Span<byte> argument, out int errno)
    {
        int fd = Enter();
        try
        {
            return _system.Ioctl(fd, request, argument, out errno);
        }
        finally
        {
            Exit();
        }
    }

    private HidrawException Failure(int errno, string operation) =>
        new(errno, $"{operation} on {Path} failed: {Errno.Describe(errno)}.");

    private int Enter()
    {
        bool added = false;
        _descriptor.DangerousAddRef(ref added);
        return (int)_descriptor.DangerousGetHandle();
    }

    private void Exit() => _descriptor.DangerousRelease();

    /// <summary>The file descriptor; closed once disposed and no call holds it.</summary>
    private sealed class Descriptor : SafeHandleMinusOneIsInvalid
    {
        private readonly IHidrawSystem _system;

        public Descriptor(IHidrawSystem system, int fd)
            : base(ownsHandle: true)
        {
            _system = system;
            SetHandle(fd);
        }

        protected override bool ReleaseHandle()
        {
            _system.Close((int)handle);
            return true;
        }
    }
}
