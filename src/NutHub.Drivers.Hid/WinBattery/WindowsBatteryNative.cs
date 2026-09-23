using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NutHub.Drivers.Hid.WinBattery;

/// <summary>
/// SetupAPI and battery class IOCTLs (winioctl.h / batclass.h / poclass.h), the documented way to enumerate
/// batteries and read them ("Enumerating Battery Devices" in the Windows driver documentation).
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsBatteryNative
{
    /// <summary>GUID_DEVCLASS_BATTERY, also the battery device interface class.</summary>
    public static readonly Guid BatteryClass = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");

    public const uint DigcfPresent = 0x02;
    public const uint DigcfDeviceInterface = 0x10;
    public const int ErrorNoMoreItems = 259;
    public const int ErrorInsufficientBuffer = 122;

    // CTL_CODE(FILE_DEVICE_BATTERY = 0x29, function, METHOD_BUFFERED, FILE_READ_ACCESS)
    public const uint IoctlBatteryQueryTag = 0x294040;
    public const uint IoctlBatteryQueryInformation = 0x294044;
    public const uint IoctlBatteryQueryStatus = 0x29404C;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;

    /// <summary>BATTERY_QUERY_INFORMATION_LEVEL.</summary>
    public enum InformationLevel
    {
        BatteryInformation = 0,
        BatteryGranularityInformation = 1,
        BatteryTemperature = 2,
        BatteryEstimatedTime = 3,
        BatteryDeviceName = 4,
        BatteryManufactureDate = 5,
        BatteryManufactureName = 6,
        BatteryUniqueId = 7,
        BatterySerialNumber = 8,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BatteryQueryInformation
    {
        public uint BatteryTag;
        public InformationLevel Level;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BatteryInformation
    {
        public uint Capabilities;
        public byte Technology;
        public fixed byte Reserved[3];
        public fixed byte Chemistry[4];
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BatteryWaitStatus
    {
        public uint BatteryTag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BatteryStatus
    {
        public uint PowerState;
        public uint Capacity;
        public uint Voltage;
        public int Rate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BatteryManufactureDate
    {
        public byte Day;
        public byte Month;
        public ushort Year;
    }

    /// <summary>The device interface paths of the batteries present now.</summary>
    public static List<string> EnumerateBatteryPaths() => EnumerateBatteryPaths(out _);

    /// <param name="stopError">
    /// The Windows error that ended the enumeration: ERROR_NO_MORE_ITEMS (259) when every battery was seen. Anything
    /// else means the call failed, e.g. a structure size the platform rejects.
    /// </param>
    public static List<string> EnumerateBatteryPaths(out int stopError)
    {
        var paths = new List<string>();
        Guid classGuid = BatteryClass;
        nint set = SetupDiGetClassDevs(ref classGuid, 0, 0, DigcfPresent | DigcfDeviceInterface);
        stopError = 0;
        if (set == -1 || set == 0)
        {
            stopError = Marshal.GetLastPInvokeError();
            return paths;
        }

        try
        {
            // A machine has a handful of batteries at most; the bound only guards against a misbehaving stack.
            for (uint index = 0; index < 64; index++)
            {
                var data = new SpDeviceInterfaceData { CbSize = (uint)sizeof(SpDeviceInterfaceData) };
                if (!SetupDiEnumDeviceInterfaces(set, 0, ref classGuid, index, ref data))
                {
                    stopError = Marshal.GetLastPInvokeError();
                    break; // ERROR_NO_MORE_ITEMS, or a failure that would repeat for the next index
                }

                if (GetInterfacePath(set, ref data) is { } path)
                {
                    paths.Add(path);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return paths;
    }

    public static SafeFileHandle Open(string path) =>
        CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, FileAttributeNormal, 0);

    /// <summary>The current battery tag, 0 when no battery is present in the slot (or the query failed).</summary>
    public static uint QueryTag(SafeFileHandle device)
    {
        uint wait = 0;
        uint tag = 0;
        return DeviceIoControl(device, IoctlBatteryQueryTag, &wait, sizeof(uint), &tag, sizeof(uint), out _, 0) ? tag : 0;
    }

    public static bool TryQueryInformation(SafeFileHandle device, uint tag, out BatteryInformation information)
    {
        information = default;
        var query = new BatteryQueryInformation { BatteryTag = tag, Level = InformationLevel.BatteryInformation };
        fixed (BatteryInformation* output = &information)
        {
            return DeviceIoControl(device, IoctlBatteryQueryInformation, &query, (uint)sizeof(BatteryQueryInformation),
                                   output, (uint)sizeof(BatteryInformation), out _, 0);
        }
    }

    public static string? QueryString(SafeFileHandle device, uint tag, InformationLevel level)
    {
        const int Chars = 256;
        char* buffer = stackalloc char[Chars];
        var query = new BatteryQueryInformation { BatteryTag = tag, Level = level };
        if (!DeviceIoControl(device, IoctlBatteryQueryInformation, &query, (uint)sizeof(BatteryQueryInformation),
                             buffer, Chars * sizeof(char), out uint returned, 0))
        {
            return null;
        }

        var text = new ReadOnlySpan<char>(buffer, (int)Math.Min(returned / sizeof(char), Chars));
        int end = text.IndexOf('\0');
        string value = (end < 0 ? text : text[..end]).ToString().Trim();
        return value.Length == 0 ? null : value;
    }

    public static DateOnly? QueryManufactureDate(SafeFileHandle device, uint tag)
    {
        BatteryManufactureDate date;
        var query = new BatteryQueryInformation { BatteryTag = tag, Level = InformationLevel.BatteryManufactureDate };
        if (!DeviceIoControl(device, IoctlBatteryQueryInformation, &query, (uint)sizeof(BatteryQueryInformation),
                             &date, (uint)sizeof(BatteryManufactureDate), out _, 0))
        {
            return null;
        }

        return date.Year is >= 1980 and <= 9999 && date.Month is >= 1 and <= 12 && date.Day >= 1 &&
               date.Day <= DateTime.DaysInMonth(date.Year, date.Month)
            ? new DateOnly(date.Year, date.Month, date.Day)
            : null;
    }

    public static bool TryQueryEstimatedTime(SafeFileHandle device, uint tag, out uint seconds)
    {
        seconds = 0;
        uint value = 0;
        var query = new BatteryQueryInformation { BatteryTag = tag, Level = InformationLevel.BatteryEstimatedTime };
        bool ok = DeviceIoControl(device, IoctlBatteryQueryInformation, &query, (uint)sizeof(BatteryQueryInformation),
                                  &value, sizeof(uint), out _, 0);
        seconds = value;
        return ok;
    }

    public static bool TryQueryStatus(SafeFileHandle device, uint tag, out BatteryStatus status)
    {
        status = default;
        var wait = new BatteryWaitStatus { BatteryTag = tag, Timeout = 0 };
        fixed (BatteryStatus* output = &status)
        {
            return DeviceIoControl(device, IoctlBatteryQueryStatus, &wait, (uint)sizeof(BatteryWaitStatus),
                                   output, (uint)sizeof(BatteryStatus), out _, 0);
        }
    }

    private static string? GetInterfacePath(nint set, ref SpDeviceInterfaceData data)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref data, null, 0, out uint required, 0);
        if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer || required < 6 || required > 64 * 1024)
        {
            return null;
        }

        void* detail = NativeMemory.AllocZeroed(required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W: a DWORD cbSize then the path; cbSize counts one WCHAR and the
            // structure's own alignment padding (8 on 64-bit, 6 on 32-bit).
            *(uint*)detail = IntPtr.Size == 8 ? 8u : 6u;
            if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, 0))
            {
                return null;
            }

            int maxChars = (int)(required - sizeof(uint)) / sizeof(char);
            var text = new ReadOnlySpan<char>((byte*)detail + sizeof(uint), maxChars);
            int end = text.IndexOf('\0');
            string path = (end < 0 ? text : text[..end]).ToString();
            return path.Length == 0 ? null : path;
        }
        finally
        {
            NativeMemory.Free(detail);
        }
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static partial nint SetupDiGetClassDevs(ref Guid classGuid, nint enumerator, nint hwndParent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(nint deviceInfoSet, nint deviceInfoData, ref Guid interfaceClassGuid,
                                                            uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetail(nint deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
                                                                void* deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
                                                                out uint requiredSize, nint deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
                                                     uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize,
                                                void* outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);
}
