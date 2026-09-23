using NutHub.Core.Security;

namespace NutHub.Core;

/// <summary>
/// Where NutHub keeps its configuration and data. Resolved once at startup from, in order of precedence, the
/// command line, the NUTHUB_DATA_DIR / NUTHUB_CONFIG environment variables and the operating system defaults.
/// </summary>
public sealed class NutHubPaths
{
    public const string DataDirEnvironmentVariable = "NUTHUB_DATA_DIR";
    public const string ConfigEnvironmentVariable = "NUTHUB_CONFIG";

    public NutHubPaths(string dataDirectory, string? configFile = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        ConfigFile = Path.GetFullPath(configFile ?? Path.Combine(DataDirectory, "nuthub.json"));
    }

    /// <summary>Databases, logs, certificates and keys.</summary>
    public string DataDirectory { get; }

    /// <summary>The JSON configuration file.</summary>
    public string ConfigFile { get; }

    public string ConfigDirectory => Path.GetDirectoryName(ConfigFile)!;

    public string DatabaseFile => Path.Combine(DataDirectory, "nuthub.db");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public string CertificateDirectory => Path.Combine(DataDirectory, "certs");

    /// <summary>The key that encrypts secrets (SMTP password, SNMP communities...) inside the configuration.</summary>
    public string SecretKeyFile => Path.Combine(DataDirectory, "secret.key");

    /// <summary>Written on first start with the generated password of the "admin" web account.</summary>
    public string InitialPasswordFile => Path.Combine(DataDirectory, "initial-admin-password.txt");

    /// <summary>
    /// Resolves the paths for this process.
    /// </summary>
    /// <param name="dataDirectory">An explicit data directory (command line), or null.</param>
    /// <param name="configFile">An explicit configuration file (command line), or null.</param>
    public static NutHubPaths Resolve(string? dataDirectory = null, string? configFile = null)
    {
        dataDirectory ??= NullIfEmpty(Environment.GetEnvironmentVariable(DataDirEnvironmentVariable));
        configFile ??= NullIfEmpty(Environment.GetEnvironmentVariable(ConfigEnvironmentVariable));

        if (dataDirectory is not null)
        {
            return new NutHubPaths(dataDirectory, configFile);
        }

        if (OperatingSystem.IsWindows())
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return new NutHubPaths(Path.Combine(programData, "NutHub"), configFile);
        }

        if (OperatingSystem.IsMacOS())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new NutHubPaths(Path.Combine(home, "Library", "Application Support", "NutHub"), configFile);
        }

        // Linux and other Unix systems: the FHS locations when running as a system service (root), the XDG ones
        // for an ordinary user trying it out.
        if (Environment.IsPrivilegedProcess)
        {
            return new NutHubPaths("/var/lib/nuthub", configFile ?? "/etc/nuthub/nuthub.json");
        }

        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dataHome = NullIfEmpty(Environment.GetEnvironmentVariable("XDG_DATA_HOME"))
                          ?? Path.Combine(homeDir, ".local", "share");
        string configHome = NullIfEmpty(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"))
                            ?? Path.Combine(homeDir, ".config");
        return new NutHubPaths(Path.Combine(dataHome, "nuthub"),
                               configFile ?? Path.Combine(configHome, "nuthub", "nuthub.json"));
    }

    /// <summary>
    /// Creates the directories if needed and restricts them to the account running NutHub (and administrators),
    /// since they hold password hashes and the secret key.
    /// </summary>
    public void EnsureCreated()
    {
        foreach (string dir in new[] { DataDirectory, ConfigDirectory, LogDirectory, CertificateDirectory })
        {
            bool existed = Directory.Exists(dir);
            Directory.CreateDirectory(dir);
            if (!existed)
            {
                FilePermissions.TryRestrictDirectory(dir);
            }
        }
    }

    public override string ToString() => $"data={DataDirectory}, config={ConfigFile}";

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
