using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perch;

/// One entry in a team room: something the owner posted, something a bot said
/// or did, a message between bots, a note a bot left for the owner, or a
/// lifecycle event ("Ada joined as Frontend dev").
///
/// Kinds:
///   user   — the owner's post. `To` names bot slugs, ["*"] for everyone, or
///            null when the post was unaddressed and the router decided.
///   beat   — what a bot SAID (an assistant text block from its transcript).
///   work   — what a bot DID (a tool call); Verb/Target/Note/Repeat mirror the
///            inspector's row so the page can fold runs the same way.
///   peer   — a bot-to-bot SendMessage as observed by the hook. `Ok` is the
///            delivery verdict; `Summary` is the sender's one-line preview.
///   note   — a bot's `perch team post` to the room; pings nobody.
///   system — lifecycle. `Event` says which: joined, left, asleep, woke,
///            waiting, permission, done, error, routed, delivered, undelivered.
internal sealed class RoomEntry
{
    public long Seq { get; set; }
    public long TsMs { get; set; }
    public string Kind { get; set; } = "";
    /// Bot slug, or "you" (the owner), or "perch" (the app).
    public string From { get; set; } = "";
    public List<string>? To { get; set; }
    public string Text { get; set; } = "";
    public string? Summary { get; set; }
    public bool? Ok { get; set; }
    public bool? Delivered { get; set; }
    /// Page-generated id echoed back so an optimistic row can be reconciled.
    public string? ClientId { get; set; }
    public string? Verb { get; set; }
    public string? Target { get; set; }
    public string? Note { get; set; }
    public int? Repeat { get; set; }
    public string? Event { get; set; }
    public string? PaneId { get; set; }
    /// An attached picture (absolute path) on a `note` or `user` row; the
    /// page fetches its bytes through `team.image`.
    public string? Image { get; set; }
    /// The board a task event is about.
    public string? TaskId { get; set; }
    /// The buttons an `ask` card offers.
    public List<string>? Choices { get; set; }
}

[JsonSerializable(typeof(RoomEntry))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class RoomJsonContext : JsonSerializerContext { }

/// The append-only log behind a team room: `room.jsonl`, one entry per line.
///
/// Append-only is the point. Every consumer — the page's incremental fetch,
/// the unread count, "what happened while I was away" — keys on a monotonic
/// `Seq`, and a file that is only ever appended to can hand out those numbers
/// without a database. A delivery that succeeds later is recorded as a NEW
/// `system` entry (`Event = "delivered"`) rather than rewriting the old row.
///
/// Size is capped: past ~2 MB the file is rewritten keeping the newest lines.
/// Older messages fall off the room's history, which is what a chat log does;
/// the bots' full transcripts are still on disk under ~/.claude.
///
/// Never fatal: a corrupt line is skipped on read, a failed write is logged.
/// Losing a room entry costs a line in the feed, not a bot.
internal sealed class RoomLedger
{
    private readonly string _path;
    private readonly object _gate = new();
    private long _lastSeq;

    /// Rotate when the file passes this many bytes …
    internal const long RotateAtBytes = 2L * 1024 * 1024;
    /// … keeping this many newest lines.
    internal const int KeepLines = 2000;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public RoomLedger(string path)
    {
        _path = path;
        _lastSeq = ScanLastSeq();
    }

    public string Path => _path;

    /// The newest sequence number on disk (0 for an empty room).
    public long LastSeq { get { lock (_gate) return _lastSeq; } }

    /// The number the next Append will assign. Appends run on one thread
    /// (the UI's), so a caller may bake it into what it writes before the
    /// row exists — the delivered post carries its own number.
    public long NextSeq { get { lock (_gate) return _lastSeq + 1; } }

    /// Append one entry, assigning its `Seq` and `TsMs` (when unset). Returns
    /// the entry for convenience so callers can push its seq to the page.
    public RoomEntry Append(RoomEntry entry)
    {
        lock (_gate)
        {
            entry.Seq = ++_lastSeq;
            if (entry.TsMs == 0) entry.TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var line = JsonSerializer.Serialize(entry, RoomJsonContext.Default.RoomEntry);
                File.AppendAllText(_path, line + "\n", Utf8NoBom);
                RotateIfNeeded();
            }
            catch (Exception ex) { Log.Error("RoomLedger.Append", ex); }
            return entry;
        }
    }

    /// Entries with Seq &gt; `sinceSeq`, oldest first, at most `max` of the NEWEST
    /// ones. `truncated` is true when older matching entries were left out —
    /// the page shows "older messages aren't shown" rather than a silent gap.
    public (List<RoomEntry> Entries, bool Truncated) ReadSince(long sinceSeq, int max = 500)
    {
        var all = ReadAll();
        var matching = all.FindAll(e => e.Seq > sinceSeq);
        if (matching.Count <= max) return (matching, false);
        return (matching.GetRange(matching.Count - max, max), true);
    }

    /// The newest `n` entries, oldest first.
    public List<RoomEntry> Tail(int n)
    {
        var all = ReadAll();
        if (all.Count <= n) return all;
        return all.GetRange(all.Count - n, n);
    }

    /// Every parseable entry, in file order. Corrupt lines are skipped: a
    /// half-written line from a crash must not take the whole room with it.
    public List<RoomEntry> ReadAll()
    {
        var list = new List<RoomEntry>();
        lock (_gate)
        {
            if (!File.Exists(_path)) return list;
            string[] lines;
            try { lines = File.ReadAllLines(_path, Utf8NoBom); }
            catch (Exception ex) { Log.Error("RoomLedger.Read", ex); return list; }
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                try
                {
                    var e = JsonSerializer.Deserialize(line, RoomJsonContext.Default.RoomEntry);
                    if (e != null && e.Seq > 0) list.Add(e);
                }
                catch (JsonException) { /* skip the corrupt line */ }
            }
        }
        list.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        return list;
    }

    private long ScanLastSeq()
    {
        var max = 0L;
        foreach (var e in ReadAll()) if (e.Seq > max) max = e.Seq;
        return max;
    }

    /// Keep the file bounded. Called under the gate, after an append.
    private void RotateIfNeeded()
    {
        long length;
        try { length = new FileInfo(_path).Length; } catch { return; }
        if (length < RotateAtBytes) return;

        string[] lines;
        try { lines = File.ReadAllLines(_path, Utf8NoBom); } catch { return; }
        if (lines.Length <= KeepLines) return;

        var keep = new string[KeepLines];
        Array.Copy(lines, lines.Length - KeepLines, keep, 0, KeepLines);
        try { AtomicFile.WriteAllText(_path, string.Join("\n", keep) + "\n"); }
        catch (Exception ex) { Log.Error("RoomLedger.Rotate", ex); }
    }
}
