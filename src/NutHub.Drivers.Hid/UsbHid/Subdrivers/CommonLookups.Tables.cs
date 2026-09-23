// Value lookups ported from Network UPS Tools, drivers/usbhid-ups.c (GPL-2.0-or-later).
namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

internal static partial class CommonLookups
{
    public static readonly NutLookup OnlineInfo = NutLookup.Table([(1, "online"), (0, "offline")]);

    public static readonly NutLookup DischargingInfo = NutLookup.Table([(1, "dischrg"), (0, "!dischrg")]);

    public static readonly NutLookup ChargingInfo = NutLookup.Table([(1, "chrg"), (0, "!chrg")]);

    public static readonly NutLookup LowbattInfo = NutLookup.Table([(1, "lowbatt"), (0, "!lowbatt")]);

    public static readonly NutLookup OverloadInfo = NutLookup.Table([(1, "overload"), (0, "!overload")]);

    public static readonly NutLookup ReplacebattInfo = NutLookup.Table([(1, "replacebatt"), (0, "!replacebatt")]);

    public static readonly NutLookup TrimInfo = NutLookup.Table([(1, "trim"), (0, "!trim")]);

    public static readonly NutLookup BoostInfo = NutLookup.Table([(1, "boost"), (0, "!boost")]);

    public static readonly NutLookup BypassAutoInfo = NutLookup.Table([(1, "bypassauto"), (0, "!bypassauto")]);

    public static readonly NutLookup BypassManualInfo = NutLookup.Table([(1, "bypassman"), (0, "!bypassman")]);

    public static readonly NutLookup EcoModeInfo = NutLookup.Table([(0, "normal"), (1, "ecomode"), (2, "essmode")]);

    public static readonly NutLookup OffInfo = NutLookup.Table([(0, "off"), (1, "!off")]);

    public static readonly NutLookup CalibrationInfo = NutLookup.Table([(1, "cal"), (0, "!cal")]);

    public static readonly NutLookup NobatteryInfo = NutLookup.Table([(1, "!nobattery"), (0, "nobattery")]);

    public static readonly NutLookup FanfailInfo = NutLookup.Table([(1, "fanfail"), (0, "!fanfail")]);

    public static readonly NutLookup ShutdownimmInfo = NutLookup.Table([(1, "shutdownimm"), (0, "!shutdownimm")]);

    public static readonly NutLookup OverheatInfo = NutLookup.Table([(1, "overheat"), (0, "!overheat")]);

    public static readonly NutLookup AwaitingpowerInfo = NutLookup.Table([(1, "awaitingpower"), (0, "!awaitingpower")]);

    public static readonly NutLookup CommfaultInfo = NutLookup.Table([(1, "commfault"), (0, "!commfault")]);

    public static readonly NutLookup TimelimitexpiredInfo = NutLookup.Table([(1, "timelimitexp"), (0, "!timelimitexp")]);

    public static readonly NutLookup BattvoltloInfo = NutLookup.Table([(1, "battvoltlo"), (0, "!battvoltlo")]);

    public static readonly NutLookup BattvolthiInfo = NutLookup.Table([(1, "battvolthi"), (0, "!battvolthi")]);

    public static readonly NutLookup ChargerfailInfo = NutLookup.Table([(1, "chargerfail"), (0, "!chargerfail")]);

    public static readonly NutLookup FullychargedInfo = NutLookup.Table([(1, "fullycharged"), (0, "notfullycharged")]);

    public static readonly NutLookup DepletedInfo = NutLookup.Table([(1, "depleted"), (0, "!depleted")]);

    public static readonly NutLookup VrangeInfo = NutLookup.Table([(0, "!vrange"), (1, "vrange")]);

    public static readonly NutLookup FrangeInfo = NutLookup.Table([(0, "!frange"), (1, "frange")]);

    public static readonly NutLookup TestWriteInfo = NutLookup.Table([(0, "No test"), (1, "Quick test"), (2, "Deep test"), (3, "Abort test")]);

    public static readonly NutLookup TestReadInfo = NutLookup.Table([(1, "Done and passed"), (2, "Done and warning"), (3, "Done and error"), (4, "Aborted"), (5, "In progress"), (6, "No test initiated"), (7, "Test scheduled")]);

    public static readonly NutLookup BeeperInfo = NutLookup.Table([(1, "disabled"), (2, "enabled"), (3, "muted")]);

    public static readonly NutLookup YesNoInfo = NutLookup.Table([(0, "no"), (1, "yes")]);

    public static readonly NutLookup OnOffInfo = NutLookup.Table([(0, "off"), (1, "on")]);
}
