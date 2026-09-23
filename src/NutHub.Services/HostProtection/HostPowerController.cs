using Microsoft.Extensions.Logging;
using NutHub.Services.Processes;

namespace NutHub.Services.HostProtection;

/// <summary>The result of trying to shut the machine down.</summary>
internal sealed record ShutdownOutcome(bool Executed, bool Succeeded, string Detail);

/// <summary>
/// Shuts the machine down. The only place NutHub starts a shutdown; tests replace it with a recorder so that no
/// test can ever run a real command.
/// </summary>
internal interface IHostPowerController
{
    /// <summary>Runs the invocations in order until one succeeds.</summary>
    Task<ShutdownOutcome> ShutdownAsync(IReadOnlyList<ShutdownInvocation> invocations, CancellationToken cancellationToken);
}

/// <summary>Runs the shutdown program. Refuses in debug builds and when the safety switch is set, whoever calls it.</summary>
internal sealed class ProcessHostPowerController(ILogger logger) : IHostPowerController
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    public async Task<ShutdownOutcome> ShutdownAsync(IReadOnlyList<ShutdownInvocation> invocations,
                                                     CancellationToken cancellationToken)
    {
        // Last line of defence, independent of the checks of the caller.
        if (ShutdownSafety.FromEnvironment().DisabledReason is { } reason)
        {
            logger.LogCritical("Dry run ({Reason}): the machine would now be shut down with: {Command}", reason,
                               invocations.Count > 0 ? invocations[0].ToString() : "(none)");
            return new ShutdownOutcome(false, false, "Not executed: " + reason);
        }

        string detail = "No shutdown command.";
        foreach (ShutdownInvocation invocation in invocations)
        {
            logger.LogCritical("Shutting down this machine: {Command}", invocation);
            ProcessOutcome outcome = await ProcessRunner
                .RunAsync(invocation.FileName, invocation.Arguments, null, CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
            detail = $"{invocation}: {outcome.Describe(CommandTimeout)}";
            if (outcome.Succeeded)
            {
                return new ShutdownOutcome(true, true, detail);
            }

            logger.LogCritical("The shutdown command failed ({Detail}); trying the next one.", detail);
        }

        return new ShutdownOutcome(true, false, detail);
    }
}
