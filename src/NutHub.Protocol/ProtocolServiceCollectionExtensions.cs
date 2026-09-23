using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NutHub.Core.Abstractions;
using NutHub.Protocol.Server;

namespace NutHub.Protocol;

public static class ProtocolServiceCollectionExtensions
{
    /// <summary>
    /// Registers the NUT protocol server (TCP 3493 by default, the upsd replacement) as a hosted service, and its
    /// state as the <see cref="INutServerStatus"/> the web panel shows (replacing Core's "not available" default).
    /// Needs the services of <c>AddNutHubCore</c>.
    /// </summary>
    public static IServiceCollection AddNutHubProtocol(this IServiceCollection services)
    {
        services.TryAddSingleton<NutProtocolOptions>();
        services.AddSingleton<NutServer>();
        services.AddSingleton<INutServerStatus>(sp => sp.GetRequiredService<NutServer>());
        services.AddHostedService(sp => sp.GetRequiredService<NutServer>());
        return services;
    }
}
