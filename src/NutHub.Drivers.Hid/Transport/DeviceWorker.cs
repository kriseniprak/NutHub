using System.Collections.Concurrent;

namespace NutHub.Drivers.Hid.Transport;

/// <summary>
/// A dedicated thread that performs every call to one device. HID calls block (Windows HidD_*, hidraw ioctls),
/// so they must not run on the thread pool; running them on a single thread also serialises polls, commands and
/// variable writes without locks. When no work is queued the thread runs <see cref="Idle"/>, which the driver
/// uses to wait briefly for input reports; queued work therefore waits at most one idle round.
/// </summary>
internal sealed class DeviceWorker : IDisposable
{
    private readonly ConcurrentQueue<(Action Run, Action Abandon)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Exception> _idleFault =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopping;
    private volatile Action? _idle;

    public DeviceWorker(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>
    /// Work to do when the queue is empty; it should return within a few hundred milliseconds. An exception
    /// stops the idle work and completes <see cref="IdleFault"/>.
    /// </summary>
    public Action? Idle
    {
        get => _idle;
        set
        {
            _idle = value;
            _signal.Release();
        }
    }

    /// <summary>Completes with the exception that stopped <see cref="Idle"/> (e.g. the device was unplugged).</summary>
    public Task<Exception> IdleFault => _idleFault.Task;

    /// <summary>Queues work for the device thread. Cancelling abandons the wait, not a call already running.</summary>
    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_stopping, this);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue((() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, () => tcs.TrySetException(new ObjectDisposedException(nameof(DeviceWorker), "The device connection is closed."))));
        _signal.Release();
        return tcs.Task.WaitAsync(cancellationToken);
    }

    public Task InvokeAsync(Action work, CancellationToken cancellationToken = default) =>
        InvokeAsync(() =>
        {
            work();
            return true;
        }, cancellationToken);

    /// <summary>
    /// Lets <see cref="Idle"/> pause without delaying queued work: returns as soon as work is queued (true) or after
    /// <paramref name="timeout"/> (false). Used when the device has nothing to read and would otherwise be polled in
    /// a tight loop.
    /// </summary>
    public bool WaitForWork(TimeSpan timeout) => !_queue.IsEmpty || _signal.Wait(timeout);

    /// <summary>Whether the calling code runs on the device thread.</summary>
    public bool IsCurrentThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>
    /// Stops the thread; queued work fails with <see cref="ObjectDisposedException"/>. A call stuck in the device
    /// is abandoned (the thread is a background thread), which is why a new worker is used for every connection.
    /// </summary>
    public void Dispose() => Stop(TimeSpan.FromSeconds(1));

    /// <summary>
    /// Stops the thread, waiting at most <paramref name="join"/> for the call in progress; pass zero for a thread
    /// known to be stuck in the device, which would only delay the reconnection.
    /// </summary>
    public void Stop(TimeSpan join)
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _idle = null;
        _signal.Release();
        if (!IsCurrentThread && join > TimeSpan.Zero)
        {
            _thread.Join(join);
        }

        // Work still queued (the thread stopped, or is stuck in the device) would otherwise never complete.
        while (_queue.TryDequeue(out var orphan))
        {
            orphan.Abandon();
        }
    }

    private void Run()
    {
        while (!_stopping)
        {
            if (_queue.TryDequeue(out var work))
            {
                work.Run();
                continue;
            }

            Action? idle = _idle;
            if (idle is null)
            {
                _signal.Wait(TimeSpan.FromMilliseconds(500));
                continue;
            }

            try
            {
                idle();
            }
            catch (Exception ex)
            {
                _idle = null;
                _idleFault.TrySetResult(ex);
            }
        }

        while (_queue.TryDequeue(out var remaining))
        {
            remaining.Abandon();
        }
    }
}
