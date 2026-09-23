using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NutHub.Hidraw;

/// <summary>The events poll() reports for a hidraw node (include/uapi/asm-generic/poll.h).</summary>
[Flags]
internal enum PollEvents : short
{
    None = 0,
    In = 0x001,
    Err = 0x008,
    Hup = 0x010,
    Nval = 0x020,
}

/// <summary>
/// The operating system calls of the hidraw layer. Enumeration, the decoding of the ioctl answers and the sysfs files
/// all go through it, so tests can run them against a fake system with fake nodes and a sysfs tree in a temporary
/// directory. Calls report failures through <c>errno</c> instead of throwing.
/// </summary>
internal interface IHidrawSystem
{
    /// <summary>Where sysfs is mounted: "/sys". It may be missing (some containers) or read-only.</summary>
    string SysfsRoot { get; }

    /// <summary>The entries named hidraw* in /dev, in any order; empty when there are none.</summary>
    IReadOnlyList<string> ListNodes();

    /// <summary>The final target of a symbolic link (sysfs class entries link into /sys/devices), null when unavailable.</summary>
    string? ResolveLink(string path);

    /// <summary>open(path, O_RDWR | O_NONBLOCK | O_CLOEXEC): the file descriptor, or -1 and the errno.</summary>
    int Open(string path, out int errno);

    void Close(int fd);

    /// <summary>ioctl(fd, request, argument): the result (0 or more), or -1 and the errno.</summary>
    int Ioctl(int fd, uint request, Span<byte> argument, out int errno);

    /// <summary>read(): the number of bytes, or -1 and the errno (EAGAIN when no report is queued).</summary>
    int Read(int fd, Span<byte> buffer, out int errno);

    /// <summary>write(): the number of bytes, or -1 and the errno.</summary>
    int Write(int fd, ReadOnlySpan<byte> buffer, out int errno);

    /// <summary>
    /// poll() for input on one descriptor: 1 with the events, 0 on timeout (also when a signal interrupted the
    /// wait), or -1 and the errno.
    /// </summary>
    int Poll(int fd, int timeoutMilliseconds, out PollEvents events, out int errno);
}

/// <summary>
/// The real system: libc calls on the /dev/hidraw* nodes and the files of /sys. The flag values are those of every
/// Linux architecture .NET supports (O_NONBLOCK 04000, O_CLOEXEC 02000000).
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class LibcHidrawSystem : IHidrawSystem
{
    private const int ORdWr = 0x2;
    private const int ONonBlock = 0x800;
    private const int OCloExec = 0x80000;

    private LibcHidrawSystem()
    {
    }

    public static LibcHidrawSystem Instance { get; } = new();

    public string SysfsRoot => "/sys";

    public IReadOnlyList<string> ListNodes()
    {
        try
        {
            return Directory.EnumerateFileSystemEntries("/dev", "hidraw*").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public string? ResolveLink(string path)
    {
        try
        {
            return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public int Open(string path, out int errno)
    {
        int fd;
        do
        {
            fd = NativeOpen(path, ORdWr | ONonBlock | OCloExec);
            errno = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
        }
        while (fd < 0 && errno == Errno.EIntr);

        return fd;
    }

    // Linux releases the descriptor even when close() fails, so there is nothing to retry.
    public void Close(int fd) => _ = NativeClose(fd);

    public unsafe int Ioctl(int fd, uint request, Span<byte> argument, out int errno)
    {
        fixed (byte* pointer = argument)
        {
            int result;
            do
            {
                // The request is an unsigned long: 32 bits on 32-bit ARM, 64 on x64 and ARM64.
                result = NativeIoctl(fd, request, pointer);
                errno = result < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
            while (result < 0 && errno == Errno.EIntr);

            return result;
        }
    }

    public unsafe int Read(int fd, Span<byte> buffer, out int errno)
    {
        fixed (byte* pointer = buffer)
        {
            nint result;
            do
            {
                result = NativeRead(fd, pointer, (nuint)buffer.Length);
                errno = result < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
            while (result < 0 && errno == Errno.EIntr);

            return (int)result;
        }
    }

    public unsafe int Write(int fd, ReadOnlySpan<byte> buffer, out int errno)
    {
        fixed (byte* pointer = buffer)
        {
            nint result;
            do
            {
                result = NativeWrite(fd, pointer, (nuint)buffer.Length);
                errno = result < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
            while (result < 0 && errno == Errno.EIntr);

            return (int)result;
        }
    }

    public unsafe int Poll(int fd, int timeoutMilliseconds, out PollEvents events, out int errno)
    {
        var request = new PollFd { Fd = fd, Events = (short)PollEvents.In };
        int result = NativePoll(&request, 1, timeoutMilliseconds);
        errno = result < 0 ? Marshal.GetLastPInvokeError() : 0;
        events = result > 0 ? (PollEvents)request.REvents : PollEvents.None;
        return result < 0 && errno == Errno.EIntr ? 0 : result;
    }

    /// <summary>struct pollfd: int fd, short events, short revents.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short REvents;
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeOpen(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int NativeClose(int fd);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static unsafe partial int NativeIoctl(int fd, nuint request, void* argument);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static unsafe partial nint NativeRead(int fd, void* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial nint NativeWrite(int fd, void* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static unsafe partial int NativePoll(PollFd* fds, nuint count, int timeout);
}
