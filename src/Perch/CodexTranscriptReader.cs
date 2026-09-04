using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Perch;

/// Reads codex's rollout file for a pane and projects it into the SAME
/// Inspector stream + vitals that <see cref="TranscriptReader"/> produces for
/// Claude Code. Different file, different vocabulary, identical output — so the
/// rail, its filters, its search and its cost strip work for a codex pane with
/// no page-side special case.
///
/// The shapes below were read off real rollout files (codex-cli 0.153.2), not
/// from documentation. What matters:
///
///   • Every line is `{timestamp, type, payload}`. The one worth reading is
///     `type:"event_msg"` with `payload.type:"item_completed"`, whose `item` is
///     codex's OWN normalised view of what happened — UserMessage,
///     AgentMessage, CommandExecution, FileChange, Extension, Reasoning. That
///     is deliberately the same altitude as the journal, so we take codex's
///     normalisation rather than re-deriving it from raw function calls.
///   • `type:"token_usage_record"` carries CUMULATIVE thread totals, so those
///     are assigned, never accumulated — adding them would multiply the count
///     by the number of turns.
///   • Reasoning items are dropped for the same reason Claude's thinking blocks
///     are: they are most of the file and none of the story.
///
/// Tails by byte offset like its Claude sibling: the first read parses the
/// whole file, later reads only the bytes appended since.
internal sealed class CodexTranscriptReader
{
    private sealed class Tail
    {
        public string Path = "";
        public long Offset;
        public readonly List<InspectorEvent> Events = new();
        public string Model = "";
        public long Input, Output, CacheRead, CacheWrite;
        public long LastContext, ContextMax;
    }

    private readonly Dictionary<Guid, Tail> _tails = new();

    /// Drop a pane's parse state — a new codex thread is a different rollout
    /// file, and carrying the old events over would attribute the previous
    /// conversation's work (and tokens) to this one.
    public void Forget(Guid paneId) => _tails.Remove(paneId);

    /// Project the pane's codex journal. `path` is what codex's SessionStart
    /// hook told us (`transcript_path`); when that's absent we fall back to
    /// finding it by session id. Null when the pane has no codex thread or
    /// codex hasn't written the file yet.
    public InspectorData? Read(Guid paneId, string? sessionId, string? path = null)
    {
        var file = !string.IsNullOrEmpty(path) && File.Exists(path)
            ? path
            : CodexTranscripts.Locate(sessionId);
        if (file == null) return null;

        if (!_tails.TryGetValue(paneId, out var tail) || tail.Path != file)
        {
            tail = new Tail { Path = file };
            _tails[paneId] = tail;
        }

        try { Ingest(tail); }
        catch (Exception ex) { Log.Error("CodexTranscriptReader.Ingest", ex); }

        return new InspectorData(Collapse(tail.Events), Vitals(tail));
    }

    private static void Ingest(Tail tail)
    {
        using var fs = new FileStream(tail.Path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        // Truncated or rotated: the offset means nothing now.
        if (fs.Length < tail.Offset)
        {
            tail.Offset = 0;
            tail.Events.Clear();
            tail.Input = tail.Output = tail.CacheRead = tail.CacheWrite = 0;
            tail.LastContext = 0;
            tail.Model = "";
        }
        if (fs.Length == tail.Offset) return;

        fs.Seek(tail.Offset, SeekOrigin.Begin);
        var buf = new byte[fs.Length - tail.Offset];
        fs.ReadExactly(buf, 0, buf.Length);

        var lastNl = Array.LastIndexOf(buf, (byte)'\n');
        if (lastNl < 0) return;                     // no complete line yet
        tail.Offset += lastNl + 1;

        var text = Encoding.UTF8.GetString(buf, 0, lastNl + 1);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) continue;
            try { Row(tail, trimmed); }
            catch (JsonException) { /* one bad row must not kill the rest */ }
        }
    }

    private static void Row(Tail tail, string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        var ts = Str(root, "timestamp");
        var type = Str(root, "type");
        if (!root.TryGetProperty("payload", out var pay) || pay.ValueKind != JsonValueKind.Object) return;

        switch (type)
        {
            // Cumulative thread totals — assigned, not added.
            case "token_usage_record":
                if (pay.TryGetProperty("thread_token_usage", out var tu) && tu.ValueKind == JsonValueKind.Object)
                {
                    tail.Input = Num(tu, "input_tokens");
                    tail.Output = Num(tu, "output_tokens");
                    tail.CacheRead = Num(tu, "cached_input_tokens");
                    tail.CacheWrite = Num(tu, "cache_write_input_tokens");
                }
                return;

            // The model actually in use this thread.
            case "world_state":
                if (pay.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object
                    && st.TryGetProperty("collaboration_mode", out var cm) && cm.ValueKind == JsonValueKind.Object
                    && Str(cm, "model") is { Length: > 0 } m)
                    tail.Model = m;
                return;

            case "event_msg":
                break;                              // handled below

            default:
                return;
        }

        switch (Str(pay, "type"))
        {
            // The context window this thread runs in — the denominator of the
            // rail's "context used" bar.
            case "task_started":
                if (Num(pay, "model_context_window") is > 0 and var w) tail.ContextMax = w;
                return;

            // Live context use: what the LAST turn actually sent.
            case "token_count":
                if (pay.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
                {
                    if (Num(info, "model_context_window") is > 0 and var mw) tail.ContextMax = mw;
                    if (info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
                        tail.LastContext = Num(last, "input_tokens")
                                         + Num(last, "cached_input_tokens")
                                         + Num(last, "cache_write_input_tokens");
                }
                return;

            // You hit Esc. Painted as an alarm, same as Claude's "[Request
            // interrupted]" turn — it's the one thing you must not scroll past.
            case "turn_aborted":
                tail.Events.Add(new InspectorEvent(
                    "interrupt", ts, "Interrupted — " + (Str(pay, "reason") is { Length: > 0 } r ? r : "stopped"),
                    "", "", "", 1));
                return;

            case "item_completed":
                if (pay.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                    Item(tail, ts, item);
                return;
        }
    }

    private static void Item(Tail tail, string ts, JsonElement item)
    {
        switch (Str(item, "type"))
        {
            case "UserMessage":
            {
                var text = ItemText(item).Trim();
                if (text.Length > 0)
                    tail.Events.Add(new InspectorEvent("prompt", ts, text, "", "", "", 1));
                return;
            }

            case "AgentMessage":
            {
                var text = ItemText(item).Trim();
                if (text.Length > 0)
                    tail.Events.Add(new InspectorEvent("beat", ts, text, "", "", "", 1));
                return;
            }

            // A shell command. `command` is the full argv — on Windows that is
            // ["powershell.exe", "-Command", "<the actual thing>"], so the last
            // element is what a person would call "the command". codex also
            // pre-parses it into `parsed_cmd[].cmd`, which is better still when
            // it's there.
            case "CommandExecution":
            {
                var cmd = ParsedCommand(item) ?? LastArrayString(item, "command") ?? "";
                var failed = Str(item, "status") is "failed" or "error";
                tail.Events.Add(new InspectorEvent(
                    "work", ts, "", "Run", Clip(OneLine(cmd), 60), failed ? "failed" : "", 1));
                return;
            }

            // One row per file, so the rail reads like Claude's Edit/Write
            // stream rather than one opaque "patch applied".
            case "FileChange":
            {
                if (!item.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Object)
                    return;
                foreach (var change in changes.EnumerateObject())
                {
                    var verb = Str(change.Value, "type") switch
                    {
                        "add"    => "Write",
                        "delete" => "Delete",
                        _        => "Edit",
                    };
                    tail.Events.Add(new InspectorEvent("work", ts, "", verb, Leaf(change.Name), "", 1));
                }
                return;
            }

            // Codex's built-ins arrive under one wrapper with a `kind`.
            case "Extension":
            {
                var kind = Str(item, "kind");
                var (verb, target) = kind switch
                {
                    "web.search" => ("Search", Clip(Str(item, "query"), 40)),
                    _            => (PrettyKind(kind), ""),
                };
                tail.Events.Add(new InspectorEvent("work", ts, "", verb, target, "", 1));
                return;
            }

            // Reasoning: dropped on purpose (see the class note).
        }
    }

    /// "web.search" → "Web search"; an unknown kind still reads as words rather
    /// than a dotted identifier.
    private static string PrettyKind(string kind)
    {
        if (kind.Length == 0) return "Tool";
        var words = kind.Replace('.', ' ').Replace('_', ' ');
        return char.ToUpperInvariant(words[0]) + words.Substring(1);
    }

    /// The text of a UserMessage / AgentMessage item. Codex nests it as
    /// `content: [{type, text}]` and capitalises the block type differently
    /// between the two ("text" vs "Text"), so the type isn't matched at all —
    /// every block's `text` is taken in order.
    private static string ItemText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return "";
        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
            if (Str(block, "text") is { Length: > 0 } t) parts.Add(t);
        return string.Join("\n", parts);
    }

    private static string? ParsedCommand(JsonElement item)
    {
        if (!item.TryGetProperty("parsed_cmd", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var e in arr.EnumerateArray())
            if (Str(e, "cmd") is { Length: > 0 } c) return c;
        return null;
    }

    private static string? LastArrayString(JsonElement item, string field)
    {
        if (!item.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        string? last = null;
        foreach (var e in arr.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String) last = e.GetString();
        return last;
    }

    private static InspectorVitals? Vitals(Tail tail)
    {
        if (tail.Model.Length == 0 && tail.Input == 0 && tail.Output == 0) return null;
        return new InspectorVitals(
            tail.Model.Length > 0 ? tail.Model : "codex",
            tail.Input, tail.Output, tail.CacheRead, tail.CacheWrite,
            // No cost: we don't have per-token prices for these models, and a
            // made-up dollar figure is worse than none (the Claude reader takes
            // the same line for a model it doesn't know).
            0.0,
            tail.LastContext,
            tail.ContextMax > 0 ? tail.ContextMax : 0);
    }

    /// Fold each RUN of identical consecutive rows into one carrying a count —
    /// same rule, and same reason, as the Claude reader's.
    private static IReadOnlyList<InspectorEvent> Collapse(List<InspectorEvent> events)
    {
        var outp = new List<InspectorEvent>(events.Count);
        foreach (var e in events)
        {
            var prev = outp.Count > 0 ? outp[^1] : null;
            if (prev != null && prev.Kind == "work" && e.Kind == "work"
                && prev.Verb == e.Verb && prev.Target == e.Target && prev.Note == e.Note)
                outp[^1] = prev with { Repeat = prev.Repeat + 1 };
            else
                outp.Add(e);
        }
        return outp;
    }

    // ---- small helpers -----------------------------------------------------

    private static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static long Num(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string Clip(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 1)) + "…";

    /// The filename, not the path — at the rail's width a full path ellipsizes
    /// into uselessness. Codex reports absolute paths, and some of them as
    /// file:// URLs.
    private static string Leaf(string p)
    {
        if (p.Length == 0) return "";
        var s = p.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) ? p.Substring(8) : p;
        var i = s.LastIndexOfAny(new[] { '/', '\\' });
        return i >= 0 ? s.Substring(i + 1) : s;
    }
}
