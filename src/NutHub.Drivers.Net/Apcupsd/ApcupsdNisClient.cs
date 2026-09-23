using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using NutHub.Drivers.Net.Common;

namespace NutHub.Drivers.Net.Apcupsd;

/// <summary>
/// Reads the status of an apcupsd daemon through its Network Information Server (NIS, TCP 3551), the protocol of
/// apcaccess. A request is a 2-byte big-endian length followed by the command ("status"); the answer is a series of
/// records, each a 2-byte big-endian length followed by that many bytes of text ("MODEL    : Back-UPS RS 1500\n"),
/// ended by a zero-length record.
/// <para>
/// A new connection is opened for every read, as NUT's apcupsd-ups driver and apcaccess do: the daemon serves each
/// connection in its own thread, and a short-lived connection cannot be left in a half-read state.
/// </para>
/// </summary>
internal sealed class ApcupsdNisClient(string host, int port, TimeSpan timeout, TimeProvider time)
{
    /// <summary>Longest accepted record; real ones are under 100 bytes, apcupsd's own buffer is 1 KiB.</summary>
    internal const int MaxRecordLength = 4096;

    /// <summary>Most records accepted in one status, against a peer that never sends the terminator.</summary>
    internal const int MaxRecords = 1000;

    private static readonly byte[] StatusRequest = BuildRequest("status");

    public string Endpoint { get; } = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]:{port}" : $"{host}:{port}";

    /// <summary>Fetches the status and returns its lines (without line terminators, empty lines removed).</summary>
    public async Task<IReadOnlyList<string>> FetchStatusAsync(CancellationToken cancellationToken)
    {
        using Socket socket = await TcpConnector.ConnectAsync(host, port, timeout, time, cancellationToken)
            .ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var scope = new TimeoutScope(timeout, time, cancellationToken);
        try
        {
            await stream.WriteAsync(StatusRequest, scope.Token).ConfigureAwait(false);
            await stream.FlushAsync(scope.Token).ConfigureAwait(false);

            var lines = new List<string>();
            byte[] header = new byte[2];
            byte[] record = new byte[MaxRecordLength];
            for (int count = 0; ; count++)
            {
                if (count >= MaxRecords)
                {
                    throw new InvalidDataException($"apcupsd at {Endpoint} sent more than {MaxRecords} records");
                }

                await stream.ReadExactlyAsync(header, scope.Token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadUInt16BigEndian(header);
                if (length == 0)
                {
                    return lines;
                }

                if (length > MaxRecordLength)
                {
                    throw new InvalidDataException(
                        $"apcupsd at {Endpoint} sent a record of {length} bytes; is this really an apcupsd server?");
                }

                await stream.ReadExactlyAsync(record.AsMemory(0, length), scope.Token).ConfigureAwait(false);

                // apcupsd sends one line per record, but nothing in the protocol forbids several.
                foreach (string line in Encoding.UTF8.GetString(record, 0, length)
                             .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    lines.Add(line);
                }
            }
        }
        catch (OperationCanceledException) when (scope.TimedOut)
        {
            throw new TimeoutException($"no complete answer from {Endpoint} within {timeout.TotalSeconds:0.#} s");
        }
        catch (EndOfStreamException)
        {
            throw new IOException($"{Endpoint} closed the connection before the end of the status");
        }
    }

    private static byte[] BuildRequest(string command)
    {
        byte[] text = Encoding.ASCII.GetBytes(command);
        byte[] request = new byte[2 + text.Length];
        BinaryPrimitives.WriteUInt16BigEndian(request, (ushort)text.Length);
        text.CopyTo(request, 2);
        return request;
    }
}
