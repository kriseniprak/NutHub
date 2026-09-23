using Lextm.SharpSnmpLib;
using Microsoft.Extensions.Logging;
using NutHub.Drivers.Snmp.Mibs;
using NutHub.Drivers.Snmp.Transport;

namespace NutHub.Drivers.Snmp.Engine;

/// <summary>
/// Chooses the mapping table for an agent the way snmp-ups' load_mib2nut() does: with "auto", the sysObjectID
/// first (a MIB announcing it is confirmed by reading its model object), then probing each MIB's model object in
/// the catalog order, vendor MIBs before RFC 1628; with an explicit name, only that MIB's model object is checked.
/// </summary>
internal static class MibDetector
{
    /// <summary>SNMPv2-MIB sysObjectID.0.</summary>
    public const string SysObjectIdOid = "1.3.6.1.2.1.1.2.0";

    /// <summary>The detected MIB and the sysObjectID the agent announced (null when it has none).</summary>
    public sealed record Result(MibDefinition Mib, string? SysObjectId);

    /// <exception cref="SnmpMibNotFoundException">The agent implements none of the candidate MIBs.</exception>
    public static async Task<Result> DetectAsync(SnmpClient client, string requested, ILogger logger,
                                                 CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, ISnmpData> sys = await client
            .GetAsync([SysObjectIdOid], cancellationToken).ConfigureAwait(false);
        string? sysObjectId = sys.TryGetValue(SysObjectIdOid, out ISnmpData? data) ? ToOidText(data) : null;

        if (!string.Equals(requested, MibCatalog.Auto, StringComparison.OrdinalIgnoreCase))
        {
            MibDefinition mib = MibCatalog.Find(requested)
                                ?? throw new SnmpMibNotFoundException($"Unknown MIB '{requested}'.");
            if (!await AnswersAsync(client, mib.EffectiveProbeOid, cancellationToken).ConfigureAwait(false))
            {
                throw new SnmpMibNotFoundException(
                    $"{client.Target} does not implement the {mib.DisplayName} MIB (no answer for " +
                    $"{mib.EffectiveProbeOid}); choose another MIB or \"auto\".");
            }

            return new Result(mib, sysObjectId);
        }

        if (sysObjectId is not null)
        {
            foreach (MibDefinition mib in MibCatalog.All.Where(m => AnnouncedBy(m, sysObjectId)))
            {
                if (await AnswersAsync(client, mib.EffectiveProbeOid, cancellationToken).ConfigureAwait(false))
                {
                    logger.LogDebug("{Target}: sysObjectID {SysObjectId} selects the {Mib} MIB.",
                                    client.Target, sysObjectId, mib.Name);
                    return new Result(mib, sysObjectId);
                }
            }
        }

        // Classic detection: every probe object in one batched request, the first MIB in catalog order wins.
        List<MibDefinition> candidates = MibCatalog.All.Where(m => m.Probeable).ToList();
        IReadOnlyDictionary<string, ISnmpData> probes = await client
            .GetAsync(candidates.Select(m => m.EffectiveProbeOid), cancellationToken).ConfigureAwait(false);
        foreach (MibDefinition mib in candidates)
        {
            if (probes.ContainsKey(mib.EffectiveProbeOid))
            {
                logger.LogDebug("{Target}: the {Mib} MIB answers (sysObjectID {SysObjectId}).",
                                client.Target, mib.Name, sysObjectId ?? "none");
                return new Result(mib, sysObjectId);
            }
        }

        throw new SnmpMibNotFoundException(
            $"{client.Target} answers SNMP but implements none of the supported UPS MIBs " +
            $"(sysObjectID {sysObjectId ?? "not available"}). Is it a UPS network card?");
    }

    /// <summary>Whether the sysObjectID is one of the MIB's, or below one of them.</summary>
    public static bool AnnouncedBy(MibDefinition mib, string sysObjectId)
    {
        string id = Mib.Normalize(sysObjectId);
        foreach (string announced in mib.SysObjectIds)
        {
            string prefix = Mib.Normalize(announced);
            if (id == prefix || id.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> AnswersAsync(SnmpClient client, string oid, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, ISnmpData> result = await client.GetAsync([oid], cancellationToken)
            .ConfigureAwait(false);
        return result.ContainsKey(oid);
    }

    /// <summary>The sysObjectID as dotted text; some agents send it as a string (NUT accepts that too).</summary>
    private static string? ToOidText(ISnmpData data) => data switch
    {
        ObjectIdentifier oid => SnmpValues.Key(oid),
        OctetString s when SnmpValues.ToText(s) is { Length: > 0 } text && text.TrimStart('.').All(c => c == '.' || char.IsAsciiDigit(c)) =>
            text.TrimStart('.'),
        _ => null,
    };
}
