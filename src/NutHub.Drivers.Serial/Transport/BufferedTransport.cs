namespace NutHub.Drivers.Serial.Transport;

/// <summary>
/// Implements the line-oriented reads of <see cref="ISerialTransport"/> on top of a raw "read what is there" primitive,
/// keeping the bytes that follow a terminator for the next read. The concrete transports only move bytes.
/// </summary>
internal abstract class BufferedTransport : ISerialTransport
{
    private const int ChunkSize = 256;

    private readonly byte[] _chunk = new byte[ChunkSize];
    private byte[] _pending = new byte[ChunkSize];
    private int _pendingCount;

    protected BufferedTransport(TimeProvider time)
    {
        Time = time;
    }

    public abstract string Description { get; }

    public abstract bool IsOpen { get; }

    protected TimeProvider Time { get; }

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (IsOpen)
        {
            await CloseAsync().ConfigureAwait(false);
        }

        _pendingCount = 0;
        await OpenCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseAsync()
    {
        _pendingCount = 0;
        await CloseCoreAsync().ConfigureAwait(false);
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        EnsureOpen();
        return WriteCoreAsync(data, cancellationToken);
    }

    public async Task<TransportRead> ReadUntilAsync(ReadOnlyMemory<byte> terminators, TimeSpan timeout, int maxLength,
                                                    CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        EnsureOpen();

        var result = new List<byte>(Math.Min(maxLength, 128));
        long started = Time.GetTimestamp();
        while (true)
        {
            // Consume what is already buffered before touching the device.
            int consumed = 0;
            while (consumed < _pendingCount)
            {
                byte b = _pending[consumed++];
                result.Add(b);
                if (terminators.Span.IndexOf(b) >= 0)
                {
                    Consume(consumed);
                    return new TransportRead(ReadStatus.Complete, [.. result]);
                }

                if (result.Count >= maxLength)
                {
                    Consume(consumed);
                    return new TransportRead(ReadStatus.Overflow, [.. result]);
                }
            }

            Consume(consumed);

            TimeSpan remaining = timeout - Time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return new TransportRead(ReadStatus.Timeout, [.. result]);
            }

            int read = await ReadSomeAsync(_chunk, remaining, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return new TransportRead(ReadStatus.Timeout, [.. result]);
            }

            Append(_chunk.AsSpan(0, read));
        }
    }

    public async Task DiscardInputAsync(CancellationToken cancellationToken)
    {
        EnsureOpen();
        _pendingCount = 0;
        await DiscardCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected abstract Task OpenCoreAsync(CancellationToken cancellationToken);

    protected abstract Task CloseCoreAsync();

    protected abstract Task WriteCoreAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for data and returns what arrived (at least one byte), or 0 on timeout.
    /// Throws <see cref="TransportException"/> when the link is broken.
    /// </summary>
    protected abstract Task<int> ReadSomeAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Drops the bytes the operating system or device already holds for us.</summary>
    protected abstract Task DiscardCoreAsync(CancellationToken cancellationToken);

    private void EnsureOpen()
    {
        if (!IsOpen)
        {
            throw new TransportException(TransportErrorKind.ConnectionLost, $"{Description} is not open.");
        }
    }

    private void Consume(int count)
    {
        if (count <= 0)
        {
            return;
        }

        _pendingCount -= count;
        if (_pendingCount > 0)
        {
            Buffer.BlockCopy(_pending, count, _pending, 0, _pendingCount);
        }
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_pendingCount + data.Length > _pending.Length)
        {
            Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _pendingCount + data.Length));
        }

        data.CopyTo(_pending.AsSpan(_pendingCount));
        _pendingCount += data.Length;
    }
}
