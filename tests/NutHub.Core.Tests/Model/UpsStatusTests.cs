using NutHub.Core.Model;

namespace NutHub.Core.Tests.Model;

public sealed class UpsStatusTests
{
    [Theory]
    [InlineData("OL", UpsStatusFlags.Online)]
    [InlineData("OB DISCHRG", UpsStatusFlags.OnBattery | UpsStatusFlags.Discharging)]
    [InlineData("  ob   lb  ", UpsStatusFlags.OnBattery | UpsStatusFlags.LowBattery)]
    [InlineData("FSD OL CHRG", UpsStatusFlags.ForcedShutdown | UpsStatusFlags.Online | UpsStatusFlags.Charging)]
    [InlineData("OL BYPASS OVER TRIM BOOST CAL OFF RB HB ALARM TEST ECO",
        UpsStatusFlags.Online | UpsStatusFlags.Bypass | UpsStatusFlags.Overload | UpsStatusFlags.Trim |
        UpsStatusFlags.Boost | UpsStatusFlags.Calibrating | UpsStatusFlags.Off | UpsStatusFlags.ReplaceBattery |
        UpsStatusFlags.HighBattery | UpsStatusFlags.Alarm | UpsStatusFlags.Test | UpsStatusFlags.Eco)]
    [InlineData("OL WAIT UNKNOWNTOKEN", UpsStatusFlags.Online)]
    [InlineData("", UpsStatusFlags.None)]
    [InlineData(null, UpsStatusFlags.None)]
    public void Parse_reads_every_standard_token_and_ignores_unknown_ones(string? status, UpsStatusFlags expected) =>
        Assert.Equal(expected, UpsStatus.Parse(status));

    [Fact]
    public void SplitTokens_uppercases_and_removes_duplicates_keeping_order()
    {
        Assert.Equal(["OL", "CHRG", "WAIT"], UpsStatus.SplitTokens(" ol CHRG ol wait "));
        Assert.Empty(UpsStatus.SplitTokens("   "));
    }

    [Fact]
    public void Format_orders_tokens_with_FSD_first()
    {
        Assert.Equal("FSD OB LB DISCHRG",
                     UpsStatus.Format(UpsStatusFlags.Discharging | UpsStatusFlags.LowBattery | UpsStatusFlags.OnBattery |
                                      UpsStatusFlags.ForcedShutdown));
        Assert.Equal("", UpsStatus.Format(UpsStatusFlags.None));
    }

    [Fact]
    public void Tokens_round_trip()
    {
        foreach (UpsStatusFlags flag in Enum.GetValues<UpsStatusFlags>().Where(f => f != UpsStatusFlags.None))
        {
            string? token = UpsStatus.ToToken(flag);
            Assert.NotNull(token);
            Assert.Equal(flag, UpsStatus.FromToken(token!));
            Assert.Equal(flag, UpsStatus.FromToken(token!.ToLowerInvariant()));
        }

        Assert.Null(UpsStatus.ToToken(UpsStatusFlags.Online | UpsStatusFlags.OnBattery));
        Assert.Equal(UpsStatusFlags.None, UpsStatus.FromToken("NOPE"));
    }

    [Fact]
    public void Combine_puts_FSD_in_front_and_appends_other_tokens()
    {
        Assert.Equal("FSD OB DISCHRG", UpsStatus.Combine("OB DISCHRG", UpsStatusFlags.ForcedShutdown));
        Assert.Equal("OB DISCHRG LB", UpsStatus.Combine("OB DISCHRG", UpsStatusFlags.LowBattery));
        Assert.Equal("FSD OB LB", UpsStatus.Combine("OB", UpsStatusFlags.LowBattery | UpsStatusFlags.ForcedShutdown));
    }

    [Fact]
    public void Combine_keeps_unknown_tokens_and_does_not_duplicate()
    {
        Assert.Equal("OL WAIT", UpsStatus.Combine("OL WAIT", UpsStatusFlags.Online));
        Assert.Equal("FSD OL", UpsStatus.Combine("FSD OL", UpsStatusFlags.ForcedShutdown));
    }

    [Fact]
    public void Combine_removes_before_adding()
    {
        Assert.Equal("OB DISCHRG", UpsStatus.Combine("OB LB DISCHRG", UpsStatusFlags.None, UpsStatusFlags.LowBattery));
        // Removing and adding the same flag re-adds it at the end.
        Assert.Equal("OB DISCHRG LB",
                     UpsStatus.Combine("OB LB DISCHRG", UpsStatusFlags.LowBattery, UpsStatusFlags.LowBattery));
        Assert.Equal("LB", UpsStatus.Combine(null, UpsStatusFlags.LowBattery));
    }

    [Theory]
    [InlineData("OB LB", true)]
    [InlineData("FSD OL", true)]
    [InlineData("OB DISCHRG", false)]
    [InlineData("OL LB", false)]
    [InlineData("", false)]
    public void IsCritical_means_on_battery_with_low_battery_or_forced_shutdown(string status, bool critical) =>
        Assert.Equal(critical, UpsStatus.IsCritical(UpsStatus.Parse(status)));
}
