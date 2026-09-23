namespace NutHub.Hidraw;

/// <summary>
/// The errno values the hidraw layer meets (include/uapi/asm-generic/errno-base.h and errno.h, the same on every
/// architecture .NET runs on), and how the drivers read them.
/// </summary>
internal static class Errno
{
    public const int EPerm = 1;
    public const int ENoEnt = 2;
    public const int EIntr = 4;
    public const int EIo = 5;
    public const int ENxIo = 6;
    public const int EAgain = 11;
    public const int EAcces = 13;
    public const int EBusy = 16;
    public const int ENoDev = 19;
    public const int EInval = 22;
    public const int ENotTy = 25;
    public const int EPipe = 32;
    public const int EProto = 71;
    public const int ETimedOut = 110;

    /// <summary>
    /// The process may not use the node: EACCES from the file mode (no udev rule), EPERM from a device cgroup (a
    /// container that was not given the device).
    /// </summary>
    public static bool IsAccessDenied(int errno) => errno is EAcces or EPerm;

    /// <summary>
    /// The device is gone: the node was removed (ENOENT), its device unplugged (hidraw answers ENODEV to ioctls and
    /// EIO to reads once the device is gone) or no driver serves its number any more (ENXIO).
    /// </summary>
    public static bool IsGone(int errno) => errno is ENoDev or ENoEnt or EIo or ENxIo;

    /// <summary>"EACCES".</summary>
    public static string Name(int errno) => errno switch
    {
        EPerm => "EPERM",
        ENoEnt => "ENOENT",
        EIntr => "EINTR",
        EIo => "EIO",
        ENxIo => "ENXIO",
        EAgain => "EAGAIN",
        EAcces => "EACCES",
        EBusy => "EBUSY",
        ENoDev => "ENODEV",
        EInval => "EINVAL",
        ENotTy => "ENOTTY",
        EPipe => "EPIPE",
        EProto => "EPROTO",
        ETimedOut => "ETIMEDOUT",
        _ => $"errno {errno}",
    };

    /// <summary>"EACCES, permission denied".</summary>
    public static string Describe(int errno)
    {
        string? text = errno switch
        {
            EPerm => "operation not permitted",
            ENoEnt => "no such file or directory",
            EIo => "input/output error",
            ENxIo => "no such device or address",
            EAgain => "no data available yet",
            EAcces => "permission denied",
            EBusy => "device or resource busy",
            ENoDev => "no such device",
            EInval => "invalid argument",
            ENotTy => "not supported by the device",
            EPipe => "the device refused the request",
            EProto => "USB protocol error",
            ETimedOut => "the device did not answer in time",
            _ => null,
        };
        return text is null ? Name(errno) : $"{Name(errno)}, {text}";
    }
}

/// <summary>A failed hidraw call, with the errno that says why.</summary>
internal sealed class HidrawException(int errno, string message) : IOException(message)
{
    public int Errno { get; } = errno;

    public bool IsAccessDenied => NutHub.Hidraw.Errno.IsAccessDenied(Errno);

    public bool IsGone => NutHub.Hidraw.Errno.IsGone(Errno);
}
