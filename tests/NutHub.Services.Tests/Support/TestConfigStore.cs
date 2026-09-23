using NutHub.Core.Configuration;
using NutHub.Core.Model;

namespace NutHub.Services.Tests.Support;

/// <summary>An in-memory configuration store: no file, no validation.</summary>
internal sealed class TestConfigStore : IConfigStore
{
    private NutHubConfig _current;

    public TestConfigStore(NutHubConfig? initial = null)
    {
        _current = initial ?? new NutHubConfig();
    }

    public NutHubConfig Current => Volatile.Read(ref _current);

    public event EventHandler<ConfigChangedEventArgs>? Changed;

    public Task<NutHubConfig> UpdateAsync(Action<NutHubConfig> mutate, CommandOrigin origin, string description,
                                          CancellationToken cancellationToken = default)
    {
        NutHubConfig previous = Current;
        NutHubConfig next = NutHubJson.Clone(previous);
        mutate(next);
        Volatile.Write(ref _current, next);
        Changed?.Invoke(this, new ConfigChangedEventArgs(previous, next, origin, description));
        return Task.FromResult(next);
    }

    /// <summary>Synchronous shortcut for tests.</summary>
    public void Update(Action<NutHubConfig> mutate) =>
        UpdateAsync(mutate, CommandOrigin.System, "test").GetAwaiter().GetResult();
}
