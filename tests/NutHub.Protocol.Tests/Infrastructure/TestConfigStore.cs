using NutHub.Core.Configuration;
using NutHub.Core.Model;

namespace NutHub.Protocol.Tests.Infrastructure;

/// <summary>
/// An in-memory configuration store. It does not validate, so tests can listen on port 0 (an ephemeral port).
/// </summary>
internal sealed class TestConfigStore(NutHubConfig initial) : IConfigStore
{
    private readonly object _lock = new();
    private NutHubConfig _current = initial;

    public NutHubConfig Current => Volatile.Read(ref _current);

    public event EventHandler<ConfigChangedEventArgs>? Changed;

    public Task<NutHubConfig> UpdateAsync(Action<NutHubConfig> mutate, CommandOrigin origin, string description,
                                          CancellationToken cancellationToken = default)
    {
        NutHubConfig previous;
        NutHubConfig next;
        lock (_lock)
        {
            previous = _current;
            next = NutHubJson.Clone(previous);
            mutate(next);
            Volatile.Write(ref _current, next);
        }

        Changed?.Invoke(this, new ConfigChangedEventArgs(previous, next, origin, description));
        return Task.FromResult(next);
    }

    public Task<NutHubConfig> UpdateAsync(Action<NutHubConfig> mutate) =>
        UpdateAsync(mutate, CommandOrigin.System, "test change");
}
