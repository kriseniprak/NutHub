using Microsoft.Extensions.Logging;
using NutHub.Core.Configuration;
using NutHub.Services.Notifications.Webhooks;
using NutHub.Services.Processes;

namespace NutHub.Services.Notifications.Commands;

/// <summary>
/// Runs a command hook (like upsmon NOTIFYCMD): the program with its arguments (placeholders substituted in each
/// argument after splitting, so a value can never add arguments), the details of the event in environment
/// variables, and a time limit after which the whole process tree is killed.
/// </summary>
internal sealed class CommandHookRunner(ILogger logger)
{
    public async Task<DeliveryResult> RunAsync(CommandHookSettings hook, NotificationContext context,
                                               CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hook.Command))
        {
            return DeliveryResult.Fail("No program is configured.");
        }

        IReadOnlyDictionary<string, string> values = context.PlaceholderValues();
        List<string> arguments = BuildArguments(hook, values);
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(hook.TimeoutSeconds, 1, 3600));
        string program = hook.Command.Trim().Trim('"');

        ProcessOutcome outcome = await ProcessRunner
            .RunAsync(program, arguments, BuildEnvironment(context, values), timeout, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Succeeded)
        {
            logger.LogDebug("Command hook {Name} finished for {Type}.", DisplayName(hook), context.TypeName);
            return DeliveryResult.Ok;
        }

        string error = outcome.Describe(timeout);
        logger.LogWarning("Command hook {Name} failed for {Type}: {Error}", DisplayName(hook), context.TypeName, error);
        return DeliveryResult.Fail(error);
    }

    public static string DisplayName(CommandHookSettings hook) =>
        !string.IsNullOrWhiteSpace(hook.Name) ? hook.Name : Path.GetFileName(hook.Command.Trim().Trim('"'));

    internal static List<string> BuildArguments(CommandHookSettings hook, IReadOnlyDictionary<string, string> values) =>
        CommandLine.Split(hook.Arguments)
            .Select(a => Placeholders.Apply(a, values, PlaceholderEscaping.None))
            .ToList();

    /// <summary>The NUTHUB_* variables plus upsmon's NOTIFYTYPE and UPSNAME.</summary>
    internal static Dictionary<string, string> BuildEnvironment(NotificationContext context,
                                                                IReadOnlyDictionary<string, string> values) =>
        new(StringComparer.Ordinal)
        {
            ["NUTHUB_EVENT"] = values["type"],
            ["NUTHUB_SEVERITY"] = values["severity"],
            ["NUTHUB_CATEGORY"] = values["category"],
            ["NUTHUB_UPS"] = values["ups"],
            ["NUTHUB_MESSAGE"] = values["message"],
            ["NUTHUB_TIMESTAMP"] = values["timestamp"],
            ["NUTHUB_SERVER"] = values["server"],
            ["NUTHUB_STATUS"] = values["status"],
            ["NUTHUB_CHARGE"] = values["charge"],
            ["NUTHUB_RUNTIME"] = values["runtime"],
            ["NUTHUB_LOAD"] = values["load"],
            ["NUTHUB_ACTOR"] = values["actor"],
            ["NUTHUB_TEST"] = context.IsTest ? "1" : "0",
            ["NOTIFYTYPE"] = values["notifytype"],
            ["UPSNAME"] = values["ups"],
        };
}
