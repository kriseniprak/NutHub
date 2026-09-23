using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NutHub.Services.Tests.Support;

/// <summary>Kestrel on 127.0.0.1 with a random port, recording every request and answering with a scripted status.</summary>
internal sealed class HttpTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private HttpTestServer(WebApplication app)
    {
        _app = app;
    }

    public ConcurrentQueue<ReceivedRequest> Requests { get; } = new();

    /// <summary>The statuses of the next answers, in order; 200 once empty.</summary>
    public ConcurrentQueue<int> Statuses { get; } = new();

    public string BaseUrl { get; private set; } = "";

    /// <summary>Answers wait for this task: a slow endpoint.</summary>
    public Task Hold { get; set; } = Task.CompletedTask;

    public static async Task<HttpTestServer> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        WebApplication app = builder.Build();
        var server = new HttpTestServer(app);
        app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            string body = await reader.ReadToEndAsync();
            var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            server.Requests.Enqueue(new ReceivedRequest(context.Request.Method,
                                                        context.Request.Path + context.Request.QueryString, headers, body));
            await server.Hold;
            context.Response.StatusCode = server.Statuses.TryDequeue(out int status) ? status : 200;
            await context.Response.WriteAsync(status >= 400 ? "not here" : "ok");
        });
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.BaseUrl = address.TrimEnd('/');
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed record ReceivedRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>A factory handing out plain clients with certificate validation on, like the production one.</summary>
internal sealed class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
