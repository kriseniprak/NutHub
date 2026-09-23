using NutHub.Core;
using NutHub.ServiceSetup;

namespace NutHub.Cli;

/// <summary>
/// On Linux the service runs as the "nuthub" account and its files are private (0600). A tool run as root that
/// rewrites the configuration would leave it owned by root, unreadable by the service: this hands the files back.
/// </summary>
internal static class ServiceAccountFiles
{
    public const string Account = "nuthub";

    /// <summary>
    /// Gives the data and configuration directories back to the service account when running as root and that
    /// account exists, then touches the configuration file so a running server (which may have tried to read it
    /// while it was still owned by root) reloads it.
    /// </summary>
    public static void RestoreOwnership(NutHubPaths paths)
    {
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess)
        {
            return;
        }

        if (ProcessRunner.Run("getent", ["passwd", Account]).ExitCode != 0)
        {
            return; // NutHub runs as root here: nothing to hand back.
        }

        var directories = new[] { paths.DataDirectory, paths.ConfigDirectory }
            .Distinct(StringComparer.Ordinal)
            .Where(IsDedicated)
            .ToList();
        if (directories.Count > 0)
        {
            ProcessResult result = ProcessRunner.Run("chown", ["-R", $"{Account}:{Account}", .. directories]);
            if (result.ExitCode != 0)
            {
                Console.Error.WriteLine($"nuthub: warning: could not give {string.Join(", ", directories)} back to " +
                                        $"the {Account} account: {result.Output.Trim()}");
            }
        }

        try
        {
            File.SetLastWriteTimeUtc(paths.ConfigFile, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The server still has the change on its next start.
        }
    }

    /// <summary>Only directories that are NutHub's own are changed recursively (never "/etc" itself).</summary>
    internal static bool IsDedicated(string directory) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(directory))
            .Contains("nuthub", StringComparison.OrdinalIgnoreCase);
}
