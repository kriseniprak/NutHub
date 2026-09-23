using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace NutHub.Drivers.Serial.Tests.Fakes;

/// <summary>
/// An APC Smart-UPS on its serial line: single-character queries answered with a value and CR LF, "NA" for unknown
/// ones, EEPROM values cycled with '-', ups.id / battery.date written with '-' and 8 characters, the power commands
/// that must be sent twice. The command set and capability string are shaped like those of a Smart-UPS 1000 (NUT
/// docs/apcsmart-protocol and apcupsd's Smart protocol notes).
/// </summary>
internal sealed class FakeApcSmartUps : IFakeDevice
{
    /// <summary>version.alerts.commands of a Smart-UPS with a 652.13.I firmware.</summary>
    public const string CommandSet =
        "3.!$%+?=#|.\u0001\u000E\u001A@ABCDEFGKLMNOPQRSUVWXYZ^abcefgjklmnopqrsuxyz~";

    /// <summary>Four transfer voltages each for 'u' and 'l', four delays each for 'p' and 'r', locale I.</summary>
    public const string Capabilities = "#uI43253264271280lI43196188208204pI43020180300600rI43000060180300";

    private readonly ConcurrentQueue<string> _received = new();
    private readonly StringBuilder _multi = new();
    private readonly Dictionary<char, string[]> _enums = new()
    {
        ['u'] = ["253", "264", "271", "280"],
        ['l'] = ["196", "188", "208", "204"],
        ['p'] = ["020", "180", "300", "600"],
        ['r'] = ["000", "060", "180", "300"],
    };

    private char _lastCommand;
    private char _lastRead;
    private char _multiKind;
    private int _multiLength;

    public FakeApcSmartUps()
    {
        SetOnLine();
    }

    /// <summary>The 'Q' status register: 0x08 on line.</summary>
    public int Status { get; set; } = 0x08;

    /// <summary>Replies to single-character queries, without CR LF.</summary>
    public ConcurrentDictionary<char, string> Values { get; } = new()
    {
        ['b'] = "652.13.I",
        ['\u0001'] = "Smart-UPS 1000",
        ['n'] = "AS0123456789",
        ['m'] = "03/15/19",
        ['x'] = "03/15/19",
        ['c'] = "UPS_IDEN",
        ['j'] = "0045:",
        ['L'] = "230.4",
        ['O'] = "230.4",
        ['F'] = "50.00",
        ['P'] = "023.4",
        ['B'] = "27.36",
        ['C'] = "031.5",
        ['X'] = "OK",
        ['G'] = "O",
        ['M'] = "235.2",
        ['N'] = "228.8",
        ['E'] = "336",
        ['e'] = "00",
        ['g'] = "024",
        ['k'] = "0",
        ['o'] = "230",
        ['q'] = "02",
        ['s'] = "H",
        ['u'] = "253",
        ['l'] = "196",
        ['p'] = "020",
        ['r'] = "000",
        ['9'] = "FF",
    };

    /// <summary>Every command received; multi-character ones ("@000", "-RACK1\r\r\r") as one entry.</summary>
    public IReadOnlyList<string> Received => [.. _received];

    public bool Silent { get; set; }

    /// <summary>Replaces the answer to 'Q' (a garbled line, for instance).</summary>
    public string? StatusReply { get; set; }

    public void SetOnLine()
    {
        Status = 0x08;
        Values['f'] = "100.0";
    }

    public void SetOnBattery(bool lowBattery, string charge = "054.0")
    {
        Status = 0x10 | (lowBattery ? 0x40 : 0);
        Values['f'] = charge;
    }

    public void Receive(byte value, Action<byte[]> send)
    {
        char c = (char)value;
        if (_multiKind != '\0')
        {
            ContinueMulti(c, send);
            return;
        }

        if (Silent)
        {
            _received.Enqueue(c.ToString());
            return;
        }

        char previous = _lastCommand;
        _lastCommand = c;
        switch (c)
        {
            case 'Y':
                Reply(send, "SM");
                break;
            case '\u001B':
                break;
            case 'a':
                Reply(send, CommandSet);
                break;
            case '\u001A':
                Reply(send, Capabilities);
                break;
            case 'Q':
                Reply(send, StatusReply ?? Status.ToString("X2", CultureInfo.InvariantCulture));
                break;
            case '@':
                StartMulti(c, 3);
                return;
            case '-':
                if (_lastRead is 'c' or 'x')
                {
                    StartMulti(c, 8);
                    return;
                }

                if (_enums.TryGetValue(_lastRead, out string[]? cycle))
                {
                    int index = Array.IndexOf(cycle, Values[_lastRead]);
                    Values[_lastRead] = cycle[(index + 1) % cycle.Length];
                    Reply(send, "OK");
                }
                else
                {
                    Reply(send, "NO");
                }

                break;
            case 'K':
                if (previous == 'K')
                {
                    Reply(send, "OK");
                    _lastCommand = '\0';
                }

                break;
            case 'Z' or '\u000E':
                break;
            case 'S':
                Reply(send, (Status & 0x10) != 0 ? "OK" : "NA");
                break;
            case 'U':
                Status = 0x10;
                break;
            case 'W' or 'A':
                Reply(send, "OK");
                break;
            case 'D':
                Status ^= 0x01;
                Reply(send, "OK");
                break;
            case '^':
                Reply(send, "BYP");
                break;
            default:
                _lastRead = c;
                Reply(send, Values.TryGetValue(c, out string? v) ? v : "NA");
                break;
        }

        _received.Enqueue(c.ToString());
    }

    private void StartMulti(char kind, int length)
    {
        _multiKind = kind;
        _multiLength = length;
        _multi.Clear().Append(kind);
    }

    private void ContinueMulti(char c, Action<byte[]> send)
    {
        _multi.Append(c);
        if (_multi.Length <= _multiLength)
        {
            return;
        }

        string command = _multi.ToString();
        char kind = _multiKind;
        _multiKind = '\0';
        _received.Enqueue(command);
        if (kind == '-')
        {
            Values[_lastRead] = command[1..].TrimEnd('\r');
        }

        Reply(send, "OK");
    }

    private static void Reply(Action<byte[]> send, string text) => send(FakeTransport.Latin1(text + "\r\n"));
}
