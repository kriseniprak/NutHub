using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;

namespace NutHub.Drivers.Snmp;

/// <summary>
/// The "snmp" driver: UPS network management cards over SNMP v1, v2c and v3, the equivalent of NUT's snmp-ups.
/// </summary>
public sealed class SnmpDriverFactory : IUpsDriverFactory
{
    public string Id => "snmp";

    public string DisplayName => "SNMP network card";

    public string Description =>
        "UPS network management cards: RFC 1628 UPS-MIB, APC PowerNet, Eaton / Powerware, MGE, CyberPower, Socomec, " +
        "Delta, Huawei, Phoenixtec and Tripp Lite, over SNMP v1, v2c or v3.";

    public DriverPlatforms Platforms => DriverPlatforms.All;

    public IReadOnlyList<DriverOption> Options => SnmpOptions.All;

    /// <summary>
    /// No discovery: finding cards would mean scanning networks, which NutHub does not do behind the user's back.
    /// </summary>
    public bool SupportsDiscovery => false;

    public Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiscoveredDevice>>([]);

    public IUpsDriver Create(DriverCreateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        SnmpSettings settings = SnmpOptions.Parse(context.Read);
        ILogger logger = context.LoggerFactory.CreateLogger<SnmpDriver>();
        return new SnmpDriver(settings, logger);
    }

    public void ValidateOptions(DriverOptionReader read) => SnmpOptions.Parse(read);
}
