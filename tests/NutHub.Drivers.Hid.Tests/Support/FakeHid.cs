using System.Collections.Concurrent;
using NutHub.Drivers.Hid.Transport;

namespace NutHub.Drivers.Hid.Tests.Support;

/// <summary>
/// A HID device in memory: feature reports by id, a queue of input reports, and switches to make it vanish, refuse
/// access or stop answering. Thread-safe, because the driver uses it from its device thread while the test changes it.
/// </summary>
internal sealed class FakeHidDevice
{
    private readonly ConcurrentDictionary<byte, byte[]> _features = new();
    private readonly BlockingCollection<byte[]> _inputs = new();
    private readonly ConcurrentQueue<byte[]> _writes = new();
    private readonly ManualResetEventSlim _answering = new(true);
    private int _opens;

    public FakeHidDevice(HidDeviceInfo info, byte[] descriptor, IEnumerable<byte[]>? features = null, int maxInputLength = 0)
    {
        Info = info;
        Descriptor = descriptor;
        MaxInputLength = maxInputLength;
        foreach (byte[] report in features ?? [])
        {
            _features[report[0]] = report;
        }
    }

    public HidDeviceInfo Info { get; set; }

    public byte[] Descriptor { get; }

    public int MaxInputLength { get; }

    /// <summary>False: unplugged. The device disappears from the list and every call fails as lost.</summary>
    public volatile bool Present = true;

    /// <summary>True: the platform refuses to open the device or read its descriptor.</summary>
    public volatile bool DenyAccess;

    /// <summary>
    /// True: the descriptor is readable but opening is refused, the usual case (Linux publishes descriptors in
    /// sysfs; Windows reads them without opening the device for I/O).
    /// </summary>
    public volatile bool DenyOpen;

    /// <summary>True: feature requests fail as with a hung firmware (the device stays listed).</summary>
    public volatile bool FailFeatures;

    public int Opens => Volatile.Read(ref _opens);

    /// <summary>Feature requests block until <see cref="Resume"/>, like a firmware that locked up.</summary>
    public void Hang() => _answering.Reset();

    public void Resume() => _answering.Set();

    /// <summary>Every SetFeature buffer, in order.</summary>
    public IReadOnlyList<byte[]> Writes => [.. _writes];

    public void SetFeature(params byte[] report) => _features[report[0]] = report;

    public byte[]? GetStoredFeature(byte id) => _features.TryGetValue(id, out byte[]? r) ? r : null;

    public void QueueInput(params byte[] report) => _inputs.Add(report);

    internal void CountOpen() => Interlocked.Increment(ref _opens);

    internal byte[] ReadFeature(byte id, int length)
    {
        _answering.Wait();
        ThrowIfGone();
        if (FailFeatures || !_features.TryGetValue(id, out byte[]? report))
        {
            throw new IOException($"Report 0x{id:x2} stalled.");
        }

        var copy = new byte[Math.Max(length, report.Length)];
        report.CopyTo(copy, 0);
        return copy;
    }

    internal void WriteFeature(byte[] report)
    {
        ThrowIfGone();
        if (FailFeatures)
        {
            throw new IOException("The device did not accept the report.");
        }

        _writes.Enqueue([.. report]);
        _features[report[0]] = [.. report];
    }

    internal int ReadInput(byte[] buffer, TimeSpan timeout)
    {
        ThrowIfGone();
        if (!_inputs.TryTake(out byte[]? report, timeout))
        {
            ThrowIfGone();
            return 0;
        }

        int length = Math.Min(report.Length, buffer.Length);
        report.AsSpan(0, length).CopyTo(buffer);
        return length;
    }

    internal void ThrowIfGone()
    {
        if (!Present)
        {
            throw new HidDeviceLostException("The device was unplugged.");
        }
    }
}

internal sealed class FakeHidDeviceSource(params FakeHidDevice[] devices) : IHidDeviceSource
{
    private readonly List<FakeHidDevice> _devices = [.. devices];

    public volatile FakeHidConnection? LastConnection;

    public void Add(FakeHidDevice device)
    {
        lock (_devices)
        {
            _devices.Add(device);
        }
    }

    public IReadOnlyList<HidDeviceInfo> GetDevices()
    {
        lock (_devices)
        {
            return _devices.Where(d => d.Present).Select(d => d.Info).ToList();
        }
    }

    public byte[] GetReportDescriptor(HidDeviceInfo device)
    {
        FakeHidDevice fake = Find(device);
        if (fake.DenyAccess)
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        return fake.Descriptor;
    }

    public IHidConnection Open(HidDeviceInfo device)
    {
        FakeHidDevice fake = Find(device);
        if (fake.DenyAccess || fake.DenyOpen)
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        fake.CountOpen();
        var connection = new FakeHidConnection(fake);
        LastConnection = connection;
        return connection;
    }

    private FakeHidDevice Find(HidDeviceInfo device)
    {
        lock (_devices)
        {
            return _devices.FirstOrDefault(d => d.Present && d.Info.Path == device.Path)
                   ?? throw new HidDeviceLostException($"{device.Path} is gone.");
        }
    }
}

internal sealed class FakeHidConnection(FakeHidDevice device) : IHidConnection
{
    public volatile bool Disposed;

    public HidDeviceInfo Device => device.Info;

    public int MaxInputReportLength => device.MaxInputLength;

    public byte[] GetReportDescriptor() => device.Descriptor;

    public byte[] GetFeature(byte reportId, int length)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        return device.ReadFeature(reportId, length);
    }

    public void SetFeature(byte[] report)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        device.WriteFeature(report);
    }

    public int ReadInput(byte[] buffer, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        return device.ReadInput(buffer, timeout);
    }

    public string? GetIndexedString(int index) => index switch
    {
        1 => device.Info.Product,
        2 => device.Info.Serial,
        3 => device.Info.Manufacturer,
        4 => "PbAc",
        _ => null,
    };

    public void Dispose() => Disposed = true;
}
