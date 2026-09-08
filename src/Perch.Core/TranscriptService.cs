using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Perch;

internal readonly record struct TranscriptKey(Guid PaneId, string? SessionId, string? Cwd, bool Codex = false, string? Path = null);

/// Per-conversation worker ownership. No file IO or parsing on the UI thread;
/// immutable results may be read synchronously by periodic team reconciliation.
internal sealed class TranscriptService
{
    private sealed class Entry
    {
        public readonly object Gate = new(), ReaderGate = new();
        public readonly TranscriptReader Claude = new();
        public readonly CodexTranscriptReader Codex = new();
        public Task<InspectorData?>? Reading;
        public volatile InspectorData? Snapshot;
        public long LastUse;
        public long KeepUntil;
    }
    private readonly ConcurrentDictionary<TranscriptKey, Entry> _entries = new();
    public event Action<TranscriptKey>? ModelChanged;
    private Entry Get(TranscriptKey key)
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        entry.LastUse = Environment.TickCount64;
        return entry;
    }

    public InspectorData? ReadCached(TranscriptKey key)
    {
        var entry = Get(key);
        // A room poll may still be consuming a sleeping bot's final reply.
        // Keep that actively requested snapshot through its next poll instead
        // of evicting every state push and perpetually returning a cold null.
        entry.KeepUntil = Environment.TickCount64 + 5000;
        var result = entry.Snapshot;
        _ = ReadAsync(key, entry);
        return result;
    }
    public Task<InspectorData?> ReadAsync(TranscriptKey key) => ReadAsync(key, Get(key));
    private Task<InspectorData?> ReadAsync(TranscriptKey key, Entry entry)
    {
        lock (entry.Gate)
        {
            if (entry.Reading is { IsCompleted: false }) return entry.Reading;
            return entry.Reading = Task.Run(() =>
            {
                lock (entry.ReaderGate)
                {
                    try
                    {
                        var previous = entry.Snapshot;
                        var next = entry.Snapshot = key.Codex
                            ? entry.Codex.Read(key.PaneId, key.SessionId, key.Path)
                            : entry.Claude.Read(key.PaneId, key.SessionId, key.Cwd);
                        if (previous?.Vitals?.Model != next?.Vitals?.Model) ModelChanged?.Invoke(key);
                        return next;
                    }
                    catch (Exception ex) { Log.Error("TranscriptService.Read", ex); return entry.Snapshot; }
                }
            });
        }
    }

    public async Task<ImageLocator?> LocateImageAsync(TranscriptKey key, string imageId)
    {
        var entry = Get(key);
        await ReadAsync(key, entry).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            lock (entry.ReaderGate) return entry.Claude.LocateImage(key.PaneId, imageId);
        }).ConfigureAwait(false);
    }

    public void Forget(Guid id)
    {
        foreach (var key in _entries.Keys.Where(k => k.PaneId == id)) _entries.TryRemove(key, out _);
    }

    public void Retain(ISet<Guid> awake)
    {
        foreach (var item in _entries.Where(e => !awake.Contains(e.Key.PaneId) && e.Value.KeepUntil < Environment.TickCount64))
            _entries.TryRemove(item.Key, out _);
        // Entries are reloadable from disk. Limit cold caches without evicting
        // in-flight reads and starting duplicate workers for the same key.
        foreach (var item in _entries.OrderByDescending(e => e.Value.LastUse).Skip(32))
            if (item.Value.Reading?.IsCompleted != false) _entries.TryRemove(item.Key, out _);
    }
    public void Clear() => _entries.Clear();
}
