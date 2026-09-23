using System.Text.Json;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Services.Notifications;
using NutHub.Services.Notifications.Commands;
using NutHub.Services.Processes;

namespace NutHub.Services.Tests.Notifications;

public sealed class TemplateTests
{
    private static readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ups"] = "rack 1",
        ["message"] = "Line 1\n\"quoted\" \\ back & più",
        ["charge"] = "42",
    };

    [Fact]
    public void Known_placeholders_are_replaced_and_everything_else_is_kept()
    {
        string result = Placeholders.Apply("{\"ups\": \"{ups}\", \"x\": {unknown}, \"c\": {CHARGE}} {", Values,
                                           PlaceholderEscaping.None);

        Assert.Equal("{\"ups\": \"rack 1\", \"x\": {unknown}, \"c\": 42} {", result);
    }

    [Fact]
    public void Json_escaping_produces_a_valid_document()
    {
        string result = Placeholders.Apply("{\"text\": \"{message}\", \"ups\": \"{ups}\"}", Values, PlaceholderEscaping.Json);

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.Equal(Values["message"], doc.RootElement.GetProperty("text").GetString());
        Assert.Contains("più", result); // non-ASCII stays readable
    }

    [Fact]
    public void Url_escaping_encodes_reserved_characters()
    {
        string result = Placeholders.Apply("https://h/x?ups={ups}&m={message}", Values, PlaceholderEscaping.Url);

        Assert.StartsWith("https://h/x?ups=rack%201&m=Line%201%0A%22quoted%22%20%5C%20back%20%26%20pi", result);
        Assert.DoesNotContain(" ", result);
    }

    [Theory]
    [InlineData("application/json", "Json")]
    [InlineData("application/vnd.api+json; charset=utf-8", "Json")]
    [InlineData("application/x-www-form-urlencoded", "Url")]
    [InlineData("text/plain", "None")]
    public void Escaping_follows_the_content_type(string contentType, string expected) =>
        Assert.Equal(expected, Placeholders.ForContentType(contentType).ToString());

    [Fact]
    public void Context_provides_every_documented_placeholder()
    {
        var e = UpsEvent.Create(UpsEventType.OnBattery, new DateTimeOffset(2026, 9, 22, 10, 15, 0, TimeSpan.FromHours(2)),
                                "rack1", "rack1 is on battery.", "system");
        var context = new NotificationContext(e, "srv", "Room 2",
                                              new UpsReadings("rack1", "OB DISCHRG", "98", "1620", "23", "0", true));

        IReadOnlyDictionary<string, string> v = context.PlaceholderValues();

        Assert.Equal("rack1", v["ups"]);
        Assert.Equal("onBattery", v["type"]);
        Assert.Equal("ONBATT", v["notifytype"]);
        Assert.Equal("warning", v["severity"]);
        Assert.Equal("power", v["category"]);
        Assert.Equal("2026-09-22T08:15:00Z", v["timestamp"]);
        Assert.Equal("srv", v["server"]);
        Assert.Equal("OB DISCHRG", v["status"]);
        Assert.Equal("98", v["charge"]);
        Assert.Equal("1620", v["runtime"]);
        Assert.Equal("23", v["load"]);
        Assert.Equal("system", v["actor"]);
    }

    [Theory]
    [InlineData(UpsEventType.OnBattery, "ONBATT")]
    [InlineData(UpsEventType.Online, "ONLINE")]
    [InlineData(UpsEventType.LowBattery, "LOWBATT")]
    [InlineData(UpsEventType.ForcedShutdown, "FSD")]
    [InlineData(UpsEventType.CommunicationLost, "COMMBAD")]
    [InlineData(UpsEventType.CommunicationRestored, "COMMOK")]
    [InlineData(UpsEventType.ShutdownStarted, "SHUTDOWN")]
    [InlineData(UpsEventType.ReplaceBattery, "REPLBATT")]
    [InlineData(UpsEventType.NoCommunication, "NOCOMM")]
    [InlineData(UpsEventType.BypassCleared, "NOTBYPASS")]
    [InlineData(UpsEventType.BoostEnded, "NOTBOOST")]
    [InlineData(UpsEventType.ShutdownPending, "SHUTDOWN_PENDING")]
    [InlineData(UpsEventType.LowBatteryCleared, "LOW_BATTERY_CLEARED")]
    public void Notify_types_follow_upsmon(UpsEventType type, string expected) =>
        Assert.Equal(expected, NotifyTypes.For(type));

    [Theory]
    [InlineData("a b  c", new[] { "a", "b", "c" })]
    [InlineData("\"C:\\Program Files\\x.exe\" /s", new[] { "C:\\Program Files\\x.exe", "/s" })]
    [InlineData("--msg \"say \\\"hi\\\"\" \"\"", new[] { "--msg", "say \"hi\"", "" })]
    [InlineData("it's fine", new[] { "it's", "fine" })]
    [InlineData("pre\"fix mid\"post", new[] { "prefix midpost" })]
    public void Command_lines_split_like_a_shell_without_one(string line, string[] expected) =>
        Assert.Equal(expected, CommandLine.Split(line));

    [Fact]
    public void Command_arguments_substitute_inside_each_argument()
    {
        var hook = new CommandHookSettings { Command = "notify", Arguments = "--ups {ups} \"{message}\" x{charge}y" };

        List<string> args = CommandHookRunner.BuildArguments(hook, Values);

        // A value with spaces or quotes stays one argument: no injection possible.
        Assert.Equal(new[] { "--ups", "rack 1", Values["message"], "x42y" }, args);
    }

    [Theory]
    [InlineData("C:\\hooks\\notify.bat", true)]
    [InlineData("notify.CMD", true)]
    [InlineData("C:\\hooks\\notify.cmd. .", true)]
    [InlineData("C:\\hooks\\notify.exe", false)]
    [InlineData("/usr/local/bin/notify.sh", false)]
    public void Batch_files_are_recognised_like_windows_does(string fileName, bool expected) =>
        Assert.Equal(expected, ProcessRunner.IsBatchFile(fileName));

    [Fact]
    public void Batch_file_arguments_are_escaped_for_cmd()
    {
        string? line = ProcessRunner.BatchCommandLine("C:\\hooks\\notify.cmd",
                                                      ["ONBATT", "a\"b & c", "50% %PATH%", "", "C:\\dir\\", "x\ny"]);

        Assert.Equal("/e:ON /v:OFF /d /c \"\"C:\\hooks\\notify.cmd\" ONBATT \"a\"\"b & c\" " +
                     "\"50%%cd:~,% %%cd:~,%PATH%%cd:~,%\" \"\" \"C:\\dir\\\\\" \"x y\"\"", line);
        Assert.Null(ProcessRunner.BatchCommandLine("C:\\hooks\\\"x\".cmd", []));
    }
}
