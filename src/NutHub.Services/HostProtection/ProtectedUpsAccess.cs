using NutHub.Core.Model;
using NutHub.Core.Runtime;

namespace NutHub.Services.HostProtection;

/// <summary>What the host protection needs from the UPSes; an interface so tests can script every situation.</summary>
internal interface IProtectedUpsAccess
{
    /// <summary>The current snapshot, or null when the registry has no such UPS.</summary>
    UpsSnapshot? GetSnapshot(string name);

    /// <summary>Sets FSD; false when the UPS is unknown.</summary>
    bool SetForcedShutdown(string name, CommandOrigin origin);

    Task<CommandResult> SetVariableAsync(string name, string variable, string value, CommandOrigin origin,
                                         CancellationToken cancellationToken);

    Task<CommandResult> InstantCommandAsync(string name, string command, string? parameter, CommandOrigin origin,
                                            CancellationToken cancellationToken);
}

/// <summary>The UPSes of the registry.</summary>
internal sealed class RegistryUpsAccess(IUpsRegistry registry) : IProtectedUpsAccess
{
    public UpsSnapshot? GetSnapshot(string name) => registry.Find(name)?.Snapshot;

    public bool SetForcedShutdown(string name, CommandOrigin origin)
    {
        if (registry.Find(name) is not { } unit)
        {
            return false;
        }

        unit.SetForcedShutdown(origin);
        return true;
    }

    public Task<CommandResult> SetVariableAsync(string name, string variable, string value, CommandOrigin origin,
                                                CancellationToken cancellationToken) =>
        registry.Find(name) is { } unit
            ? unit.SetVariableAsync(variable, value, origin, cancellationToken)
            : Task.FromResult(new CommandResult(CommandStatus.UnknownUps, $"There is no UPS named {name}."));

    public Task<CommandResult> InstantCommandAsync(string name, string command, string? parameter, CommandOrigin origin,
                                                   CancellationToken cancellationToken) =>
        registry.Find(name) is { } unit
            ? unit.InstantCommandAsync(command, parameter, origin, cancellationToken)
            : Task.FromResult(new CommandResult(CommandStatus.UnknownUps, $"There is no UPS named {name}."));
}
