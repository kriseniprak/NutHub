using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Services.Tests.Support;

namespace NutHub.Services.Tests.Notifications;

/// <summary>Runs harmless shell built-ins: cmd.exe on Windows, /bin/sh elsewhere.</summary>
public sealed class CommandHookTests : NotificationServiceTestBase
{
    [Fact]
    public async Task Exit_code_and_standard_error_are_reported()
    {
        AddCommand(Shell("echo something broke 1>&2 & exit 3", "echo something broke 1>&2; exit 3"));
        var service = CreateService();

        service.Dispatch(Event(UpsEventType.OnBattery));
        await service.WhenIdleAsync();

        NotificationDelivery delivery = Assert.Single(service.RecentDeliveries);
        Assert.False(delivery.Success);
        Assert.Equal(NotificationChannelKind.Command, delivery.Channel);
        Assert.StartsWith("Exit code 3", delivery.Error);
        Assert.Contains("something broke", delivery.Error);
    }

    [Fact]
    public async Task Event_details_are_in_the_environment()
    {
        AddCommand(Shell("echo %NOTIFYTYPE%/%UPSNAME%/%NUTHUB_EVENT%/%NUTHUB_SEVERITY%/%NUTHUB_SERVER% 1>&2 & exit 1",
                         "echo \"$NOTIFYTYPE/$UPSNAME/$NUTHUB_EVENT/$NUTHUB_SEVERITY/$NUTHUB_SERVER\" 1>&2; exit 1"));
        var service = CreateService();

        service.Dispatch(Event(UpsEventType.LowBattery, ups: "rack1"));
        await service.WhenIdleAsync();

        Assert.Contains("LOWBATT/rack1/lowBattery/critical/lab", Assert.Single(service.RecentDeliveries).Error);
    }

    [Fact]
    public async Task Successful_command_is_recorded()
    {
        AddCommand(Shell("exit 0", "exit 0"));
        var service = CreateService();

        CommandResult result = await service.SendTestAsync(NotificationChannelKind.Command, "c1");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(Assert.Single(service.RecentDeliveries).Success);
    }

    [Fact]
    public async Task A_command_running_too_long_is_killed()
    {
        // The program would run for five minutes, so the call returning at all is what proves it was killed;
        // comparing the time it took against a shorter limit only measured how busy the machine was.
        CommandHookSettings hook = OperatingSystem.IsWindows()
            ? new CommandHookSettings { Id = "c1", Command = "ping.exe", Arguments = "-n 300 127.0.0.1", TimeoutSeconds = 1 }
            : new CommandHookSettings { Id = "c1", Command = "/bin/sh", Arguments = "-c \"sleep 300\"", TimeoutSeconds = 1 };
        AddCommand(hook);
        var service = CreateService();

        CommandResult result = await service.SendTestAsync(NotificationChannelKind.Command, "c1")
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(result.IsSuccess);
        Assert.StartsWith("Killed after 1 s", result.Message);
    }

    [Fact]
    public async Task A_missing_program_is_reported()
    {
        AddCommand(new CommandHookSettings { Id = "c1", Command = "nuthub-no-such-program-" + Guid.NewGuid().ToString("N") });
        var service = CreateService();

        CommandResult result = await service.SendTestAsync(NotificationChannelKind.Command, "c1");

        Assert.False(result.IsSuccess);
        Assert.StartsWith("Could not start", result.Message);
    }

    [Fact]
    public async Task Event_text_cannot_inject_commands_into_a_batch_file_hook()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Windows starts a batch file through cmd.exe, which would parse the event text a second time.
        using var dir = new TempDirectory();
        string script = dir.File("hook.cmd");
        File.WriteAllText(script, "@echo off\r\necho %1>\"%~dp0args.txt\"\r\n");
        AddCommand(new CommandHookSettings { Id = "c1", Command = script, Arguments = "{message} {ups}", TimeoutSeconds = 20 });
        var service = CreateService();
        string message = "Failed sign-in as 'x\" & echo.>pwned.txt & rem \"' from 10.0.0.1 (%PATH%, 100%)";

        service.Dispatch(Event(UpsEventType.OnBattery, message: message));
        await service.WhenIdleAsync();

        Assert.False(File.Exists(dir.File("pwned.txt")), "the event text ran a command");
        Assert.True(Assert.Single(service.RecentDeliveries).Success);
        string received = File.ReadAllText(dir.File("args.txt")).Trim();
        Assert.Contains("from 10.0.0.1 (%PATH%, 100%)", received);
    }

    [Fact]
    public async Task A_batch_file_hook_gets_plain_arguments_unchanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        string script = dir.File("hook.bat");
        File.WriteAllText(script, "@echo off\r\n>\"%~dp0args.txt\" echo [%~1] [%~2] [%~3]\r\n");
        AddCommand(new CommandHookSettings { Id = "c1", Command = script, Arguments = "{notifytype} {ups} \"two words\"" });
        var service = CreateService();

        service.Dispatch(Event(UpsEventType.OnBattery, ups: "rack1"));
        await service.WhenIdleAsync();

        Assert.True(Assert.Single(service.RecentDeliveries).Success);
        Assert.Equal("[ONBATT] [rack1] [two words]", File.ReadAllText(dir.File("args.txt")).Trim());
    }

    private static CommandHookSettings Shell(string windows, string unix) =>
        OperatingSystem.IsWindows()
            ? new CommandHookSettings { Id = "c1", Command = "cmd.exe", Arguments = $"/d /c \"{windows}\"", TimeoutSeconds = 20 }
            : new CommandHookSettings { Id = "c1", Command = "/bin/sh", Arguments = $"-c \"{unix.Replace("\"", "\\\"")}\"", TimeoutSeconds = 20 };

    private void AddCommand(CommandHookSettings hook) =>
        Config.Update(c =>
        {
            c.Server.Name = "lab";
            c.Notifications.Commands.Add(hook);
        });
}
