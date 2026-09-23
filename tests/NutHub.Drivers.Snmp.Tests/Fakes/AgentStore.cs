using Lextm.SharpSnmpLib;
using NutHub.Drivers.Snmp.Transport;

namespace NutHub.Drivers.Snmp.Tests.Fakes;

/// <summary>
/// The objects of a fake SNMP agent and the GET / GETNEXT / SET semantics of SNMPv1 (one missing object fails the
/// whole request with noSuchName) and SNMPv2c/v3 (per-object exceptions). Shared by the in-memory transport and the
/// UDP agent, so both behave the same.
/// </summary>
internal sealed class AgentStore
{
    private readonly object _lock = new();
    private readonly SortedDictionary<ObjectIdentifier, ISnmpData> _values = new();
    private readonly HashSet<string> _readOnly = new(StringComparer.Ordinal);
    private readonly List<(string Oid, ISnmpData Value)> _sets = [];
    private readonly List<int> _requestSizes = [];

    /// <summary>Requests with more objects than this are answered tooBig; 0 means no limit.</summary>
    public int MaxObjectsPerRequest { get; set; }

    /// <summary>Whether SETs are refused for every object (a read-only community).</summary>
    public bool RefuseSets { get; set; }

    public void Set(string oid, ISnmpData value)
    {
        lock (_lock)
        {
            _values[new ObjectIdentifier(oid.TrimStart('.'))] = value;
        }
    }

    public void Set(string oid, int value) => Set(oid, new Integer32(value));

    public void Set(string oid, string value) => Set(oid, new OctetString(value));

    public void SetOid(string oid, string value) => Set(oid, new ObjectIdentifier(value.TrimStart('.')));

    public void SetTicks(string oid, uint hundredths) => Set(oid, new TimeTicks(hundredths));

    public void Remove(string oid)
    {
        lock (_lock)
        {
            _values.Remove(new ObjectIdentifier(oid.TrimStart('.')));
        }
    }

    public void MakeReadOnly(string oid)
    {
        lock (_lock)
        {
            _readOnly.Add(oid.TrimStart('.'));
        }
    }

    public ISnmpData? Get(string oid)
    {
        lock (_lock)
        {
            return _values.GetValueOrDefault(new ObjectIdentifier(oid.TrimStart('.')));
        }
    }

    /// <summary>The SETs received, in order.</summary>
    public IReadOnlyList<(string Oid, ISnmpData Value)> Sets
    {
        get
        {
            lock (_lock)
            {
                return _sets.ToArray();
            }
        }
    }

    /// <summary>The number of objects of each request received (GET, GETNEXT and SET).</summary>
    public IReadOnlyList<int> RequestSizes
    {
        get
        {
            lock (_lock)
            {
                return _requestSizes.ToArray();
            }
        }
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            _sets.Clear();
            _requestSizes.Clear();
        }
    }

    /// <summary>Answers a request the way an agent of the given version would.</summary>
    public SnmpResponse Handle(SnmpRequestKind kind, IList<Variable> variables, bool v1)
    {
        lock (_lock)
        {
            _requestSizes.Add(variables.Count);
            if (MaxObjectsPerRequest > 0 && variables.Count > MaxObjectsPerRequest)
            {
                return new SnmpResponse(ErrorCode.TooBig, 0, []);
            }

            var answer = new List<Variable>(variables.Count);
            for (int i = 0; i < variables.Count; i++)
            {
                ObjectIdentifier id = variables[i].Id;
                switch (kind)
                {
                    case SnmpRequestKind.Get:
                        if (_values.TryGetValue(id, out ISnmpData? value))
                        {
                            answer.Add(new Variable(id, value));
                        }
                        else if (v1)
                        {
                            return Fail(ErrorCode.NoSuchName, i, variables);
                        }
                        else
                        {
                            answer.Add(new Variable(id, new NoSuchObject()));
                        }

                        break;

                    case SnmpRequestKind.GetNext:
                        KeyValuePair<ObjectIdentifier, ISnmpData>? next = _values.FirstOrDefault(p => p.Key.CompareTo(id) > 0);
                        if (next is { Key: not null } found)
                        {
                            answer.Add(new Variable(found.Key, found.Value));
                        }
                        else if (v1)
                        {
                            return Fail(ErrorCode.NoSuchName, i, variables);
                        }
                        else
                        {
                            answer.Add(new Variable(id, new EndOfMibView()));
                        }

                        break;

                    default:
                        string key = SnmpValues.Key(id);
                        if (RefuseSets || _readOnly.Contains(key))
                        {
                            return Fail(v1 ? ErrorCode.NoSuchName : ErrorCode.NotWritable, i, variables);
                        }

                        answer.Add(variables[i]);
                        break;
                }
            }

            if (kind == SnmpRequestKind.Set)
            {
                foreach (Variable v in variables)
                {
                    _values[v.Id] = v.Data;
                    _sets.Add((SnmpValues.Key(v.Id), v.Data));
                }
            }

            return new SnmpResponse(ErrorCode.NoError, 0, answer);
        }
    }

    private static SnmpResponse Fail(ErrorCode code, int index, IList<Variable> variables) =>
        new(code, index + 1, variables.ToArray());
}
