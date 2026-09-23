using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using static NutHub.Drivers.Hid.WinBattery.WindowsBatteryNative;

namespace NutHub.Drivers.Hid.WinBattery;

/// <summary>The batteries Windows knows, read through SetupAPI and the battery class IOCTLs.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsBatterySource(ILogger logger) : IBatterySource
{
    public IReadOnlyList<BatteryDevice> GetBatteries()
    {
        var result = new List<BatteryDevice>();
        List<string> paths;
        try
        {
            paths = EnumerateBatteryPaths();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Listing the batteries failed.");
            return result;
        }

        foreach (string path in paths)
        {
            try
            {
                using SafeFileHandle handle = WindowsBatteryNative.Open(path);
                if (handle.IsInvalid)
                {
                    logger.LogDebug("Cannot open the battery {Path} (error {Error}).", path, Marshal.GetLastPInvokeError());
                    continue;
                }

                uint tag = QueryTag(handle);
                if (tag == 0 || !TryQueryInformation(handle, tag, out BatteryInformation info))
                {
                    continue; // an empty battery slot
                }

                result.Add(new BatteryDevice(
                    path,
                    QueryString(handle, tag, InformationLevel.BatteryDeviceName),
                    QueryString(handle, tag, InformationLevel.BatteryManufactureName),
                    QueryString(handle, tag, InformationLevel.BatterySerialNumber),
                    QueryString(handle, tag, InformationLevel.BatteryUniqueId),
                    info.Capabilities,
                    Chemistry(info),
                    info.DesignedCapacity,
                    info.FullChargedCapacity,
                    QueryManufactureDate(handle, tag)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogDebug(ex, "Skipping the battery {Path}.", path);
            }
        }

        return result;
    }

    public IBatteryHandle Open(BatteryDevice battery)
    {
        ArgumentNullException.ThrowIfNull(battery);
        SafeFileHandle handle = WindowsBatteryNative.Open(battery.Path);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new BatteryLostException($"Cannot open the battery {battery.Description} (Windows error {error}).");
        }

        uint tag = QueryTag(handle);
        if (tag == 0)
        {
            handle.Dispose();
            throw new BatteryLostException($"The battery {battery.Description} is no longer present.");
        }

        return new Handle(handle, tag, battery.Description);
    }

    private static unsafe string? Chemistry(BatteryInformation info)
    {
        var sb = new StringBuilder(4);
        for (int i = 0; i < 4; i++)
        {
            char c = (char)info.Chemistry[i];
            if (c == '\0')
            {
                break;
            }

            if (c is < ' ' or > '~')
            {
                return null;
            }

            sb.Append(c);
        }

        string text = sb.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// An open battery. The tag changes whenever the battery is removed and put back (or the UPS driver restarts),
    /// so a failed query is retried once with a fresh tag before the battery counts as gone.
    /// </summary>
    private sealed class Handle(SafeFileHandle handle, uint tag, string description) : IBatteryHandle
    {
        private uint _tag = tag;

        public BatteryReading Read()
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            if (!TryQueryStatus(handle, _tag, out BatteryStatus status))
            {
                int error = Marshal.GetLastPInvokeError();
                uint fresh = QueryTag(handle);
                if (fresh == 0 || !TryQueryStatus(handle, fresh, out status))
                {
                    throw new BatteryLostException($"The battery {description} stopped answering (Windows error {error}).");
                }

                _tag = fresh;
            }

            uint seconds = TryQueryEstimatedTime(handle, _tag, out uint estimate) ? estimate : BatteryReading.Unknown;
            return new BatteryReading(status.PowerState, status.Capacity, status.Voltage, status.Rate, seconds);
        }

        public void Dispose() => handle.Dispose();
    }
}
