using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;

namespace NutHub.Drivers.Net.Nut;

/// <summary>The configuration of one repeated UPS, validated.</summary>
internal sealed record NutUpstreamSettings
{
    public required NutClientOptions Client { get; init; }

    /// <summary>The name of the UPS on the upstream server (the "ups" of "ups@host").</summary>
    public required string RemoteUps { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    /// <summary>Send LOGIN so the upstream upsd counts this server among the systems powered by the UPS.</summary>
    public bool Login { get; init; }

    public bool HasCredentials => Username is not null && Password is not null;
}

/// <summary>
/// "nut": a UPS served by another NUT server (upsd, a Synology or QNAP NAS, another NutHub), repeated here the way
/// NUT's dummy-ups does in repeater mode ("ups@host"). Variables, writable variables and instant commands are
/// those of the upstream UPS; commands and writes are forwarded with the configured credentials.
/// </summary>
public sealed class NutUpstreamDriverFactory : IUpsDriverFactory
{
    public string Id => "nut";

    public string DisplayName => "NUT server (upsd)";

    public string Description =>
        "A UPS of another NUT server: upsd, a Synology or QNAP NAS, another NutHub. Its variables and commands are " +
        "repeated here, like dummy-ups with ups@host.";

    public DriverPlatforms Platforms => DriverPlatforms.All;

    /// <summary>A NUT server cannot be found without knowing its address, and scanning networks is not wanted.</summary>
    public bool SupportsDiscovery => false;

    public IReadOnlyList<DriverOption> Options { get; } =
    [
        new()
        {
            Key = "host", Label = "Server", Type = DriverOptionType.Host, Required = true,
            Help = "Host name or IP address of the NUT server.",
        },
        new() { Key = "port", Label = "Port", Type = DriverOptionType.Port, Default = "3493" },
        new()
        {
            Key = "upsName", Label = "UPS name on that server", Required = true,
            Help = "The name before the @ in ups@host, as listed by upsc -l on that server.",
        },
        new()
        {
            Key = "username", Label = "Username",
            Help = "A user of that server's upsd.users. Needed only for instant commands, variable writes and login.",
        },
        new()
        {
            Key = "password", Label = "Password", Type = DriverOptionType.Secret,
            Help = "Travels in clear text unless TLS is on, as with every NUT client.",
        },
        new()
        {
            Key = "useTls", Label = "Use TLS (STARTTLS)", Type = DriverOptionType.Boolean, Default = "false",
            Help = "Encrypts the connection. The server must have a certificate configured (CERTFILE in upsd.conf).",
        },
        new()
        {
            Key = "tlsVerify", Label = "Certificate check", Type = DriverOptionType.Choice, Default = "none",
            Choices =
            [
                new("none", "None (accept self-signed certificates)"),
                new("chain", "Verify the certificate chain and the host name"),
            ],
            VisibleWhen = new("useTls", ["true"]),
        },
        new()
        {
            Key = "login", Label = "Log in as a secondary", Type = DriverOptionType.Boolean, Default = "false",
            Advanced = true,
            Help = "Sends LOGIN so that server counts this machine among the systems powered by the UPS and waits " +
                   "for it before shutting down. Needs a user with the upsmon role.",
        },
        new()
        {
            Key = "timeoutMs", Label = "Timeout (ms)", Type = DriverOptionType.Integer, Default = "5000", Min = 500,
            Max = 60000, Advanced = true,
            Help = "How long to wait for the server to connect or to answer a request.",
        },
    ];

    public Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiscoveredDevice>>([]);

    public IUpsDriver Create(DriverCreateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        NutUpstreamSettings settings = ReadSettings(context.Read);
        return new NutUpstreamDriver(context.UpsName, settings, context.TimeProvider,
                                     context.LoggerFactory.CreateLogger<NutUpstreamDriver>());
    }

    public void ValidateOptions(DriverOptionReader read) => ReadSettings(read);

    internal static NutUpstreamSettings ReadSettings(DriverOptionReader read)
    {
        string host = read.GetRequiredString("host");
        if (host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '@'))
        {
            throw new DriverConfigurationException("The option 'host' must be a host name or an IP address.", "host");
        }

        string remoteUps = read.GetRequiredString("upsName");
        if (remoteUps.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new DriverConfigurationException("The option 'upsName' must not contain spaces.", "upsName");
        }

        string? username = read.GetString("username");
        string? password = read.GetString("password");
        if (username is not null && password is null)
        {
            throw new DriverConfigurationException("A password is needed with the username.", "password");
        }

        if (password is not null && username is null)
        {
            throw new DriverConfigurationException("A username is needed with the password.", "username");
        }

        if ((username ?? string.Empty).Any(char.IsControl) || (password ?? string.Empty).Any(char.IsControl))
        {
            throw new DriverConfigurationException("The username and password must not contain control characters.",
                                                   "username");
        }

        bool login = read.GetBool("login", false);
        if (login && username is null)
        {
            throw new DriverConfigurationException("Logging in needs a username and a password.", "username");
        }

        bool useTls = read.GetBool("useTls", false);
        string verify = read.GetChoice("tlsVerify", "none", "none", "chain");

        return new NutUpstreamSettings
        {
            Client = new NutClientOptions
            {
                Host = host,
                Port = read.GetPort("port", 3493),
                Timeout = TimeSpan.FromMilliseconds(read.GetInt("timeoutMs", 5000, 500, 60000)),
                UseTls = useTls,
                VerifyCertificate = useTls && verify == "chain",
            },
            RemoteUps = remoteUps,
            Username = username,
            Password = password,
            Login = login,
        };
    }
}
