using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NutHub.Core.Abstractions;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Web.Api.Dto;
using NutHub.Web.Auth;

namespace NutHub.Web.Api.Endpoints;

/// <summary>Instant commands, variable writes, FSD and cancelling a pending shutdown (operator).</summary>
internal static class OperateEndpoints
{
    public static void Map(RouteGroupBuilder operate)
    {
        operate.MapPost("/ups/{name}/commands", CommandAsync);
        operate.MapPut("/ups/{name}/variables/{variable}", SetVariableAsync);
        operate.MapPost("/ups/{name}/fsd", (string name, IUpsRegistry registry, HttpContext context) =>
        {
            UpsUnit unit = ReadEndpoints.FindUnit(registry, name);
            unit.SetForcedShutdown(WebIdentity.Origin(context));
            return CommandResultDto.From(new CommandResult(CommandStatus.Success,
                                                           $"Forced shutdown set on {unit.Name}."));
        });
        operate.MapDelete("/ups/{name}/fsd", (string name, IUpsRegistry registry, HttpContext context) =>
        {
            UpsUnit unit = ReadEndpoints.FindUnit(registry, name);
            bool cleared = unit.ClearForcedShutdown(WebIdentity.Origin(context));
            return CommandResultDto.From(new CommandResult(
                CommandStatus.Success,
                cleared ? $"Forced shutdown cleared on {unit.Name}." : $"No forced shutdown was set on {unit.Name}."));
        });
        operate.MapPost("/host-protection/cancel", (IHostProtectionService protection, HttpContext context) =>
            new OkDto(protection.CancelPending(WebIdentity.Origin(context))));
    }

    private static async Task<CommandResultDto> CommandAsync(string name, CommandRequest body, IUpsRegistry registry,
                                                             HttpContext context)
    {
        UpsUnit unit = ReadEndpoints.FindUnit(registry, name);
        string command = body.Command?.Trim() ?? "";
        if (command.Length == 0)
        {
            throw ApiException.Validation("command", "Choose a command.");
        }

        string? parameter = string.IsNullOrWhiteSpace(body.Parameter) ? null : body.Parameter.Trim();
        // The command reaches the device even if the browser leaves: never cancel it half-way.
        CommandResult result = await unit.InstantCommandAsync(command, parameter, WebIdentity.Origin(context),
                                                              CancellationToken.None).ConfigureAwait(false);
        return CommandResultDto.From(result);
    }

    private static async Task<CommandResultDto> SetVariableAsync(string name, string variable, VariableWriteRequest body,
                                                                 IUpsRegistry registry, HttpContext context)
    {
        UpsUnit unit = ReadEndpoints.FindUnit(registry, name);
        if (body.Value is null)
        {
            throw ApiException.Validation("value", "A value is required.");
        }

        CommandResult result = await unit.SetVariableAsync(variable, body.Value, WebIdentity.Origin(context),
                                                           CancellationToken.None).ConfigureAwait(false);
        return CommandResultDto.From(result);
    }
}
