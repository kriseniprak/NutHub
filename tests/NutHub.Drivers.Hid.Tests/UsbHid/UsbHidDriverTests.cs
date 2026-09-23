using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Model;
using NutHub.Drivers.Hid.Tests.Support;
using NutHub.Drivers.Hid.UsbHid;

namespace NutHub.Drivers.Hid.Tests.UsbHid;

/// <summary>
/// The driver loop against fake devices, with short delays. Every wait is on a condition with a generous limit,
/// so the tests do not depend on the machine's speed.
/// </summary>
public sealed class UsbHidDriverTests
{
    private static readonly UsbHidTimings Fast = new()
    {
        RetryDelay = TimeSpan.FromMilliseconds(20),
        InterruptWait = TimeSpan.FromMilliseconds(20),
        MinimumReadTimeout = TimeSpan.FromSeconds(10),
        CommandTimeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task Publishes_every_poll()
    {
        await using var run = Start(new FakeHidDeviceSource(SyntheticUps.Create()), TimeSpan.FromMilliseconds(30));

        await run.Context.WaitUntilAsync(c => c.Updates.Count >= 3, "three readings");

        Assert.Equal("OL CHRG", run.Context.Last!.Variables["ups.status"]);
        Assert.Contains("test.battery.start.quick", run.Context.Last.Commands!);
        Assert.Equal("connecting: Looking for the USB UPS.", run.Context.Events[0]);
    }

    [Fact]
    public async Task Reconnects_after_an_unplug()
    {
        FakeHidDevice device = SyntheticUps.Create();
        var source = new FakeHidDeviceSource(device);
        await using var run = Start(source, TimeSpan.FromMilliseconds(30));
        await run.Context.WaitUntilAsync(c => c.Updates.Count >= 1, "the first reading");

        device.Present = false;
        await run.Context.WaitUntilAsync(c => c.Events.Any(e => e.StartsWith("disconnected: ", StringComparison.Ordinal)), "the disconnection");
        int published = run.Context.Updates.Count;
        FakeHidConnection? first = source.LastConnection;
        device.SetFeature(0x01, 42);
        device.Present = true;
        await run.Context.WaitUntilAsync(c => c.Updates.Count > published && c.Last!.Variables["battery.charge"] == "42", "the reconnection");

        Assert.Equal(2, device.Opens);
        Assert.True(first!.Disposed);
        Assert.Single(run.Context.Events, e => e.StartsWith("disconnected: Communication with the UPS was lost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sticks_to_the_same_ups_after_an_unplug()
    {
        FakeHidDevice first = SyntheticUps.Create("/dev/hidraw1", serial: "A");
        FakeHidDevice second = SyntheticUps.Create("/dev/hidraw2", serial: "B");
        await using var run = Start(new FakeHidDeviceSource(first, second), TimeSpan.FromMilliseconds(30));
        await run.Context.WaitUntilAsync(c => c.Updates.Count >= 1, "the first reading");
        Assert.Equal("A", run.Context.Last!.Variables["ups.serial"]);

        first.Present = false;
        await run.Context.WaitUntilAsync(c => c.Events.Count(e => e.StartsWith("disconnected: ", StringComparison.Ordinal)) >= 2,
                                         "the failed reconnection");
        await Task.Delay(200);

        Assert.Equal(0, second.Opens);
        Assert.Contains(run.Context.Events, e => e.Contains("serial number 'A'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publishes_input_reports_without_waiting_for_the_poll()
    {
        FakeHidDevice device = SyntheticUps.Create();
        await using var run = Start(new FakeHidDeviceSource(device), TimeSpan.FromSeconds(30));
        await run.Context.WaitUntilAsync(c => c.Updates.Count >= 1, "the first reading");

        device.QueueInput(0x03, SyntheticUps.OnBattery);

        await run.Context.WaitUntilAsync(c => c.Last!.Variables["ups.status"] == "OB DISCHRG", "the power failure", seconds: 5);
    }

    [Fact]
    public async Task A_hung_ups_is_reported_and_reconnected()
    {
        FakeHidDevice device = SyntheticUps.Create();
        var timings = Fast with { MinimumReadTimeout = TimeSpan.FromMilliseconds(300) };
        await using var run = Start(new FakeHidDeviceSource(device), TimeSpan.FromMilliseconds(30), timings);
        await run.Context.WaitUntilAsync(c => c.Updates.Count >= 1, "the first reading");

        device.Hang();
        await run.Context.WaitUntilAsync(c => c.Events.Contains("disconnected: The UPS stopped answering; reconnecting."), "the timeout");
        int published = run.Context.Updates.Count;
        device.Resume();

        await run.Context.WaitUntilAsync(c => c.Updates.Count > published, "the reconnection");
    }

    [Fact]
    public async Task Reports_each_problem_once_while_retrying()
    {
        await using var run = Start(new FakeHidDeviceSource(), TimeSpan.FromMilliseconds(30));

        await Task.Delay(300); // about ten attempts

        string problem = Assert.Single(run.Context.Events, e => e.StartsWith("connecting: No USB UPS", StringComparison.Ordinal));
        Assert.Contains("was found", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_permissions_are_explained()
    {
        FakeHidDevice device = SyntheticUps.Create();
        device.DenyOpen = true;
        await using var run = Start(new FakeHidDeviceSource(device), TimeSpan.FromMilliseconds(30));

        string expected = OperatingSystem.IsWindows() ? "winbattery" : OperatingSystem.IsLinux() ? "udev" : "denied";
        await run.Context.WaitUntilAsync(c => c.Events.Any(e => e.Contains(expected, StringComparison.Ordinal)), "the permission message");
    }

    [Fact]
    public async Task Commands_and_writes_run_on_the_device()
    {
        FakeHidDevice device = SyntheticUps.Create();
        await using var run = Start(new FakeHidDeviceSource(device), TimeSpan.FromMilliseconds(30));
        await run.Context.WaitUntilAsync(c => c.Updates.Count >= 1, "the first reading");

        CommandResult command = await run.Driver.InstantCommandAsync("test.battery.start.quick", null, CancellationToken.None);
        CommandResult write = await run.Driver.SetVariableAsync("battery.charge.low", "30", CancellationToken.None);

        Assert.True(command.IsSuccess, command.ToString());
        Assert.True(write.IsSuccess, write.ToString());
        Assert.Equal(new byte[][] { [0x0A, 0x01], [0x05, 30, 0x78, 0x00] }, device.Writes);
        await run.Context.WaitUntilAsync(c => c.Last!.Variables["battery.charge.low"] == "30", "the new limit");
    }

    [Fact]
    public async Task Commands_without_a_device_report_not_connected()
    {
        var driver = new UsbHidDriver("test", new UsbHidSettings(), new FakeHidDeviceSource(), NullLogger.Instance, TimeProvider.System, Fast);

        CommandResult result = await driver.InstantCommandAsync("test.battery.start.quick", null, CancellationToken.None);

        Assert.Equal(CommandStatus.DriverNotConnected, result.Status);
        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Stopping_closes_the_device()
    {
        var source = new FakeHidDeviceSource(SyntheticUps.Create());
        RunningDriver run = Start(source, TimeSpan.FromMilliseconds(30));
        await run.Context.WaitUntilAsync(c => c.Updates.Count >= 1, "the first reading");

        await run.DisposeAsync();

        Assert.True(run.Task.IsCompletedSuccessfully);
        Assert.True(source.LastConnection!.Disposed);
    }

    private static RunningDriver Start(FakeHidDeviceSource source, TimeSpan poll, UsbHidTimings? timings = null)
    {
        var driver = new UsbHidDriver("test", new UsbHidSettings(), source, NullLogger.Instance, TimeProvider.System, timings ?? Fast);
        var context = new RecordingDriverContext(poll);
        var cts = new CancellationTokenSource();
        Task task = Task.Run(() => driver.RunAsync(context, cts.Token));
        return new RunningDriver(driver, context, cts, task);
    }

    private sealed record RunningDriver(UsbHidDriver Driver, RecordingDriverContext Context, CancellationTokenSource Cts, Task Task)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Cts.CancelAsync();
            await Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Driver.DisposeAsync();
            Cts.Dispose();
        }
    }
}
