using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NutHub.Core;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Runtime;
using NutHub.Core.Security;
using NutHub.Web.Hosting;

namespace NutHub.Web.Tests.TestSupport;

/// <summary>
/// A complete NutHub core (configuration in a temporary directory, driver manager, event recorder) with one panel
/// instance on a test server. Accounts: admin / operator / viewer, password "{name}-password".
/// </summary>
internal sealed class WebTestHost : IAsyncDisposable
{
    public const string CsrfHeader = "X-NutHub-Request";

    // Fast hashing: the tests sign in many times; the format and verification are the production ones.
    public static readonly IPasswordHasher Hasher = new Pbkdf2PasswordHasher(iterations: 1000);

    private readonly IHost _host;
    private readonly WebApplication _app;

    private WebTestHost(string directory, IHost host, WebApplication app)
    {
        Directory = directory;
        _host = host;
        _app = app;
    }

    public string Directory { get; }

    public IServiceProvider Services => _host.Services;

    public IConfigStore Config => Services.GetRequiredService<IConfigStore>();

    public EventHub Hub => Services.GetRequiredService<EventHub>();

    public FakeHistoryStore History => (FakeHistoryStore)Services.GetRequiredService<IHistoryStore>();

    public static string PasswordOf(string user) => user + "-password";

    public static async Task<WebTestHost> StartAsync(Action<NutHubConfig>? seed = null,
                                                     Action<IServiceCollection>? services = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "nuthub-web-tests", Guid.NewGuid().ToString("N"));
        var paths = new NutHubPaths(directory);
        paths.EnsureCreated();

        var config = new NutHubConfig();
        config.Nut.Enabled = false;
        config.WebUsers.Add(User("admin", WebRole.Admin));
        config.WebUsers.Add(User("operator", WebRole.Operator));
        config.WebUsers.Add(User("viewer", WebRole.Viewer));
        config.Ups.Add(new UpsConfig { Name = "sim1", Driver = "simulated", Description = "Simulated UPS" });
        seed?.Invoke(config);
        await File.WriteAllTextAsync(paths.ConfigFile, JsonSerializer.Serialize(config, NutHubJson.File));

        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information));
        builder.Services.AddNutHubCore(paths);
        builder.Services.AddSingleton(Hasher);
        builder.Services.AddSingleton<IUpsDriverFactory, TestDriverFactory>();
        builder.Services.AddSingleton<IHistoryStore, FakeHistoryStore>();
        services?.Invoke(builder.Services);
        IHost host = builder.Build();
        await host.StartAsync();

        WebApplication app = WebPanelApp.Build(host.Services, PanelBinding.From(config.Web), null,
                                               b => b.WebHost.UseTestServer());
        await app.StartAsync();
        return new WebTestHost(directory, host, app);
    }

    public static WebUserConfig User(string name, WebRole role, bool mustChangePassword = false) => new()
    {
        Name = name,
        DisplayName = name.ToUpperInvariant(),
        Role = role,
        PasswordHash = Hasher.Hash(PasswordOf(name)),
        MustChangePassword = mustChangePassword,
    };

    /// <summary>A client with its own cookies, seen as coming from <paramref name="remote"/> (127.0.0.1 by default).</summary>
    public HttpClient Client(bool csrfHeader = true, string remote = "127.0.0.1", bool https = false)
    {
        IPAddress address = IPAddress.Parse(remote);
        HttpMessageHandler inner = _app.GetTestServer().CreateHandler(context =>
        {
            context.Connection.RemoteIpAddress = address;
        });
        var client = new HttpClient(new CookieContainerHandler(new CookieContainer()) { InnerHandler = inner })
        {
            BaseAddress = new Uri(https ? "https://localhost/" : "http://localhost/"),
        };
        if (csrfHeader)
        {
            client.DefaultRequestHeaders.Add(CsrfHeader, "1");
        }

        return client;
    }

    /// <summary>A client signed in as one of the seeded accounts.</summary>
    public async Task<HttpClient> SignedInAsync(string user, string remote = "127.0.0.1")
    {
        HttpClient client = Client(remote: remote);
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login",
                                                                   new { username = user, password = PasswordOf(user) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    /// <summary>Waits until the driver manager created the unit (and, optionally, the driver published data).</summary>
    public async Task<UpsUnit> WaitForUpsAsync(string name, bool connected = true)
    {
        var registry = Services.GetRequiredService<IUpsRegistry>();
        for (int i = 0; i < 200; i++)
        {
            if (registry.Find(name) is { } unit && (!connected || unit.Snapshot.IsAvailable))
            {
                return unit;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"The UPS {name} did not come up.");
    }

    public async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Timed out waiting for " + what);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still open by a watcher; the temporary directory is cleaned by the OS eventually.
        }
    }
}

/// <summary>JSON helpers for the tests.</summary>
internal static class Json
{
    public static async Task<JsonNode> ReadAsync(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty body");
    }

    public static StringContent Body(object value) =>
        new(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), Encoding.UTF8, "application/json");

    public static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");

    public static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        (await ReadAsync(response))["error"]?.GetValue<string>();
}
