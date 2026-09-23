namespace NutHub.Hidraw;

/// <summary>
/// What to tell the user when a hidraw node may not be opened. The fix depends on who refused: EPERM comes from a
/// device cgroup (a container or a systemd unit that was not given the device), where running as root does not help;
/// EACCES from the node's file mode, which inside a container is fixed on the host or with the container's user.
/// </summary>
internal static class HidrawAccess
{
    /// <summary>
    /// Whether the process runs in a container: the .NET container images set DOTNET_RUNNING_IN_CONTAINER, Docker and
    /// Podman leave a marker file.
    /// </summary>
    public static bool InContainer { get; } = DetectContainer();

    /// <summary>Whether a reason produced by the hidraw layer ("open() of /dev/hidraw0 returned EPERM") names EPERM.</summary>
    public static bool IsDeviceCgroupDenial(string? reason) =>
        reason is not null && reason.Contains(Errno.Name(Errno.EPerm), StringComparison.Ordinal);

    /// <summary>The advice for a node the device cgroup refuses (EPERM).</summary>
    public static string DeviceCgroupAdvice(string path) =>
        $"The system does not let NutHub use {path}: a container must be given the device (docker run --device " +
        $"{path}, 'devices:' in the compose file, or 'device_cgroup_rules' with the hidraw major number when /dev is " +
        "mounted) and a systemd unit must allow it (DeviceAllow=); running as root does not help.";

    /// <summary>The advice for a node whose file mode refuses the container's user (EACCES in a container).</summary>
    public static string ContainerPermissionAdvice(string path, string udevRule) =>
        $"The container's user may not open {path}: add the node's group with 'group_add' (the node must be group " +
        $"read-write, crw-rw----; one that only root may open, crw-------, needs a udev rule on the host first, such as " +
        $"{udevRule}), or run the container as root.";

    private static bool DetectContainer()
    {
        try
        {
            return string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase) ||
                   File.Exists("/.dockerenv") || File.Exists("/run/.containerenv");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
