using System.Text;
using NutHub.Core.Model;

namespace NutHub.Services.Notifications;

/// <summary>
/// The upsmon NOTIFYTYPE names (clients/upsmon.c), so NOTIFYCMD scripts written for upsmon work unchanged as command
/// hooks. Events upsmon does not know get an upper-case name of their own (ShutdownPending: SHUTDOWN_PENDING).
/// </summary>
internal static class NotifyTypes
{
    public static string For(UpsEventType type) => type switch
    {
        UpsEventType.Online => "ONLINE",
        UpsEventType.OnBattery => "ONBATT",
        UpsEventType.LowBattery => "LOWBATT",
        UpsEventType.ForcedShutdown => "FSD",
        UpsEventType.CommunicationRestored => "COMMOK",
        UpsEventType.CommunicationLost => "COMMBAD",
        UpsEventType.ShutdownStarted => "SHUTDOWN",
        UpsEventType.ReplaceBattery => "REPLBATT",
        UpsEventType.NoCommunication => "NOCOMM",
        UpsEventType.Off => "OFF",
        UpsEventType.OffCleared => "NOTOFF",
        UpsEventType.Bypass => "BYPASS",
        UpsEventType.BypassCleared => "NOTBYPASS",
        UpsEventType.Overload => "OVER",
        UpsEventType.OverloadCleared => "NOTOVER",
        UpsEventType.Calibration => "CAL",
        UpsEventType.CalibrationEnded => "NOTCAL",
        UpsEventType.Trim => "TRIM",
        UpsEventType.TrimEnded => "NOTTRIM",
        UpsEventType.Boost => "BOOST",
        UpsEventType.BoostEnded => "NOTBOOST",
        UpsEventType.Alarm => "ALARM",
        UpsEventType.AlarmCleared => "NOTALARM",
        _ => ToUpperSnake(type.ToString()),
    };

    private static string ToUpperSnake(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                sb.Append('_');
            }

            sb.Append(char.ToUpperInvariant(name[i]));
        }

        return sb.ToString();
    }
}
