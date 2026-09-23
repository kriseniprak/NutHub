using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NutHub.Core;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Logging;
using NutHub.Core.Runtime;
using NutHub.Core.Security;
using NutHub.Web.Admin;
using NutHub.Web.Api;
using NutHub.Web.Api.Endpoints;
using NutHub.Web.Auth;
using NutHub.Web.Import;
using NutHub.Web.Middleware;

namespace NutHub.Web.Hosting;

/// <summary>
/// Builds one instance of the panel: a slim WebApplication with its own container, into which the NutHub services
/// it needs are forwarded from the application container, so both share one state and one set of log providers.
/// </summary>
internal static class WebPanelApp
{
    /// <param name="outer">The NutHub application services.</param>
    /// <param name="binding">The listeners.</param>
    /// <param name="certificate">The HTTPS certificate when <see cref="PanelBinding.HttpsEnabled"/>; owned by the caller.</param>
    /// <param name="configure">Last-minute changes, e.g. a test server instead of Kestrel.</param>
    public static WebApplication Build(IServiceProvider outer, PanelBinding binding, X509Certificate2? certificate,
                                       Action<WebApplicationBuilder>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(WebPanelApp).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });

        // Everything comes from NutHub's configuration: an appsettings.json or ASPNETCORE_* variable must not add
        // Kestrel endpoints or change the panel behind its back.
        builder.Configuration.Sources.Clear();
        IServiceCollection services = builder.Services;
        builder.Logging.ClearProviders();
        Forward(services, outer);
        services.AddSingleton(binding);

        // The panel lives and dies with NutHub's host: it must not react to Ctrl+C / SIGTERM on its own.
        services.AddSingleton<IHostLifetime, NoopHostLifetime>();
        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        services.ConfigureHttpJsonOptions(o => ApiJson.Apply(o.SerializerOptions));
        services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        services.AddPanelAuthentication(outer.GetRequiredService<NutHubPaths>());
        services.AddSingleton<ApiViews>();
        services.AddSingleton<ConfigWriter>();
        services.AddSingleton<UpsConfigMapper>();
        services.AddSingleton<NutImporter>();

        builder.WebHost.UseKestrelHttpsConfiguration();
        KestrelSetup.Configure(builder.WebHost, binding, certificate);
        configure?.Invoke(builder);

        WebApplication app = builder.Build();
        ConfigurePipeline(app);
        return app;
    }

    private static void Forward(IServiceCollection services, IServiceProvider outer)
    {
        // Registered as instances, never through factories: the inner container must not dispose shared services.
        void Add<T>() where T : class => services.AddSingleton(outer.GetRequiredService<T>());

        Add<IConfigStore>();
        Add<IUpsRegistry>();
        Add<IDriverManager>();
        Add<IDriverCatalog>();
        Add<EventHub>();
        Add<NutSessionRegistry>();
        Add<IEventStore>();
        Add<IHistoryStore>();
        Add<INotificationService>();
        Add<IHostProtectionService>();
        Add<INutServerStatus>();
        Add<InMemoryLogSink>();
        Add<IPasswordHasher>();
        Add<ISecretProtector>();
        Add<NutHubPaths>();
        TimeProvider time = outer.GetService<TimeProvider>() ?? TimeProvider.System;
        services.AddSingleton(time);
        services.AddSingleton(outer.GetService<LoginRateLimiter>() ?? new LoginRateLimiter(time));
        services.Replace(ServiceDescriptor.Singleton(outer.GetRequiredService<ILoggerFactory>()));
    }

    private static void ConfigurePipeline(WebApplication app)
    {
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<ErrorHandlingMiddleware>();
        app.UseMiddleware<NetworkAclMiddleware>();
        app.UseMiddleware<HttpsRedirectMiddleware>();
        app.UseMiddleware<CsrfMiddleware>();
        app.UsePanelFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<PasswordChangeGateMiddleware>();
        app.UseAuthorization();

        RouteGroupBuilder api = app.MapGroup("/api");
        AuthEndpoints.Map(api);

        RouteGroupBuilder read = api.MapGroup("").RequireAuthorization(Policies.Read);
        ReadEndpoints.Map(read);
        StreamEndpoint.Map(read);

        RouteGroupBuilder operate = api.MapGroup("").RequireAuthorization(Policies.Operator);
        OperateEndpoints.Map(operate);

        RouteGroupBuilder admin = api.MapGroup("/admin").RequireAuthorization(Policies.Admin);
        AdminUpsEndpoints.Map(admin);
        ImportEndpoints.Map(admin);
        AccountEndpoints.Map(admin);
        SettingsEndpoints.Map(admin);
        NotificationEndpoints.Map(admin);
        ServerEndpoints.Map(admin);

        app.MapFallback("/api/{**path}", context => ApiErrorWriter.WriteAsync(
            context, StatusCodes.Status404NotFound, ErrorCodes.NotFound, "There is no such API endpoint."));
    }
}

/// <summary>A host lifetime that does nothing: NutHub's own host decides when the panel starts and stops.</summary>
internal sealed class NoopHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
