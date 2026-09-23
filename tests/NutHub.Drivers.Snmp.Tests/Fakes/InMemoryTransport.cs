using Lextm.SharpSnmpLib;
using NutHub.Drivers.Snmp.Transport;

namespace NutHub.Drivers.Snmp.Tests.Fakes;

/// <summary>An <see cref="ISnmpTransport"/> answering from an <see cref="AgentStore"/> without any network.</summary>
internal sealed class InMemoryTransport(AgentStore store, bool v1 = false) : ISnmpTransport
{
    public string Target => "fake:161";

    /// <summary>When set, every request fails as if the agent did not answer.</summary>
    public bool Silent { get; set; }

    public Task<SnmpResponse> SendAsync(SnmpRequestKind kind, IList<Variable> variables, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Silent)
        {
            throw new SnmpNoResponseException(Target);
        }

        return Task.FromResult(store.Handle(kind, variables, v1));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
