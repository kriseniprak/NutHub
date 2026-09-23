using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Core.Security;
using NutHub.Drivers.Net.Nut;

namespace NutHub.Drivers.Net.Tests.Fakes;

/// <summary>A driver running in the background for the duration of a test.</summary>
public sealed class RunningDriver : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly IUpsDriver _driver;
    private readonly Task _run;

    private RunningDriver(IUpsDriver driver, RecordingDriverContext context)
    {
        _driver = driver;
        Context = context;
        _run = Task.Run(() => driver.RunAsync(context, _cts.Token));
    }

    public RecordingDriverContext Context { get; }

    public static RunningDriver Start(IUpsDriver driver, RecordingDriverContext? context = null) =>
        new(driver, context ?? new RecordingDriverContext());

    /// <summary>Stops the driver; any exception other than the cancellation fails the test.</summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            // Expected: RunAsync ends by cancellation.
        }

        Assert.True(_run.IsCompleted, "The driver did not stop when cancelled.");
        await _driver.DisposeAsync();
        _cts.Dispose();
    }
}

internal static class TestSupport
{
    /// <summary>A self-signed server certificate usable by SslStream on every platform.</summary>
    public static X509Certificate2 CreateServerCertificate()
    {
        using X509Certificate2 created = CertificateProvider.CreateSelfSigned("localhost");

        // SslStream on Windows cannot use the ephemeral key of a freshly created certificate: go through PKCS#12.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    public static Dictionary<string, string> NutOptions(FakeUpsd server, params (string Key, string Value)[] extra)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = "127.0.0.1",
            ["port"] = server.Port.ToString(CultureInfo.InvariantCulture),
            ["upsName"] = "myups",
            ["timeoutMs"] = "2000",
        };
        foreach (var (key, value) in extra)
        {
            options[key] = value;
        }

        return options;
    }

    public static NutUpstreamDriver CreateNutDriver(FakeUpsd server, params (string Key, string Value)[] extra)
    {
        NutUpstreamSettings settings = NutUpstreamDriverFactory.ReadSettings(new DriverOptionReader(NutOptions(server, extra)));
        return new NutUpstreamDriver("test", settings, TimeProvider.System, NullLogger<NutUpstreamDriver>.Instance)
        {
            ReconnectDelays = [TimeSpan.FromMilliseconds(100)],
            TrackingPollInterval = TimeSpan.FromMilliseconds(20),
            TrackingTimeout = TimeSpan.FromSeconds(3),
        };
    }

    public static (string Key, string Value)[] Credentials => [("username", "admin"), ("password", "secret")];
}
