using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Drivers.Net.Common;

namespace NutHub.Drivers.Net.Apcupsd;

/// <summary>
/// "apcupsd": a UPS managed by apcupsd (typically an APC unit on a machine where apcupsd already runs), read through
/// its network information server on TCP 3551, like NUT's apcupsd-ups driver.
/// </summary>
public sealed class ApcupsdDriverFactory : IUpsDriverFactory
{
    private readonly ILogger _logger;

    public ApcupsdDriverFactory(ILogger<ApcupsdDriverFactory>? logger = null)
    {
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public string Id => "apcupsd";

    public string DisplayName => "apcupsd";

    public string Description =>
        "A UPS managed by apcupsd, read through its network information server (NIS, TCP port 3551). Read-only.";

    public DriverPlatforms Platforms => DriverPlatforms.All;

    public bool SupportsDiscovery => true;

    /// <summary>Where discovery looks; a test points it at a local fake server.</summary>
    internal string DiscoveryHost { get; init; } = "127.0.0.1";

    internal int DiscoveryPort { get; init; } = 3551;

    /// <summary>Discovery must answer quickly even when nothing listens.</summary>
    internal TimeSpan DiscoveryTimeout { get; init; } = TimeSpan.FromSeconds(1);

    internal TimeProvider DiscoveryTimeProvider { get; init; } = TimeProvider.System;

    public IReadOnlyList<DriverOption> Options { get; } =
    [
        new()
        {
            Key = "host", Label = "apcupsd host", Type = DriverOptionType.Host, Default = "127.0.0.1",
            Help = "The machine where apcupsd runs, with NETSERVER on in apcupsd.conf.",
        },
        new() { Key = "port", Label = "Port", Type = DriverOptionType.Port, Default = "3551" },
        new()
        {
            Key = "timeoutMs", Label = "Timeout (ms)", Type = DriverOptionType.Integer, Default = "5000", Min = 500,
            Max = 60000, Advanced = true,
            Help = "How long to wait for apcupsd to connect and send its status.",
        },
    ];

    public IUpsDriver Create(DriverCreateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        DriverOptionReader read = context.Read;
        string host = read.GetString("host", "127.0.0.1")!;
        if (host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '@'))
        {
            throw new DriverConfigurationException("The option 'host' must be a host name or an IP address.", "host");
        }

        int port = read.GetPort("port", 3551);
        TimeSpan timeout = TimeSpan.FromMilliseconds(read.GetInt("timeoutMs", 5000, 500, 60000));
        var client = new ApcupsdNisClient(host, port, timeout, context.TimeProvider);
        return new ApcupsdDriver(context.UpsName, client, context.TimeProvider,
                                 context.LoggerFactory.CreateLogger<ApcupsdDriver>());
    }

    /// <summary>Looks for apcupsd on this machine (127.0.0.1:3551), the usual place for it.</summary>
    public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var client = new ApcupsdNisClient(DiscoveryHost, DiscoveryPort, DiscoveryTimeout, DiscoveryTimeProvider);
        try
        {
            IReadOnlyList<string> lines = await client.FetchStatusAsync(cancellationToken).ConfigureAwait(false);
            ApcupsdReading reading = ApcupsdStatusMapper.ToReading(ApcupsdStatusMapper.ParseFields(lines));
            if (lines.Count == 0)
            {
                return [];
            }

            string model = reading.Variables.GetValueOrDefault("ups.model") ?? "UPS";
            string? serial = reading.Variables.GetValueOrDefault("ups.serial");
            var detail = new StringBuilder();
            if (serial is not null)
            {
                detail.Append("Serial ").Append(serial);
            }

            if (reading.Version is not null)
            {
                detail.Append(detail.Length > 0 ? ", " : string.Empty).Append("apcupsd ").Append(reading.Version);
            }

            var options = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["host"] = DiscoveryHost,
                ["port"] = DiscoveryPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            return
            [
                new DiscoveredDevice($"{model} (apcupsd on {client.Endpoint})",
                                     detail.Length > 0 ? detail.ToString() : null, options,
                                     SuggestName(reading.UpsName)),
            ];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (NetworkErrors.IsTransient(ex) || ex is OperationCanceledException)
        {
            _logger.LogDebug("No apcupsd found on {Endpoint}: {Error}", client.Endpoint, NetworkErrors.Describe(ex));
            return [];
        }
    }

    /// <summary>A NutHub UPS name from apcupsd's UPSNAME: letters, digits, '.', '_' and '-' only.</summary>
    internal static string SuggestName(string? upsName)
    {
        var sb = new StringBuilder();
        foreach (char c in (upsName ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            {
                sb.Append(c);
            }
            else if (char.IsWhiteSpace(c) && sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }

            if (sb.Length == 64)
            {
                break;
            }
        }

        string name = sb.ToString().Trim('-', '.', '_');
        return name.Length > 0 && char.IsAsciiLetterOrDigit(name[0]) ? name : "apcupsd";
    }
}
