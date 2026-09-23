using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NutHub.Drivers.Hid.Transport;

/// <summary>
/// USB string descriptors by index through the Windows HID API. UPSes report their battery chemistry, model and
/// firmware as string indexes (iDeviceChemistry, iProduct...), which HidSharp cannot resolve.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsHidStrings
{
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;

    public static unsafe string? GetIndexedString(string devicePath, int index)
    {
        // Access 0 is enough for string requests and works even while another process has the device open.
        using SafeFileHandle handle = CreateFile(devicePath, 0, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        // A USB string descriptor holds at most 126 UTF-16 characters.
        char* buffer = stackalloc char[128];
        new Span<char>(buffer, 128).Clear();
        if (!HidD_GetIndexedString(handle, (uint)index, buffer, 128 * sizeof(char)))
        {
            return null;
        }

        var text = new ReadOnlySpan<char>(buffer, 128);
        int end = text.IndexOf('\0');
        return (end < 0 ? text : text[..end]).ToString();
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
                                                     uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static unsafe partial bool HidD_GetIndexedString(SafeFileHandle device, uint stringIndex, void* buffer, uint bufferLength);
}
