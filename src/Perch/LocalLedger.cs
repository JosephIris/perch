using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perch;

/// Remembers which pane last owned each listening port, so a server that outlives
/// the pane that spawned it can still be named ("lingering — pane 'kanban' closed
/// 20m ago") instead of showing up as an anonymous orphan process.
///
/// Why a ledger and not a close-time hook: once a pane's shell exits, the OS does
/// NOT reparent its surviving children, so their recorded ParentProcessId points
/// at a dead (possibly reused) pid — the ancestry link is gone. We therefore
/// can't reconstruct ownership after the fact. Instead we observe it CONTINUOUSLY:
/// every scan, a server owned by a live pane refreshes its entry here; when a
/// later scan finds that same server (same pid) with no live owner, this is the
/// only record of who it belonged to.
///
/// Keyed by PORT, guarded by PID: ports get reused, so a remembered owner only
/// applies while the pid that holds the port is unchanged. A new process on an
/// old port is a new server, not the old pane's ghost.
///
/// Losing this file degrades gracefully — a lingering server just shows without
/// the "which pane" memory. So it's plain JSON in %LOCALAPPDATA%, written
/// atomically, never fatal on error.
internal sealed class LocalLedger
{
    internal sealed record Entry(
        [property: JsonPropertyName("port")]     int Port,
        [property: JsonPropertyName("pid")]      int Pid,
        [property: JsonPropertyName("paneName")] string? PaneName,
        [property: JsonPropertyName("paneId")]   string? PaneId,
        [property: JsonPropertyName("lastOwnedUnixMs")] long LastOwnedUnixMs);

    private readonly string _path;
    private readonly Dictionary<int, Entry> _byPort = new();
    private bool _dirty;

    /// Cap the file so a long-lived install can't grow it without bound. Ports
    /// are finite, but a machine that churns through thousands over months
    /// shouldn't accumulate them all. Evicted oldest-first.
    private const int MaxEntries = 400;

    public LocalLedger(string? pathOverride = null)
    {
        _path = pathOverride ?? DefaultPath();
        Load();
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Perch", "local-ledger.json");

    /// A live pane owns this port right now. Refresh in memory every scan; only
    /// mark the file dirty when the IDENTITY changes (pid/pane), not for the
    /// lastOwned bump — persisting a timestamp every few seconds would be pure
    /// disk churn, and losing the last few seconds of it only shifts a "closed
    /// Xm ago" label by one scan interval.
    public void Remember(int port, int pid, string? paneName, string? paneId, long nowMs)
    {
        if (_byPort.TryGetValue(port, out var prior)
            && prior.Pid == pid && prior.PaneName == paneName && prior.PaneId == paneId)
        {
            _byPort[port] = prior with { LastOwnedUnixMs = nowMs };
            return;
        }
        _byPort[port] = new Entry(port, pid, paneName, paneId, nowMs);
        _dirty = true;
    }

    /// The port is free, or a different process now holds it — drop the stale
    /// memory so an unrelated new server never inherits an old pane's name.
    public void Forget(int port)
    {
        if (_byPort.Remove(port)) _dirty = true;
    }

    public Entry? Get(int port) => _byPort.TryGetValue(port, out var e) ? e : null;

    /// Write only when something actually changed. Called once at the end of each
    /// scan by the controller.
    public void Flush()
    {
        if (!_dirty) return;
        Evict();
        Save();
        _dirty = false;
    }

    private void Evict()
    {
        if (_byPort.Count <= MaxEntries) return;
        foreach (var stale in _byPort.Values
                     .OrderBy(e => e.LastOwnedUnixMs)
                     .Take(_byPort.Count - MaxEntries)
                     .ToList())
            _byPort.Remove(stale.Port);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path));
            if (entries == null) return;
            foreach (var e in entries)
                if (e.Port > 0) _byPort[e.Port] = e;
        }
        catch (Exception ex)
        {
            // A corrupt ledger must never take the app down or block the panel:
            // an empty ledger just means "lingering servers without a name".
            Log.Error("LocalLedger.Load", ex);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(
                _byPort.Values.OrderBy(e => e.LastOwnedUnixMs).ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { Log.Error("LocalLedger.Save", ex); }
    }
}
