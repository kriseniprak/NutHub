using NutHub.Core.Model;
using NutHub.Drivers.Net.Nut;

namespace NutHub.Drivers.Net.Tests.Nut;

public sealed class NutProtocolParsingTests
{
    [Theory]
    [InlineData("VAR su700 ups.status \"OL CHRG\"", new[] { "VAR", "su700", "ups.status", "OL CHRG" })]
    [InlineData("VAR ups ups.id \"say \\\"hi\\\" \\\\ now\"", new[] { "VAR", "ups", "ups.id", "say \"hi\" \\ now" })]
    [InlineData("VAR ups ups.id \"\"", new[] { "VAR", "ups", "ups.id", "" })]
    [InlineData("VAR ups ups.id \"a # b\"", new[] { "VAR", "ups", "ups.id", "a # b" })]
    [InlineData("VAR ups x \\#literal", new[] { "VAR", "ups", "x", "#literal" })]
    [InlineData("ERR DATA-STALE # comment", new[] { "ERR", "DATA-STALE" })]
    [InlineData("   OK   ", new[] { "OK" })]
    [InlineData("a=b", new[] { "a", "=", "b" })]
    [InlineData("RANGE ups v \"90\" \"100\"", new[] { "RANGE", "ups", "v", "90", "100" })]
    [InlineData("VAR ups v \"unterminated", new[] { "VAR", "ups", "v", "unterminated" })]
    public void Split_follows_parseconf(string line, string[] expected)
    {
        Assert.Equal(expected, NutLine.Split(line));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("two words")]
    [InlineData("quote \" backslash \\ hash #")]
    [InlineData("")]
    [InlineData("a=b")]
    [InlineData("àccénts")]
    public void Quote_round_trips_through_split(string value)
    {
        string line = NutLine.Build("SET VAR", "ups", "ups.id") + " " + NutLine.Quote(value, always: true);
        Assert.Equal(["SET", "VAR", "ups", "ups.id", value], NutLine.Split(line));
        Assert.Equal(["X", value], NutLine.Split(NutLine.Build("X", value)));
    }

    [Fact]
    public void Plain_words_are_not_quoted_unless_asked()
    {
        Assert.Equal("INSTCMD ups load.off.delay 120", NutLine.Build("INSTCMD", "ups", "load.off.delay", "120"));
        Assert.Equal("\"120\"", NutLine.Quote("120", always: true));
        Assert.Equal("\"\"", NutLine.Quote(""));
    }

    [Theory]
    [InlineData("ok value", true)]
    [InlineData("line\nbreak", false)]
    [InlineData("tab\there", false)]
    [InlineData("nul\0", false)]
    public void Control_characters_are_not_transmittable(string value, bool expected)
    {
        Assert.Equal(expected, NutLine.IsTransmittable(value));
    }

    [Theory]
    [InlineData("RW STRING:32", true, false, false, true, 32)]
    [InlineData("RW ENUM", true, true, false, false, 0)]
    [InlineData("RW ENUM STRING:8", true, true, false, true, 8)]
    [InlineData("RW RANGE NUMBER", true, false, true, false, 0)]
    [InlineData("NUMBER", false, false, false, false, 0)]
    [InlineData("STRING", false, false, false, true, 0)]
    [InlineData("rw string:abc FUTURE-WORD", true, false, false, true, 0)]
    public void Type_words_are_parsed(string words, bool rw, bool isEnum, bool isRange, bool isString, int maxLength)
    {
        NutTypeDescription type = NutTypeParser.ParseTypeWords(words.Split(' '));

        Assert.Equal(new NutTypeDescription(rw, isEnum, isRange, isString, maxLength), type);
    }

    [Fact]
    public void Variable_info_is_built_from_type_enum_and_range()
    {
        var range = NutTypeParser.ParseTypeWords(["RW", "RANGE"])
            .ToVariableInfo([], NutTypeParser.ParseRanges([["ups", "v", "90", "100"], ["ups", "v", "bad", "1"], ["ups", "v"]]));
        Assert.Equal([new ValueRange("90", "100")], range.Ranges);
        Assert.Equal(VariableType.Number, range.Type);

        var enumeration = NutTypeParser.ParseTypeWords(["RW", "ENUM"])
            .ToVariableInfo(NutTypeParser.ParseEnumValues([["ups", "v", "103"], ["ups", "v", "100"], ["ups", "v", "103"]]), []);
        Assert.Equal(["103", "100"], enumeration.EnumValues);

        // An enum without values is text: the words of an enum are not always numbers.
        Assert.Equal(VariableType.String, NutTypeParser.ParseTypeWords(["RW", "ENUM"]).ToVariableInfo([], []).Type);
        Assert.True(NutTypeDescription.UnknownWritable.ToVariableInfo([], []).Writable);
    }

    [Theory]
    [InlineData("UNKNOWN-UPS", CommandStatus.DriverNotConnected)]
    [InlineData("CMD-NOT-SUPPORTED", CommandStatus.NotSupported)]
    [InlineData("VAR-NOT-SUPPORTED", CommandStatus.NotSupported)]
    [InlineData("READONLY", CommandStatus.ReadOnly)]
    [InlineData("INVALID-VALUE", CommandStatus.InvalidValue)]
    [InlineData("TOO-LONG", CommandStatus.TooLong)]
    [InlineData("INVALID-ARGUMENT", CommandStatus.InvalidArgument)]
    [InlineData("DATA-STALE", CommandStatus.DriverNotConnected)]
    [InlineData("USERNAME-REQUIRED", CommandStatus.AccessDenied)]
    [InlineData("INVALID-PASSWORD", CommandStatus.AccessDenied)]
    [InlineData("SET-FAILED", CommandStatus.Failed)]
    [InlineData("SOMETHING-NEW", CommandStatus.Failed)]
    public void Error_codes_map_to_command_statuses(string code, CommandStatus expected)
    {
        CommandResult result = NutErrorCodes.ToCommandResult(new NutErrorException(code), "nas:3493");

        Assert.Equal(expected, result.Status);
        Assert.Contains($"upstream nas:3493: ERR {code}", result.Message);
    }

    [Theory]
    [InlineData("Network UPS Tools upsd 2.8.1 - https://www.networkupstools.org/", "Network UPS Tools upsd 2.8.1")]
    [InlineData("  NutHub 1.0.0  ", "NutHub 1.0.0")]
    [InlineData("", null)]
    public void Version_is_shortened(string version, string? expected)
    {
        Assert.Equal(expected, NutUpstreamDriver.ShortenVersion(version));
    }
}
