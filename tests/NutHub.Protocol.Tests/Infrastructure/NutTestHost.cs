using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NutHub.Core;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Core.Security;
using NutHub.Protocol.Server;
using Xunit.Abstractions;

namespace NutHub.Protocol.Tests.Infrastructure;

/// <summary>
/// A NUT server on 127.0.0.1 (ephemeral port) over the real Core runtime (registry, driver manager, UPS units)
/// fed by <see cref="FakeDriverFactory"/>. Time is a <see cref="FakeTimeProvider"/>: data stays fresh and nothing
/// times out unless a test advances it.
/// </summary>
internal sealed class NutTestHost : IAsyncDisposable
{
    /// <summary>The password of every test account: a space and a '#' exercise the quoting rules.</summary>
    public const string Password = "s3cret pass#word";

    private readonly string _dataDirectory;
    private readonly ServiceProvider _provider;
    private readonly IDisposable _eventSubscription;

    private NutTestHost(ITestOutputHelper? output, Action<NutHubConfig>? configure, NutProtocolOptions? options)
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "nuthub-protocol-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
        Paths = new NutHubPaths(_dataDirectory);
        Logs = new CapturingLoggerProvider(output);

        NutHubConfig config = DefaultConfig(Hasher);
        configure?.Invoke(config);
        Config = new TestConfigStore(config);

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(Logs);
        });
        services.AddSingleton<TimeProvider>(Time);
        services.AddNutHubCore(Paths);
        services.AddSingleton<IPasswordHasher>(Hasher);
        services.AddSingleton<IConfigStore>(Config);
        services.AddSingleton<IUpsDriverFactory>(Drivers);
        services.AddNutHubProtocol();
        services.AddSingleton(options ?? new NutProtocolOptions());
        _provider = services.BuildServiceProvider();

        Hub = _provider.GetRequiredService<EventHub>();
        _eventSubscription = Hub.Subscribe(message =>
        {
            if (message is UpsEventMessage e)
            {
                Events.Enqueue(e.Event);
            }
        });
        Registry = _provider.GetRequiredService<UpsRegistry>();
        Sessions = _provider.GetRequiredService<NutSessionRegistry>();
        DriverManager = _provider.GetRequiredService<DriverManager>();
        Server = _provider.GetRequiredService<NutServer>();
    }

    public static Pbkdf2PasswordHasher Hasher { get; } = new(iterations: 1_000);

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));

    public NutHubPaths Paths { get; }

    public TestConfigStore Config { get; }

    public FakeDriverFactory Drivers { get; } = new();

    public CapturingLoggerProvider Logs { get; }

    public EventHub Hub { get; }

    public ConcurrentQueue<UpsEvent> Events { get; } = new();

    public UpsRegistry Registry { get; }

    public NutSessionRegistry Sessions { get; }

    public DriverManager DriverManager { get; }

    public NutServer Server { get; }

    public IServiceProvider Services => _provider;

    /// <summary>The first endpoint the server listens on.</summary>
    public IPEndPoint EndPoint => Server.BoundEndPoints[0];

    public FakeDevice Device(string ups) => Drivers.Device(ups);

    public static async Task<NutTestHost> StartAsync(ITestOutputHelper? output = null,
                                                     Action<NutHubConfig>? configure = null,
                                                     NutProtocolOptions? options = null)
    {
        var host = new NutTestHost(output, configure, options);
        try
        {
            await host.DriverManager.StartAsync(CancellationToken.None);
            await host.Server.StartAsync(CancellationToken.None);
            NutHubConfig config = host.Config.Current;
            if (config.Nut.Enabled && config.Nut.Listen.Count > 0)
            {
                await WaitUntilAsync(() => host.Server.BoundEndPoints.Count > 0 || host.Server.LastError is not null,
                                     "the server to listen");
            }

            foreach (UpsConfig ups in config.Ups.Where(u => u.Enabled))
            {
                await WaitUntilAsync(() => host.Registry.Find(ups.Name)?.Snapshot.IsAvailable == true,
                                     $"{ups.Name} to have data");
            }

            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public static NutHubConfig DefaultConfig(IPasswordHasher hasher)
    {
        string hash = hasher.Hash(Password);
        var config = new NutHubConfig();
        config.Nut.Listen = [new ListenEndpoint { Address = "127.0.0.1", Port = 0 }];
        config.Nut.FsdClearDelaySeconds = 0;
        config.Ups =
        [
            new UpsConfig { Name = "ups1", Description = "Rack \"A\" #1 \\ main", Driver = FakeDriverFactory.DriverId },
            new UpsConfig { Name = "ups2", Driver = FakeDriverFactory.DriverId },
            new UpsConfig { Name = "off", Driver = FakeDriverFactory.DriverId, Enabled = false },
        ];
        config.NutUsers =
        [
            new NutUserConfig { Name = "monprimary", PasswordHash = hash, Monitor = NutMonitorRole.Primary },
            new NutUserConfig { Name = "monsecondary", PasswordHash = hash, Monitor = NutMonitorRole.Secondary },
            new NutUserConfig { Name = "admin", PasswordHash = hash, Actions = ["SET", "FSD"], InstantCommands = ["ALL"] },
            new NutUserConfig
            {
                Name = "operator", PasswordHash = hash, Actions = ["set"], InstantCommands = ["beeper.enable"],
                AllowedUps = ["ups1"],
            },
            new NutUserConfig { Name = "fsdonly", PasswordHash = hash, Actions = ["fsd"] },
            new NutUserConfig { Name = "nobody", PasswordHash = hash },
        ];
        return config;
    }

    public async Task<NutTestClient> ConnectAsync(int? receiveBufferSize = null) =>
        await NutTestClient.ConnectAsync(EndPoint, receiveBufferSize);

    /// <summary>A client that already sent USERNAME and PASSWORD (quoted, as the password has a space).</summary>
    public async Task<NutTestClient> ConnectAsAsync(string user, string password = Password)
    {
        NutTestClient client = await ConnectAsync();
        Assert.Equal("OK", await client.CommandAsync("USERNAME " + user));
        Assert.Equal("OK", await client.CommandAsync("PASSWORD " + NutTestClient.Quote(password)));
        return client;
    }

    /// <summary>Waits until the UPS snapshot satisfies a condition (the driver runs on other threads).</summary>
    public Task WaitForUpsAsync(string ups, Func<UpsSnapshot, bool> condition, string what) =>
        WaitUntilAsync(() => Registry.Find(ups) is { } unit && condition(unit.Snapshot), what);

    public static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (watch.Elapsed > limit)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Server.StopAsync(CancellationToken.None);
            await DriverManager.StopAsync(CancellationToken.None);
        }
        finally
        {
            _eventSubscription.Dispose();
            await _provider.DisposeAsync();
            try
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A certificate file may still be locked for a moment; the temp folder is cleaned eventually.
            }
        }
    }
}
