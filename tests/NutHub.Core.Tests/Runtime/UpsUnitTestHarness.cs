using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Core.Tests.Support;

namespace NutHub.Core.Tests.Runtime;

/// <summary>A <see cref="UpsUnit"/> driven by hand, the way the driver manager and a driver would.</summary>
internal sealed class UpsUnitHarness : IDisposable
{
    public UpsUnitHarness(UpsConfig? config = null)
    {
        Recorder = new HubRecorder(Hub);
        Unit = new UpsUnit(config ?? new UpsConfig { Name = "ups1", Driver = "fake" }, Hub, Time,
                           NullLogger.Instance, () => Server);
    }

    public FakeTimeProvider Time { get; } = TestTime.Create();

    public EventHub Hub { get; } = TestTime.Hub();

    public HubRecorder Recorder { get; }

    public NutServerSettings Server { get; } = new() { MaxAgeSeconds = 15, FsdClearDelaySeconds = 60 };

    public UpsUnit Unit { get; }

    public UpsSnapshot Snapshot => Unit.Snapshot;

    public IReadOnlyList<UpsEventType> Events => Recorder.EventTypes;

    /// <summary>Publishes like a driver: the variables, optional metadata and commands.</summary>
    public void Publish(string status, Dictionary<string, string>? more = null,
                        Dictionary<string, VariableInfo>? info = null, string[]? commands = null)
    {
        var vars = new Dictionary<string, string>(more ?? []) { ["ups.status"] = status };
        Unit.OnPublish(new DriverUpdate { Variables = vars, VariableInfo = info, Commands = commands });
    }

    /// <summary>A started driver run that has published once (an online UPS by default).</summary>
    public void Connect(string status = "OL", Dictionary<string, string>? more = null)
    {
        Unit.BeginRun(clearData: true);
        Publish(status, more);
    }

    public void Advance(double seconds)
    {
        Time.Advance(TimeSpan.FromSeconds(seconds));
        Unit.Tick();
    }

    public FakeDriver AttachDriver()
    {
        var driver = new FakeDriver(FakeRunBehavior.PublishAndWait, [], DummyCreateContext());
        Unit.AttachDriver(driver);
        return driver;
    }

    public static DriverCreateContext DummyCreateContext() => new()
    {
        UpsName = "ups1",
        Options = new Dictionary<string, string>(),
        PollInterval = TimeSpan.FromSeconds(2),
        LoggerFactory = NullLoggerFactory.Instance,
        TimeProvider = TimeProvider.System,
        Services = new ServiceCollection().BuildServiceProvider(),
    };

    public void Dispose() => Recorder.Dispose();
}
