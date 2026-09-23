using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NutHub.Core.Security;

/// <summary>
/// Restricts files and directories that hold secrets to the account running NutHub (plus SYSTEM and the
/// Administrators group on Windows). Failures are ignored: the data is still written, just less protected.
/// </summary>
public static class FilePermissions
{
    public static bool TryRestrictDirectory(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                RestrictWindows(new DirectoryInfo(path));
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException
                                       or InvalidOperationException or SystemException)
        {
            return false;
        }
    }

    public static bool TryRestrictFile(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                RestrictWindows(new FileInfo(path));
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException
                                       or InvalidOperationException or SystemException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(FileSystemInfo info)
    {
        bool isDirectory = info is DirectoryInfo;
        FileSystemSecurity security = isDirectory
            ? ((DirectoryInfo)info).GetAccessControl()
            : ((FileInfo)info).GetAccessControl();

        // Drop inherited entries (Users: read on ProgramData) and any explicit ones, then grant full control to the
        // three principals that legitimately run or administer NutHub.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRuleSpecific(rule);
        }

        var inheritance = isDirectory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;

        var principals = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        SecurityIdentifier? current = WindowsIdentity.GetCurrent().User;
        if (current is not null && !principals.Contains(current))
        {
            principals.Add(current);
        }

        foreach (SecurityIdentifier sid in principals)
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance,
                                                            PropagationFlags.None, AccessControlType.Allow));
        }

        if (isDirectory)
        {
            ((DirectoryInfo)info).SetAccessControl((DirectorySecurity)security);
        }
        else
        {
            ((FileInfo)info).SetAccessControl((FileSecurity)security);
        }
    }
}
