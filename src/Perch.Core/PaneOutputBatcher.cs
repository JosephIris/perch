using System;
using System.Collections.Generic;

namespace Perch;

/// Coalesce bursts before entering the page bridge. PTY acknowledgements still
/// account for original bytes. One bounded slice per pane gives other UI work
/// a turn, even when many producers are active.
internal sealed class PaneOutputBatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly record struct Item(ReadOnlyMemory<byte> Bytes, Action? Complete = null);
    private readonly Dictionary<Guid, Queue<Item>> _pending = new();
    private readonly IUiThread _ui;
    private readonly Action<Guid, ReadOnlyMemory<byte>> _send;
    private bool _scheduled, _disposed;
    internal const int SliceBytes = 64 * 1024;
    public PaneOutputBatcher(IUiThread ui, Action<Guid, ReadOnlyMemory<byte>> send)
        => (_ui, _send) = (ui, send);

    public void Add(Guid id, ReadOnlyMemory<byte> bytes) => Enqueue(id, new Item(bytes));
    public void Complete(Guid id, Action complete) => Enqueue(id, new Item(default, complete));
    private void Enqueue(Guid id, Item item)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_pending.TryGetValue(id, out var queue)) _pending[id] = queue = new();
            queue.Enqueue(item);
            if (_scheduled) return;
            _scheduled = true;
        }
        _ui.Post(Drain);
    }

    private void Drain()
    {
        var batch = new List<(Guid, byte[], Action?)>();
        lock (_gate)
        {
            if (_disposed) return;
            // Cap aggregate work per dispatcher callback, not just per pane.
            int budget = 256 * 1024;
            foreach (var id in new List<Guid>(_pending.Keys))
            {
                var queue = _pending[id];
                var parts = new List<ReadOnlyMemory<byte>>();
                Action? complete = null;
                int size = 0;
                while (queue.Count > 0 && size < SliceBytes)
                {
                    var part = queue.Dequeue();
                    if (part.Complete != null) { complete = part.Complete; break; }
                    parts.Add(part.Bytes);
                    size += part.Bytes.Length;
                }
                var bytes = new byte[size];
                int offset = 0;
                foreach (var part in parts) { part.Span.CopyTo(bytes.AsSpan(offset)); offset += part.Length; }
                batch.Add((id, bytes, complete));
                // Rotate a busy pane to the back for fairness.
                _pending.Remove(id);
                if (queue.Count > 0) _pending.Add(id, queue);
                budget -= size;
                if (budget <= 0) break;
            }
        }
        foreach (var (id, bytes, complete) in batch)
        {
            if (bytes.Length > 0) _send(id, bytes);
            complete?.Invoke();
        }
        lock (_gate)
        {
            _scheduled = _pending.Count > 0 && !_disposed;
            if (_scheduled) _ui.Post(Drain);
        }
    }

    public void Dispose() { lock (_gate) { _disposed = true; _pending.Clear(); } }
}
