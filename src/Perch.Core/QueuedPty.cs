using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Perch;

/// One ordered writer per PTY. A blocked shell never blocks the UI dispatcher.
/// All callers (keyboard, paste, and team delivery) use the same queue.
internal sealed class QueuedPty : IPty
{
    private readonly IPty _inner;
    private readonly object _gate = new();
    private readonly Queue<(byte[] Bytes, TaskCompletionSource? Done)> _queue = new();
    private bool _writing, _disposed;
    private long _queuedBytes;
    internal const int Capacity = 16 * 1024 * 1024;
    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    public event EventHandler<int>? Exited;
    public event Action<Exception>? WriteFailed;
    public int ProcessId => _inner.ProcessId;
    public long MaxOutstanding => _inner.MaxOutstanding;
    public IProcScope? Scope => _inner.Scope;

    public QueuedPty(IPty inner)
    {
        _inner = inner;
        inner.OutputReceived += (_, bytes) => OutputReceived?.Invoke(this, bytes);
        inner.Exited += (_, code) => Exited?.Invoke(this, code);
    }

    public void Write(ReadOnlySpan<byte> bytes) => Enqueue(bytes, null);
    public Task WriteAsync(byte[] bytes)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(bytes, done);
        return done.Task;
    }
    private void Enqueue(ReadOnlySpan<byte> bytes, TaskCompletionSource? done)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_queuedBytes + bytes.Length > Capacity)
                throw new InvalidOperationException("Terminal input is still draining. Wait for the shell before pasting more text.");
            _queue.Enqueue((bytes.ToArray(), done));
            _queuedBytes += bytes.Length;
            if (_writing) return;
            _writing = true;
            _ = Task.Run(Drain);
        }
    }

    private void Drain()
    {
        while (true)
        {
            byte[] bytes;
            TaskCompletionSource? done;
            lock (_gate)
            {
                if (_disposed || _queue.Count == 0) { _writing = false; return; }
                (bytes, done) = _queue.Dequeue();
            }
            try { _inner.Write(bytes); }
            catch (Exception ex)
            {
                done?.TrySetException(ex);
                lock (_gate)
                {
                    if (_disposed) return;
                    while (_queue.TryDequeue(out var item)) item.Done?.TrySetException(ex);
                    _queuedBytes = 0;
                    _writing = false;
                }
                WriteFailed?.Invoke(ex);
                return;
            }
            lock (_gate) _queuedBytes -= bytes.Length;
            done?.TrySetResult();
        }
    }

    public void Ack(long bytes) => _inner.Ack(bytes);
    public void Resize(int cols, int rows) => _inner.Resize(cols, rows);
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            while (_queue.TryDequeue(out var item)) item.Done?.TrySetCanceled();
        }
        // Closing the native PTY releases a blocked native write. Never wait
        // for the writer while holding its gate or the UI synchronization context.
        _inner.Dispose();
    }
}
