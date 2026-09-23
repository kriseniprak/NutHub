using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.Tests.Fakes;

/// <summary>An emulated UPS on the far end of a <see cref="FakeTransport"/>.</summary>
internal interface IFakeDevice
{
    /// <summary>One byte arrived from the driver; <paramref name="send"/> queues bytes for the driver to read.</summary>
    void Receive(byte value, Action<byte[]> send);
}

/// <summary>
/// An in-memory serial line to an <see cref="IFakeDevice"/>. Replies are produced synchronously while the driver writes,
/// and a read that finds no terminator returns at once as a timeout: tests never wait for real timeouts.
/// </summary>
internal sealed class FakeTransport : ISerialTransport
{
    private readonly object _sync = new();
    private readonly Queue<byte> _input = new();
    private IFakeDevice _device;

    public FakeTransport(IFakeDevice device)
    {
        _device = device;
    }

    public string Description => "fake0";

    public bool IsOpen { get; private set; }

    public int OpenCount { get; private set; }

    /// <summary>Thrown by the next opens while set: simulates a missing port, a permission problem...</summary>
    public TransportException? OpenError { get; set; }

    /// <summary>While set, writes fail as when a USB adapter is unplugged.</summary>
    public bool Broken { get; set; }

    public IFakeDevice Device
    {
        get => _device;
        set => _device = value;
    }

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        if (OpenError is { } error)
        {
            throw error;
        }

        lock (_sync)
        {
            _input.Clear();
            IsOpen = true;
            OpenCount++;
        }

        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        IsOpen = false;
        return Task.CompletedTask;
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        EnsureUsable();
        foreach (byte b in data.ToArray())
        {
            _device.Receive(b, Enqueue);
        }

        return Task.CompletedTask;
    }

    public Task<TransportRead> ReadUntilAsync(ReadOnlyMemory<byte> terminators, TimeSpan timeout, int maxLength,
                                              CancellationToken cancellationToken)
    {
        EnsureUsable();
        var data = new List<byte>();
        lock (_sync)
        {
            while (_input.Count > 0)
            {
                byte b = _input.Dequeue();
                data.Add(b);
                if (terminators.Span.IndexOf(b) >= 0)
                {
                    return Task.FromResult(new TransportRead(ReadStatus.Complete, [.. data]));
                }

                if (data.Count >= maxLength)
                {
                    return Task.FromResult(new TransportRead(ReadStatus.Overflow, [.. data]));
                }
            }
        }

        return Task.FromResult(new TransportRead(ReadStatus.Timeout, [.. data]));
    }

    public Task DiscardInputAsync(CancellationToken cancellationToken)
    {
        EnsureUsable();
        lock (_sync)
        {
            _input.Clear();
        }

        return Task.CompletedTask;
    }

    /// <summary>Bytes the device sends on its own (APC alert characters).</summary>
    public void Inject(string text) => Enqueue(Latin1(text));

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        return ValueTask.CompletedTask;
    }

    internal static byte[] Latin1(string text) => System.Text.Encoding.Latin1.GetBytes(text);

    private void Enqueue(byte[] bytes)
    {
        lock (_sync)
        {
            foreach (byte b in bytes)
            {
                _input.Enqueue(b);
            }
        }
    }

    private void EnsureUsable()
    {
        if (Broken)
        {
            throw new TransportException(TransportErrorKind.ConnectionLost, "The fake adapter was unplugged.");
        }

        if (!IsOpen)
        {
            throw new TransportException(TransportErrorKind.ConnectionLost, "fake0 is not open.");
        }
    }
}
