using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NutHub.Drivers.Net.Tests.Fakes;

/// <summary>
/// An in-process apcupsd network information server on 127.0.0.1: answers "status" with one length-prefixed record
/// per line and a zero-length terminator, like apcupsd's apcnis.c.
/// </summary>
public sealed class FakeApcupsd : IAsyncDisposable
{
    /// <summary>The documented apcaccess example of a Back-UPS RS 1500 on USB (apcupsd user manual).</summary>
    public const string BackUpsStatus = """
        APC      : 001,036,0879
        DATE     : 2009-03-02 13:52:44 +0000
        HOSTNAME : dev
        VERSION  : 3.14.6 (16 May 2009) redhat
        UPSNAME  : dev
        CABLE    : USB Cable
        MODEL    : Back-UPS RS 1500
        UPSMODE  : Stand Alone
        STARTTIME: 2009-02-28 16:14:17 +0000
        STATUS   : ONLINE
        LINEV    : 123.0 Volts
        LOADPCT  :  24.0 Percent Load Capacity
        BCHARGE  : 100.0 Percent
        TIMELEFT :  52.3 Minutes
        MBATTCHG : 5 Percent
        MINTIMEL : 3 Minutes
        MAXTIME  : 0 Seconds
        SENSE    : Medium
        LOTRANS  : 097.0 Volts
        HITRANS  : 138.0 Volts
        ALARMDEL : 30 seconds
        BATTV    : 27.0 Volts
        LASTXFER : Automatic or explicit self test
        NUMXFERS : 0
        TONBATT  : 0 seconds
        CUMONBATT: 0 seconds
        XOFFBATT : N/A
        SELFTEST : NO
        STATFLAG : 0x07000008 Status Flag
        SERIALNO : JB0520005612
        BATTDATE : 2005-05-04
        NOMINV   : 120 Volts
        NOMBATTV :  24.0 Volts
        FIRMWARE : 8.g9 .D USB FW:g9
        APCMODEL : Back-UPS RS 1500
        END APC  : 2009-03-02 13:52:48 +0000
        """;

    /// <summary>A Smart-UPS on battery with a low battery, with the fields Smart-UPS models add.</summary>
    public const string SmartUpsOnBattery = """
        APC      : 001,043,1032
        DATE     : 2024-01-10 10:15:02 +0100
        HOSTNAME : server1
        VERSION  : 3.14.14 (31 May 2016) debian
        UPSNAME  : rack ups
        CABLE    : Custom Cable Smart
        DRIVER   : APC Smart UPS (any)
        MODEL    : Smart-UPS 1500
        STATUS   : ONBATT LOWBATT
        LINEV    : 000.0 Volts
        LOADPCT  : 31.2 Percent
        BCHARGE  : 012.0 Percent
        TIMELEFT : 2.5 Minutes
        MBATTCHG : 10 Percent
        MINTIMEL : 5 Minutes
        MAXLINEV : 235.2 Volts
        MINLINEV : 000.0 Volts
        OUTPUTV  : 230.4 Volts
        SENSE    : High
        DWAKE    : 0 Seconds
        DSHUTD   : 90 Seconds
        LOTRANS  : 208.0 Volts
        HITRANS  : 253.0 Volts
        RETPCT   : 15.0 Percent
        ITEMP    : 29.2 C
        BATTV    : 24.1 Volts
        LINEFREQ : 50.0 Hz
        LASTXFER : Line voltage notch or spike
        SELFTEST : OK
        STATFLAG : 0x05060050
        SERIALNO : AS1234567890
        BATTDATE : 2021-06-01
        NOMOUTV  : 230 Volts
        NOMPOWER : 980 Watts
        NOMAPNT  : 1500 VA
        MANDATE  : 2016-03-14
        FIRMWARE : UPS 09.3 / ID=18
        END APC  : 2024-01-10 10:15:05 +0100
        """;

    private TcpListener? _listener;
    private CancellationTokenSource? _stop;

    public FakeApcupsd(string status)
    {
        Status = status;
    }

    /// <summary>The apcaccess-style text sent for "status"; can change while running.</summary>
    public volatile string Status;

    /// <summary>Send a record header announcing more bytes than any real record.</summary>
    public volatile bool SendOversizedRecord;

    public int Port { get; private set; }

    public int Requests;

    public void Start(int port = 0)
    {
        _stop = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(listener, _stop.Token);
    }

    public void Stop()
    {
        _stop?.Cancel();
        _listener?.Stop();
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        _stop?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop);
            }
            catch (Exception)
            {
                return;
            }

            _ = ServeAsync(client, stop);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                byte[] header = new byte[2];
                while (true)
                {
                    await stream.ReadExactlyAsync(header, stop);
                    byte[] command = new byte[BinaryPrimitives.ReadUInt16BigEndian(header)];
                    await stream.ReadExactlyAsync(command, stop);
                    Interlocked.Increment(ref Requests);
                    if (Encoding.ASCII.GetString(command) != "status")
                    {
                        await SendRecordAsync(stream, "Invalid command\n", stop);
                        await stream.WriteAsync(new byte[2], stop);
                        continue;
                    }

                    if (SendOversizedRecord)
                    {
                        await stream.WriteAsync(new byte[] { 0xFF, 0xFF }, stop);
                        return;
                    }

                    foreach (string line in Status.Split('\n'))
                    {
                        await SendRecordAsync(stream, line.TrimEnd('\r') + "\n", stop);
                    }

                    await stream.WriteAsync(new byte[2], stop);
                }
            }
            catch (Exception)
            {
                // Client gone.
            }
        }
    }

    private static async Task SendRecordAsync(NetworkStream stream, string text, CancellationToken stop)
    {
        byte[] body = Encoding.ASCII.GetBytes(text);
        byte[] record = new byte[2 + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(record, (ushort)body.Length);
        body.CopyTo(record, 2);
        await stream.WriteAsync(record, stop);
    }
}
