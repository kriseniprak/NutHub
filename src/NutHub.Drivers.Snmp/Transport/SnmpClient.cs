using Lextm.SharpSnmpLib;
using Microsoft.Extensions.Logging;

namespace NutHub.Drivers.Snmp.Transport;

/// <summary>
/// The SNMP operations the driver needs, on top of <see cref="ISnmpTransport"/>: reading many objects with as few
/// requests as the agent allows, walking a table column, and writing one object.
/// </summary>
/// <remarks>
/// GETs are batched. When the agent answers tooBig the batch is halved (and stays smaller for this connection);
/// when an SNMPv1 agent rejects a batch with noSuchName, the object it names is dropped and the rest asked again;
/// other errors, and agents that silently drop multi-object requests, make the client fall back to one object per
/// request. Objects that do not exist are simply absent from the result.
/// </remarks>
internal sealed class SnmpClient
{
    private readonly ISnmpTransport _transport;
    private readonly ILogger _logger;
    private int _batchSize;
    private bool _agentAnswered;
    private bool _batchesConfirmed;

    public SnmpClient(ISnmpTransport transport, int maxObjectsPerRequest, ILogger logger)
    {
        _transport = transport;
        _logger = logger;
        _batchSize = Math.Clamp(maxObjectsPerRequest, 1, 128);
    }

    public string Target => _transport.Target;

    /// <summary>The current number of objects per GET (lowered after tooBig answers).</summary>
    public int BatchSize => _batchSize;

    /// <summary>
    /// Reads the given objects (numeric OIDs without leading dot); the result holds those the agent has, keyed by
    /// the same OID text.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ISnmpData>> GetAsync(IEnumerable<string> oids,
                                                                       CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, ISnmpData>(StringComparer.Ordinal);
        List<string> distinct = oids.Distinct(StringComparer.Ordinal).ToList();
        var pending = new Queue<List<string>>(distinct.Chunk(_batchSize).Select(c => c.ToList()));

        while (pending.TryDequeue(out List<string>? batch))
        {
            SnmpResponse response;
            try
            {
                response = await _transport.SendAsync(SnmpRequestKind.Get, ToVariables(batch), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SnmpNoResponseException) when (batch.Count > 1 && _agentAnswered && !_batchesConfirmed)
            {
                // Some agents drop requests for several objects instead of answering an error. The agent answered
                // single requests before, so ask one object at a time from now on.
                _logger.LogWarning("{Target} does not answer requests for several objects; asking one object at a time.",
                                   Target);
                _batchSize = 1;
                EnqueueSingles(pending, batch);
                continue;
            }

            _agentAnswered = true;
            switch (response.Status)
            {
                case ErrorCode.NoError:
                    if (batch.Count > 1)
                    {
                        _batchesConfirmed = true;
                    }

                    Store(batch, response, results);
                    break;

                case ErrorCode.TooBig when batch.Count > 1:
                    int half = Math.Max(1, batch.Count / 2);
                    if (half < _batchSize)
                    {
                        _batchSize = half;
                        _logger.LogDebug("{Target} answered tooBig; asking {Count} objects per request.", Target, half);
                    }

                    pending.Enqueue(batch.GetRange(0, half));
                    pending.Enqueue(batch.GetRange(half, batch.Count - half));
                    break;

                case ErrorCode.NoSuchName when response.ErrorIndex >= 1 && response.ErrorIndex <= batch.Count:
                    // SNMPv1: the whole request fails because of the object at ErrorIndex. Drop it, ask the rest.
                    batch.RemoveAt(response.ErrorIndex - 1);
                    if (batch.Count > 0)
                    {
                        pending.Enqueue(batch);
                    }

                    break;

                default:
                    if (batch.Count > 1)
                    {
                        // An error we cannot pin on one object: find out object by object.
                        EnqueueSingles(pending, batch);
                    }
                    else
                    {
                        _logger.LogDebug("{Target} answered {Status} for {Oid}; treating it as missing.",
                                         Target, response.Status, batch[0]);
                    }

                    break;
            }
        }

        return results;
    }

    /// <summary>
    /// Reads the rows of a table column with GETNEXT, stopping at the end of the column, after
    /// <paramref name="maxRows"/> rows, or when the agent does not move forward.
    /// </summary>
    public async Task<IReadOnlyList<Variable>> WalkAsync(string root, int maxRows, CancellationToken cancellationToken)
    {
        var rows = new List<Variable>();
        uint[] rootArcs = new ObjectIdentifier(root).ToNumerical();
        var current = new ObjectIdentifier(rootArcs);
        while (rows.Count < maxRows)
        {
            SnmpResponse response = await _transport
                .SendAsync(SnmpRequestKind.GetNext, [new Variable(current)], cancellationToken)
                .ConfigureAwait(false);
            _agentAnswered = true;
            if (response.Status == ErrorCode.NoSuchName)
            {
                break; // SNMPv1 end of the MIB view
            }

            if (response.Status != ErrorCode.NoError)
            {
                throw new SnmpErrorStatusException(response.Status, response.ErrorIndex, root);
            }

            Variable? next = response.Variables.Count > 0 ? response.Variables[0] : null;
            if (next is null || next.Data.TypeCode == SnmpType.EndOfMibView || !IsUnder(next.Id, rootArcs) ||
                next.Id.CompareTo(current) <= 0)
            {
                break;
            }

            rows.Add(next);
            current = next.Id;
        }

        return rows;
    }

    /// <summary>Writes one object; throws <see cref="SnmpErrorStatusException"/> when the agent refuses.</summary>
    public async Task SetAsync(string oid, ISnmpData value, CancellationToken cancellationToken)
    {
        SnmpResponse response = await _transport
            .SendAsync(SnmpRequestKind.Set, [new Variable(new ObjectIdentifier(oid), value)], cancellationToken)
            .ConfigureAwait(false);
        _agentAnswered = true;
        if (response.Status != ErrorCode.NoError)
        {
            throw new SnmpErrorStatusException(response.Status, response.ErrorIndex, oid);
        }
    }

    private static List<Variable> ToVariables(List<string> oids) =>
        oids.Select(o => new Variable(new ObjectIdentifier(o))).ToList();

    private static void EnqueueSingles(Queue<List<string>> pending, List<string> batch)
    {
        foreach (string oid in batch)
        {
            pending.Enqueue([oid]);
        }
    }

    private static void Store(List<string> batch, SnmpResponse response, Dictionary<string, ISnmpData> results)
    {
        var asked = new HashSet<string>(batch, StringComparer.Ordinal);
        foreach (Variable variable in response.Variables)
        {
            string key = SnmpValues.Key(variable.Id);

            // Ignore what we did not ask for (agents that rewrite OIDs) and the "does not exist" markers.
            if (asked.Contains(key) && !SnmpValues.IsMissing(variable.Data))
            {
                results[key] = variable.Data;
            }
        }
    }

    private static bool IsUnder(ObjectIdentifier oid, uint[] root)
    {
        uint[] arcs = oid.ToNumerical();
        return arcs.Length > root.Length && arcs.AsSpan(0, root.Length).SequenceEqual(root);
    }
}
