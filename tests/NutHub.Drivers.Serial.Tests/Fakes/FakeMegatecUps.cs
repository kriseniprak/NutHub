using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NutHub.Drivers.Serial.Tests.Fakes;

/// <summary>
/// A Megatec Q1 UPS as seen on its serial line: queries answered from a table, action commands (S, C, T, Q...)
/// accepted silently, anything else echoed back as the real firmware does. Replies are the examples of NUT
/// drivers/nutdrv_qx_megatec.c.
/// </summary>
internal sealed partial class FakeMegatecUps : IFakeDevice
{
    public const string OnLineStatus = "(226.0 195.0 226.0 014 49.0 27.5 30.0 00001000\r";
    public const string Rating = "#220.0 000 024.0 50.0\r";
    /// <summary>"#Company_Name(15) UPS_Model(10) Version(10)\r".</summary>
    public static readonly string Vendor =
        "#" + "MegaTec".PadRight(15) + " " + "UPS-1000".PadRight(10) + " " + "V1.0".PadRight(10) + "\r";

    private readonly StringBuilder _line = new();
    private readonly ConcurrentQueue<string> _received = new();

    public FakeMegatecUps()
    {
        Replies["Q1"] = OnLineStatus;
        Replies["F"] = Rating;
        Replies["I"] = Vendor;
    }

    /// <summary>Command (without CR) to reply (with CR).</summary>
    public ConcurrentDictionary<string, string> Replies { get; } = new(StringComparer.Ordinal);

    /// <summary>Every command received, without its CR, in order.</summary>
    public IReadOnlyList<string> Received => [.. _received];

    /// <summary>A UPS that is switched off, or a broken cable: nothing comes back.</summary>
    public bool Silent { get; set; }

    /// <summary>Sets the Q1 reply for a UPS on battery (utility fail bit), optionally with a low battery.</summary>
    public void SetOnBattery(double batteryVoltage, bool lowBattery, int load = 20) =>
        Replies["Q1"] = Status(0, 0, 230, load, 50, batteryVoltage, 30, lowBattery ? "11000000" : "10000000");

    public void SetOnLine(double batteryVoltage = 27.5, int load = 14) =>
        Replies["Q1"] = Status(226, 195, 226, load, 49, batteryVoltage, 30, "00001000");

    /// <summary>Formats a Q1 reply "(MMM.M NNN.N PPP.P QQQ RR.R SS.S TT.T b7b6b5b4b3b2b1b0".</summary>
    public static string Status(double input, double fault, double output, int load, double frequency, double battery,
                                double temperature, string bits) =>
        string.Create(CultureInfo.InvariantCulture,
            $"({input:000.0} {fault:000.0} {output:000.0} {load:000} {frequency:00.0} {battery:00.0} {temperature:00.0} {bits}\r");

    public void Receive(byte value, Action<byte[]> send)
    {
        if (value != '\r')
        {
            _line.Append((char)value);
            return;
        }

        string command = _line.ToString();
        _line.Clear();
        _received.Enqueue(command);
        if (Silent)
        {
            return;
        }

        if (Replies.TryGetValue(command, out string? reply))
        {
            send(FakeTransport.Latin1(reply));
        }
        else if (!ActionCommand().IsMatch(command))
        {
            send(FakeTransport.Latin1(command + "\r"));
        }
    }

    public void ClearReceived() => _received.Clear();

    [GeneratedRegex(@"^(Q|T|TL|T[0-9]{2}|CT|C|S\.[0-9]|S[0-9]{2}|S(\.[0-9]|[0-9]{2})R[0-9]{4})$")]
    private static partial Regex ActionCommand();
}
