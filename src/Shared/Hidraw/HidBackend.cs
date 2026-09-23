namespace NutHub.Hidraw;

/// <summary>The HID layers the USB drivers can use.</summary>
internal enum HidBackendKind
{
    /// <summary>The HidSharp library: the Windows HID API, IOKit on macOS, libudev and hidraw on Linux.</summary>
    HidSharp,

    /// <summary>The Linux hidraw interface used directly (<see cref="HidrawEnumerator"/>, <see cref="HidrawHandle"/>).</summary>
    Hidraw,
}

/// <summary>
/// Chooses the HID layer of the usbhid driver and of the megatec USB-serial bridges. On Linux NutHub talks to
/// /dev/hidraw* itself: that needs neither libudev nor the udev database in /run/udev, so it also works in containers
/// and minimal systems, where HidSharp (which enumerates through libudev) can find nothing and say nothing. Windows
/// and macOS keep HidSharp.
/// <para>
/// The environment variable NUTHUB_HID_BACKEND=hidsharp brings HidSharp back on Linux, to compare the two layers or
/// to get around a problem of the native one; "hidraw" or no value keeps the default. Other systems ignore it.
/// </para>
/// </summary>
internal static class HidBackend
{
    public const string EnvironmentVariable = "NUTHUB_HID_BACKEND";

    /// <summary>The layer for this process, from the operating system and <see cref="EnvironmentVariable"/>.</summary>
    public static HidBackendKind Current => Select(OperatingSystem.IsLinux(), Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static HidBackendKind Select(bool isLinux, string? setting) =>
        isLinux && !string.Equals(setting?.Trim(), "hidsharp", StringComparison.OrdinalIgnoreCase)
            ? HidBackendKind.Hidraw
            : HidBackendKind.HidSharp;
}
