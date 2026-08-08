using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perch;

/// Remembers WHAT a cloud resource was for, keyed by the agent session that made
/// it — the half of the story a GCP label physically cannot hold.
///
/// The division of labour:
///   - the LABEL, on the resource itself, carries the join keys (agent-owner,
///     agent-session, agent-pane). It survives reboots, app reinstalls, and the
///     pane being closed, and it's what lets the poller filter server-side.
///   - the LEDGER, here, carries the human-readable half (the pane's name, the
///     prompt that caused the machine). GCP label values are capped at 63 chars
///     of [a-z0-9_-] — a sentence with spaces and quotes simply cannot go there.
///
/// Losing this file degrades gracefully: you still see the machine, its cost and
/// its session, just not the sentence explaining it. So it's plain JSON, written
/// atomically, and never fatal on error.
///
/// Deliberately in %LOCALAPPDATA%, never the repo: it contains prompt text and a
/// GCP-adjacent identity, neither of which belongs in a commit.
internal sealed class CloudLedger
{
    /// One entry per agent session that ever created a billable resource.
    internal sealed record Entry(
        [property: JsonPropertyName("session")]   string Session,
        [property: JsonPropertyName("agentName")] string? AgentName,
        [property: JsonPropertyName("task")]      string? Task,
        [property: JsonPropertyName("cwd")]       string? Cwd,
        [property: JsonPropertyName("paneId")]    string? PaneId,
        [property: JsonPropertyName("firstSeenUnixMs")] long FirstSeenUnixMs,
        [property: JsonPropertyName("lastSeenUnixMs")]  long LastSeenUnixMs);

    private readonly string _path;
    private readonly Dictionary<string, Entry> _bySession = new(StringComparer.OrdinalIgnoreCase);

    /// Cap the file so a long-lived install can't grow it without bound. Entries
    /// are evicted oldest-first; an evicted entry only costs us the description
    /// of a very old machine, which is the least valuable thing here.
    private const int MaxEntries = 500;

    public CloudLedger(string? pathOverride = null)
    {
        _path = pathOverride ?? DefaultPath();
        Load();
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Perch", "cloud-ledger.json");

    /// Record (or refresh) what this session is doing. Called when the hook tells
    /// us it just stamped a `gcloud create`, at which point the pane is still
    /// alive and we can still read its name and current task off PaneNode.
    public void Remember(string session, string? agentName, string? task, string? cwd, string? paneId)
    {
        if (string.IsNullOrWhiteSpace(session)) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Keep the FIRST task we saw for a session, not the latest: the prompt
        // that was live when the machine got created is the one that explains it.
        // A later prompt in the same session ("now summarize the results") would
        // be actively misleading next to a 2-day-old cluster.
        if (_bySession.TryGetValue(session, out var prior))
        {
            _bySession[session] = prior with
            {
                AgentName = agentName ?? prior.AgentName,
                Task = prior.Task ?? task,
                Cwd = cwd ?? prior.Cwd,
                PaneId = paneId ?? prior.PaneId,
                LastSeenUnixMs = now,
            };
        }
        else
        {
            _bySession[session] = new Entry(session, agentName, task, cwd, paneId, now, now);
        }

        Evict();
        Save();
    }

    public Entry? Get(string? session)
        => session != null && _bySession.TryGetValue(session, out var e) ? e : null;

    private void Evict()
    {
        if (_bySession.Count <= MaxEntries) return;
        foreach (var stale in _bySession.Values
                     .OrderBy(e => e.LastSeenUnixMs)
                     .Take(_bySession.Count - MaxEntries)
                     .ToList())
            _bySession.Remove(stale.Session);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var entries = JsonSerializer.Deserialize<List<Entry>>(json);
            if (entries == null) return;
            foreach (var e in entries)
                if (!string.IsNullOrWhiteSpace(e.Session)) _bySession[e.Session] = e;
        }
        catch (Exception ex)
        {
            // A corrupt ledger must not take the app down, and must not block the
            // panel: an empty ledger just means "machines with no description".
            Log.Error("CloudLedger.Load", ex);
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(
                _bySession.Values.OrderBy(e => e.FirstSeenUnixMs).ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            // Write-then-replace, so a crash mid-write can't leave a truncated
            // file that fails to parse on next launch. See AtomicFile.
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception ex) { Log.Error("CloudLedger.Save", ex); }
    }
}
