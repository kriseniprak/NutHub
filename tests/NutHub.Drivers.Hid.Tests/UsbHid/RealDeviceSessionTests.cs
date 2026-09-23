using NutHub.Drivers.Hid.Tests.Support;

namespace NutHub.Drivers.Hid.Tests.UsbHid;

/// <summary>
/// The whole mapping on the reports of real UPSes (see the files in TestData). The expected values are those NUT
/// published for the same reports, except where the NUT issue the dump comes from was about a wrong value that
/// NUT has fixed since: those assertions check the corrected value.
/// </summary>
public sealed class RealDeviceSessionTests
{
    [Fact]
    public void Apc_back_ups_xs_1400u()
    {
        IReadOnlyDictionary<string, string> vars = Read(RealDeviceDump.ApcBackUpsXs1400U, "American Power Conversion",
                                                        "Back-UPS XS 1400U  FW:926.T1 .I USB FW:T1", "3B1527X15613", "apc");

        Assert.Equal("OL", vars["ups.status"]);
        Assert.Equal("100", vars["battery.charge"]);
        Assert.Equal("1464", vars["battery.runtime"]);
        Assert.Equal("27.1", vars["battery.voltage"]);
        Assert.Equal("24.0", vars["battery.voltage.nominal"]);
        Assert.Equal("10", vars["battery.charge.low"]);
        Assert.Equal("120", vars["battery.runtime.low"]);
        Assert.Equal("2015/07/02", vars["battery.mfr.date"]);
        Assert.Equal("2001/09/25", vars["battery.date"]);
        Assert.Equal("PbAc", vars["battery.type"]);
        Assert.Equal("Back-UPS XS 1400U", vars["ups.model"]);
        Assert.Equal("926.T1 .I", vars["ups.firmware"]);
        Assert.Equal("T1", vars["ups.firmware.aux"]);
        Assert.Equal("enabled", vars["ups.beeper.status"]);
        Assert.Equal("No test initiated", vars["ups.test.result"]);
        Assert.Equal("280", vars["input.transfer.high"]);
        Assert.Equal("155", vars["input.transfer.low"]);
        Assert.Equal("medium", vars["input.sensitivity"]);
        Assert.Equal("700", vars["ups.realpower.nominal"]);
        Assert.Equal("-1", vars["ups.timer.shutdown"]);

        // NUT issue 1189: the descriptor declares input voltage limits too small for 230 V; repaired, it reads 226 V, not 98.
        Assert.Equal("226.0", vars["input.voltage"]);
        Assert.Equal("230", vars["input.voltage.nominal"]);
    }

    [Fact]
    public void Eaton_3s_700()
    {
        IReadOnlyDictionary<string, string> vars = Read(RealDeviceDump.Eaton3S700, "EATON", "Eaton 3S", "Blank", "mge");

        Assert.Equal("OL CHRG", vars["ups.status"]);
        Assert.Equal("92", vars["battery.charge"]);
        Assert.Equal("2737", vars["battery.runtime"]);
        Assert.Equal("Eaton 3S 700", vars["ups.model"]);
        Assert.Equal("230.0", vars["output.voltage"]);
        Assert.Equal("230", vars["output.voltage.nominal"]);
        Assert.Equal("50", vars["output.frequency.nominal"]);
        Assert.Equal("264", vars["input.transfer.high"]);
        Assert.Equal("184", vars["input.transfer.low"]);
        Assert.Equal("disabled", vars["ups.beeper.status"]);
        Assert.Equal("700", vars["ups.power.nominal"]);
        Assert.Equal("offline / line interactive", vars["ups.type"]);
        Assert.Equal("-1", vars["ups.timer.shutdown"]);
    }

    [Fact]
    public void Eaton_5sc_750()
    {
        IReadOnlyDictionary<string, string> vars = Read(RealDeviceDump.Eaton5Sc750, "EATON", "Eaton 5SC", "G000000000", "mge");

        Assert.Equal("OL", vars["ups.status"]);
        Assert.Equal("100", vars["battery.charge"]);
        Assert.Equal("Eaton 5SC 750", vars["ups.model"]);
        Assert.Equal("229.0", vars["input.voltage"]);
        Assert.Equal("228.4", vars["output.voltage"]);
        Assert.Equal("49.9", vars["input.frequency"]);
        Assert.Equal("Done and passed", vars["ups.test.result"]);
        Assert.Equal("enabled", vars["ups.beeper.status"]);
        Assert.Equal("yes", vars["ups.start.auto"]);
    }

    [Fact]
    public void CyberPower_pr3000elcdsl()
    {
        IReadOnlyDictionary<string, string> vars = Read(RealDeviceDump.CyberPowerPr3000, "CPS", "PR3000ELCDSL", "PTJNS2000086", "cps");

        Assert.Equal("OL", vars["ups.status"]);
        Assert.Equal("100", vars["battery.charge"]);
        Assert.Equal("5790", vars["battery.runtime"]);
        Assert.Equal("225.0", vars["output.voltage"]);
        Assert.Equal("50.0", vars["input.frequency"]);
        Assert.Equal("249", vars["input.transfer.high"]);
        Assert.Equal("208", vars["input.transfer.low"]);
        Assert.Equal("Done and passed", vars["ups.test.result"]);
        Assert.Equal("disabled", vars["ups.beeper.status"]);
        Assert.Equal("2700", vars["ups.realpower.nominal"]);

        // NUT issue 3089: 2.8.4 showed a constant 70 V input; the descriptor repair gives the real reading.
        Assert.Equal("225.0", vars["input.voltage"]);
        Assert.Equal("230", vars["input.voltage.nominal"]);
    }

    [Fact]
    public void Commands_of_a_real_ups_write_the_reports_nut_writes()
    {
        RealDeviceDump dump = RealDeviceDump.Load(RealDeviceDump.ApcBackUpsXs1400U);
        FakeHidDevice device = TestSessions.FromDump(dump, "American Power Conversion", "Back-UPS XS 1400U", "3B1527X15613");
        var (session, _) = TestSessions.Open(device);

        Assert.True(session.ExecuteCommand("test.battery.start.quick", null).IsSuccess);
        Assert.True(session.ExecuteCommand("beeper.disable", null).IsSuccess);

        // apc-hid.c: UPS.Battery.Test = 1 (report 0x21) and UPS.PowerSummary.AudibleAlarmControl = 1 (report 0x18).
        Assert.Equal(new byte[][] { [0x21, 0x01], [0x18, 0x01] }, device.Writes);
    }

    private static IReadOnlyDictionary<string, string> Read(string name, string mfr, string product, string serial, string subdriver)
    {
        RealDeviceDump dump = RealDeviceDump.Load(name);
        var (session, _) = TestSessions.Open(TestSessions.FromDump(dump, mfr, product, serial));
        session.Update();
        Assert.Equal(subdriver, session.Subdriver.Id);
        return TestSessions.Variables(session);
    }
}
