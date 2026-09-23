using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using NutHub.Core.Configuration;
using NutHub.Core.Model;

namespace NutHub.Core.Runtime;

/// <summary>The UPSes NutHub serves, in configuration order.</summary>
public interface IUpsRegistry
{
    IReadOnlyList<UpsUnit> Units { get; }

    /// <summary>Finds a UPS by name, ignoring case (like upsd).</summary>
    UpsUnit? Find(string name);
}

public sealed class UpsRegistry : IUpsRegistry
{
    private readonly EventHub _hub;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<NutServerSettings> _serverSettings;
    private readonly object _lock = new();
    private ImmutableList<UpsUnit> _units = [];

    public UpsRegistry(EventHub hub, TimeProvider time, ILoggerFactory loggerFactory, IConfigStore config)
        : this(hub, time, loggerFactory, () => config.Current.Nut)
    {
    }

    internal UpsRegistry(EventHub hub, TimeProvider time, ILoggerFactory loggerFactory,
                         Func<NutServerSettings> serverSettings)
    {
        _hub = hub;
        _time = time;
        _loggerFactory = loggerFactory;
        _serverSettings = serverSettings;
    }

    public IReadOnlyList<UpsUnit> Units => Volatile.Read(ref _units);

    public UpsUnit? Find(string name)
    {
        foreach (UpsUnit unit in Units)
        {
            if (string.Equals(unit.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return unit;
            }
        }

        return null;
    }

    internal UpsUnit Add(UpsConfig config)
    {
        var unit = new UpsUnit(config, _hub, _time, _loggerFactory.CreateLogger("NutHub.Ups." + config.Name),
                               _serverSettings);
        lock (_lock)
        {
            _units = _units.Add(unit);
        }

        _hub.Publish(new SnapshotChangedMessage(null, unit.Snapshot));
        return unit;
    }

    internal void Remove(UpsUnit unit)
    {
        lock (_lock)
        {
            _units = _units.Remove(unit);
        }

        _hub.Publish(new UpsRemovedMessage(unit.Name));
    }

    /// <summary>Puts the units in the order of the configuration.</summary>
    internal void Reorder(IReadOnlyList<string> names)
    {
        lock (_lock)
        {
            _units = _units
                .OrderBy(u =>
                {
                    int index = -1;
                    for (int i = 0; i < names.Count; i++)
                    {
                        if (string.Equals(names[i], u.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            index = i;
                            break;
                        }
                    }

                    return index < 0 ? int.MaxValue : index;
                })
                .ToImmutableList();
        }
    }

    internal void Tick()
    {
        foreach (UpsUnit unit in Units)
        {
            unit.Tick();
        }
    }
}
