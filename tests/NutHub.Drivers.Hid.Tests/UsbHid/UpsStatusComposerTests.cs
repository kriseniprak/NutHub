using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NutHub.Drivers.Hid.UsbHid;
using static NutHub.Drivers.Hid.UsbHid.UpsStatusBits;

namespace NutHub.Drivers.Hid.Tests.UsbHid;

public sealed class UpsStatusComposerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("Online", null, "OL")]
    [InlineData("Online, Charging", "85", "OL CHRG")]
    [InlineData("Online, Charging", "100", "OL")] // charging bit set but full: NUT trusts the charge
    [InlineData("Online, Charging, FullyCharged", "85", "OL")]
    [InlineData("Online, Charging, NotFullyCharged", "100", "OL CHRG")]
    [InlineData("Offline, Discharging", "60", "OB DISCHRG")]
    [InlineData("Discharging", "60", "OB DISCHRG")] // no power state: discharging means on battery
    [InlineData("Offline, Discharging, LowBattery", "8", "OB DISCHRG LB")]
    [InlineData("Offline, ShutdownImminent", null, "OB LB")]
    [InlineData("Offline, TimeLimitExpired", null, "OB LB")]
    [InlineData("Offline, Discharging, Depleted", null, "OB")]
    [InlineData("Online, Overload, Boost", null, "OL OVER BOOST")]
    [InlineData("Online, Trim, BypassAuto", null, "OL TRIM BYPASS")]
    [InlineData("Online, ReplaceBattery", null, "OL RB")]
    [InlineData("Online, NoBattery", null, "OL RB")]
    [InlineData("Online, Off", null, "OL OFF")]
    [InlineData("Online, Calibrating, Discharging", null, "CAL OL DISCHRG")]
    public void Combines_the_flags_like_usbhid_ups(string flags, string? charge, string expected)
    {
        var composer = new UpsStatusComposer(_time, NullLogger.Instance, false, false);

        Assert.Equal(expected, string.Join(' ', composer.Compose(Enum.Parse<UpsStatusBits>(flags), charge, "test").Tokens));
    }

    [Fact]
    public void Online_and_discharging_is_on_line_by_default()
    {
        var composer = new UpsStatusComposer(_time, NullLogger.Instance, onlineDischargeOnBattery: false, onlineDischargeCalibration: false);

        Assert.Equal(["OL", "DISCHRG"], composer.Compose(Online | Discharging, "90", "test").Tokens);
    }

    [Fact]
    public void Online_and_discharging_can_mean_on_battery()
    {
        var composer = new UpsStatusComposer(_time, NullLogger.Instance, onlineDischargeOnBattery: true, onlineDischargeCalibration: false);

        Assert.Equal(["OB", "DISCHRG"], composer.Compose(Online | Discharging, "90", "test").Tokens);
    }

    [Fact]
    public void Online_and_discharging_can_mean_calibrating()
    {
        var composer = new UpsStatusComposer(_time, NullLogger.Instance, onlineDischargeOnBattery: false, onlineDischargeCalibration: true);

        Assert.Equal(["CAL", "DISCHRG"], composer.Compose(Online | Discharging, "90", "test").Tokens);
    }

    [Fact]
    public void Low_battery_during_calibration_waits_for_the_configured_delay()
    {
        var composer = new UpsStatusComposer(_time, NullLogger.Instance, false, false, lowReplaceDelaySeconds: 3);
        UpsStatusBits bits = Online | Calibrating | Discharging | LowBattery;

        Assert.DoesNotContain("LB", composer.Compose(bits, "30", "test").Tokens);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("LB", composer.Compose(bits, "30", "test").Tokens);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains("LB", composer.Compose(bits, "30", "test").Tokens);
    }

    [Fact]
    public void Low_battery_on_battery_is_never_delayed()
    {
        var composer = new UpsStatusComposer(_time, NullLogger.Instance, false, false, lowReplaceDelaySeconds: 3);

        Assert.Contains("LB", composer.Compose(Offline | Calibrating | Discharging | LowBattery, "30", "test").Tokens);
    }

    [Fact]
    public void Alarms_and_transfer_reason_follow_the_flags()
    {
        var composer = new UpsStatusComposer(_time, NullLogger.Instance, false, false);

        ComposedStatus status = composer.Compose(Online | ReplaceBattery | Overheat | VoltageOutOfRange | EcoMode, null, "test");

        Assert.Equal(["Replace battery!", "Temperature too high!"], status.Alarms);
        Assert.Equal("input voltage out of range", status.TransferReason);
        Assert.Equal(["vendor:default:ECO"], status.Buzzwords);
    }

    [Theory]
    [InlineData("online", "Offline", "Online")]
    [InlineData("offline", "Online", "Offline")]
    [InlineData("!dischrg", "Discharging, Charging", "Charging")]
    [InlineData("fullycharged", "NotFullyCharged", "FullyCharged")]
    [InlineData("normal", "Charging", "Charging")] // unknown names change nothing
    public void Flag_names_set_and_clear_bits(string name, string before, string after)
    {
        Assert.Equal(Enum.Parse<UpsStatusBits>(after), UpsStatusBitNames.Apply(Enum.Parse<UpsStatusBits>(before), name));
    }
}
