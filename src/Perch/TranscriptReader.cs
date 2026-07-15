using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Perch;

/// One row in the Inspector's stream. Four kinds, one ordered list:
///   "prompt"    — what YOU asked (a real user turn, slash-command noise stripped)
///   "beat"      — what the agent SAID (an assistant `text` block)
///   "work"      — what the agent DID (an assistant `tool_use` block)
///   "interrupt" — a turn YOU stopped (Esc / Ctrl-C); the rail paints it red
///   "skill"     — the agent invoked a Skill; its own kind, coloured violet
/// The page renders beats as the spine and work as dimmed connective tissue,
/// so one list drives both the narrative and the activity views.
///
/// Repeat > 1 means a RUN of consecutive identical calls was collapsed
/// ("Read perch.log ×6"). That collapse is not cosmetic — it's the only cheap
/// signal that an agent is thrashing, and it's what makes 300+ tool calls
/// skimmable at all.
internal sealed record InspectorEvent(
    string Kind,
    string Ts,
    string Text,
    string Verb,
    string Target,
    string Note,
    int Repeat);

/// What the pane is costing you. Nothing else in perch can answer this:
/// UsageService is account-wide rate limits (and 429s in practice), and
/// CloudLedger is GCP dollars. This comes from the transcript's per-message
/// `usage` blocks, which is also the only place the ACTUAL model id appears —
/// PaneNode.Model is just the alias the user picked, and it goes stale the
/// moment they type /model inside the TUI.
internal sealed record InspectorVitals(
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    double CostUsd,
    long ContextTokens,
    long ContextMax);

internal sealed record InspectorData(
    IReadOnlyList<InspectorEvent> Events,
    InspectorVitals? Vitals);

/// Reads Claude Code's JSONL transcript for a pane and projects it into the
/// Inspector's stream + vitals.
///
/// Tails by byte offset: the first read of a pane parses the whole file, every
/// later read parses only the bytes appended since. That matters because the
/// file is fat but the EXTRACT is tiny — the largest transcript on a real
/// machine (5.8 MB / 2,384 rows) yields ~300 prose beats and ~340 tool calls,
/// a few hundred KB. So we keep ALL history rather than windowing it; there is
/// no budget worth spending here.
///
/// Every failure path returns what we have so far (or null) — a transcript we
/// can't parse must never take a pane's Inspector down with it.
internal sealed class TranscriptReader
{
    /// Per-pane parse state. Offset is the byte position of the next unparsed
    /// line START, so a half-written line (the agent is mid-append) is simply
    /// left for the next read instead of being parsed as truncated JSON.
    private sealed class Tail
    {
        public string Path = "";
        public long Offset;
        public readonly List<InspectorEvent> Events = new();
        public string Model = "";
        public long Input, Output, CacheRead, CacheWrite;
        public long LastContext;
        public double Cost;
    }

    private readonly Dictionary<Guid, Tail> _tails = new();

    /// Drop a pane's parse state. Called when its Claude session id changes —
    /// a new session is a different transcript, and carrying the old events
    /// over would attribute the previous agent's work to this one.
    public void Forget(Guid paneId) => _tails.Remove(paneId);

    /// Project the pane's transcript. Returns null when the pane has no Claude
    /// session, no cwd, or no transcript on disk yet (a just-spawned agent) —
    /// the page reads null as "nothing to inspect" and shows its empty state.
    public InspectorData? Read(Guid paneId, string? sessionId, string? cwd)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(cwd)) return null;
        var path = ClaudeTranscripts.Locate(sessionId!, cwd!);
        if (path == null) return null;

        if (!_tails.TryGetValue(paneId, out var tail) || tail.Path != path)
        {
            tail = new Tail { Path = path };
            _tails[paneId] = tail;
        }

        try { Ingest(tail); }
        catch (Exception ex) { Log.Error("TranscriptReader.Ingest", ex); }

        return new InspectorData(Collapse(tail.Events), Vitals(tail));
    }

    /// Parse the bytes appended since the last read. Only whole lines are
    /// consumed; the offset advances past the last newline, so a partially
    /// flushed row is re-read (complete) next time rather than dropped.
    private static void Ingest(Tail tail)
    {
        using var fs = new FileStream(
            tail.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // Truncated or rotated (a /clear, or a fresh session reusing the id):
        // the offset is meaningless now — start over rather than parse garbage.
        if (fs.Length < tail.Offset)
        {
            tail.Offset = 0;
            tail.Events.Clear();
            tail.Input = tail.Output = tail.CacheRead = tail.CacheWrite = 0;
            tail.Cost = 0;
            tail.Model = "";
        }
        if (fs.Length == tail.Offset) return;

        fs.Seek(tail.Offset, SeekOrigin.Begin);
        var buf = new byte[fs.Length - tail.Offset];
        fs.ReadExactly(buf, 0, buf.Length);

        var lastNl = Array.LastIndexOf(buf, (byte)'\n');
        if (lastNl < 0) return;                       // no complete line yet
        tail.Offset += lastNl + 1;

        foreach (var range in SplitLines(buf.AsSpan(0, lastNl + 1)))
        {
            var line = Encoding.UTF8.GetString(buf, range.Start, range.Length);
            if (line.Length == 0) continue;
            try { Row(tail, line); }
            catch (JsonException) { /* one bad row must not kill the rest */ }
        }
    }

    private static List<(int Start, int Length)> SplitLines(ReadOnlySpan<byte> span)
    {
        var lines = new List<(int, int)>();
        var start = 0;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] != (byte)'\n') continue;
            var end = i;
            if (end > start && span[end - 1] == (byte)'\r') end--;   // tolerate CRLF
            lines.Add((start, end - start));
            start = i + 1;
        }
        return lines;
    }

    private static void Row(Tail tail, string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        // Subagent (Task) rows. They're the majority of a fan-out session and
        // they'd bury the main thread's narrative — the coordinator's own
        // `Task` tool_use already stands in for the whole subagent.
        if (root.TryGetProperty("isSidechain", out var side) &&
            side.ValueKind == JsonValueKind.True) return;

        var type = Str(root, "type");
        var ts = Str(root, "timestamp");
        if (!root.TryGetProperty("message", out var msg) ||
            msg.ValueKind != JsonValueKind.Object) return;

        if (type == "user")
        {
            // Only a genuinely TYPED turn becomes a prompt. Claude Code injects a
            // lot of rows as type "user" that the user never typed — image-paste
            // markers and skill/command bodies (isMeta), and task-completion
            // notifications / system turns (origin.kind != "human"). Rendered in
            // full they flood the journal (a single task-notification is ~8 KB of
            // XML), so they're dropped here on metadata rather than by guessing at
            // their prose. Absent metadata (older transcripts) reads as human, so
            // their prompts still show; UserPrompt's text filters stay as the
            // backstop for tool_results and slash-command scaffolding.
            if (IsInjected(root)) return;
            var prompt = UserPrompt(msg);
            if (prompt == null) return;
            // An interrupt (Esc / Ctrl-C) is recorded as a "[Request interrupted
            // …]" user turn — bare, or "…for tool use". It reads as an alarm, not
            // a prompt, so it gets its own kind and the rail paints it red.
            var kind = prompt.StartsWith("[Request interrupted", StringComparison.Ordinal)
                ? "interrupt" : "prompt";
            tail.Events.Add(new InspectorEvent(kind, ts, prompt, "", "", "", 1));
            return;
        }
        if (type != "assistant") return;

        if (Str(msg, "model") is { Length: > 0 } model) tail.Model = model;
        Usage(tail, msg);

        if (!msg.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array) return;

        foreach (var block in content.EnumerateArray())
        {
            switch (Str(block, "type"))
            {
                // Thinking blocks are deliberately dropped. They're the single
                // largest slice of a transcript and they are NOT what the user
                // asked for — the whole point of the journal is the ~25 prose
                // beats, not the 135 thinking blocks they're buried in.
                case "text":
                    var text = Str(block, "text").Trim();
                    if (text.Length > 0)
                        tail.Events.Add(new InspectorEvent("beat", ts, text, "", "", "", 1));
                    break;

                case "tool_use":
                    var verb = Str(block, "name");
                    if (verb.Length == 0) break;
                    block.TryGetProperty("input", out var input);
                    var (target, note) = ToolTarget(verb, input);
                    // A skill invocation is its own kind — the agent reaching for
                    // a packaged capability, worth seeing (and filtering) apart
                    // from ordinary tool calls, and coloured for it in the rail.
                    tail.Events.Add(new InspectorEvent(
                        verb == "Skill" ? "skill" : "work", ts, "", verb, target, note, 1));
                    break;
            }
        }
    }

    /// A `type:"user"` row the user did NOT type: an isMeta row (an image-paste
    /// marker, or a skill/command body injected under a tool use) or a turn whose
    /// origin isn't a human (task-completion notifications, system turns). Both
    /// signals are absent on a genuine typed prompt AND on older transcripts, so
    /// a missing signal reads as "human" and the prompt still renders.
    private static bool IsInjected(JsonElement root)
    {
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
            return true;
        if (root.TryGetProperty("promptSource", out var ps) &&
            ps.ValueKind == JsonValueKind.String &&
            string.Equals(ps.GetString(), "system", StringComparison.Ordinal))
            return true;
        if (root.TryGetProperty("origin", out var origin) &&
            origin.ValueKind == JsonValueKind.Object &&
            origin.TryGetProperty("kind", out var kind) &&
            kind.ValueKind == JsonValueKind.String)
            return !string.Equals(kind.GetString(), "human", StringComparison.Ordinal);
        return false;
    }

    /// A real user turn, or null. Two things masquerade as one:
    ///   • tool_result rows — the API models them as `user` messages
    ///   • slash-command scaffolding Claude Code injects (<command-name>,
    ///     <local-command-stdout>, <local-command-caveat>, …)
    /// Neither is something the user typed, and both would drown the journal's
    /// prompt headers. In a real 633-row transcript this filter takes 12
    /// candidate "user" rows down to the 1 prompt actually typed.
    private static string? UserPrompt(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var content)) return null;

        string raw;
        if (content.ValueKind == JsonValueKind.String)
        {
            raw = content.GetString() ?? "";
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                var kind = Str(block, "type");
                if (kind == "tool_result") return null;      // not a user turn at all
                if (kind == "text") parts.Add(Str(block, "text"));
            }
            raw = string.Join(" ", parts);
        }
        else return null;

        raw = raw.Trim();
        if (raw.Length == 0) return null;
        if (raw.StartsWith("<command-", StringComparison.Ordinal) ||
            raw.StartsWith("<local-command-", StringComparison.Ordinal) ||
            raw.StartsWith("<user-memory-", StringComparison.Ordinal)) return null;
        return raw;
    }

    /// Human verb + target for one tool call: "Edit GitProc.cs", "Bash dotnet
    /// test". The target is the FILENAME, not the path — at the rail's width a
    /// full path ellipsizes into uselessness, and the filename is what you
    /// actually scan for.
    private static (string Target, string Note) ToolTarget(string verb, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return ("", "");
        return verb switch
        {
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" =>
                (Leaf(Str(input, "file_path") is { Length: > 0 } p ? p : Str(input, "notebook_path")), ""),
            "Read"      => (Leaf(Str(input, "file_path")), ""),
            "Bash"      => (Clip(Str(input, "command"), 60), ""),
            "Grep"      => (Clip(Str(input, "pattern"), 40), ""),
            "Glob"      => (Clip(Str(input, "pattern"), 40), ""),
            "Task"      => (Clip(Str(input, "description"), 40), ""),
            "WebFetch"  => (Clip(Str(input, "url"), 50), ""),
            "WebSearch" => (Clip(Str(input, "query"), 40), ""),
            "Skill"     => (Str(input, "skill"), ""),
            "TodoWrite" => ("todos", ""),
            _           => ("", ""),
        };
    }

    private static void Usage(Tail tail, JsonElement msg)
    {
        if (!msg.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;

        var input = Num(u, "input_tokens");
        var output = Num(u, "output_tokens");
        var read = Num(u, "cache_read_input_tokens");
        var write = Num(u, "cache_creation_input_tokens");

        // Cache writes bill at 1.25x input for the 5-minute TTL and 2x for the
        // 1-hour one. The transcript reports the split, so we price it exactly
        // rather than assuming a TTL.
        long w5 = 0, w1h = 0;
        if (u.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
        {
            w5 = Num(cc, "ephemeral_5m_input_tokens");
            w1h = Num(cc, "ephemeral_1h_input_tokens");
        }
        if (w5 + w1h == 0) w5 = write;                 // older rows: assume the 5m default

        tail.Input += input;
        tail.Output += output;
        tail.CacheRead += read;
        tail.CacheWrite += write;

        // Context = everything the model saw on this turn. The LAST turn's
        // figure is the live one — earlier turns are smaller by definition.
        tail.LastContext = input + read + write;

        var (inRate, outRate) = Rates(Str(msg, "model"));
        tail.Cost += (input * inRate
                    + w5 * inRate * 1.25
                    + w1h * inRate * 2.0
                    + read * inRate * 0.1
                    + output * outRate) / 1_000_000.0;
    }

    /// USD per 1M tokens. Verified against Anthropic's published pricing.
    /// Prefix-matched because transcripts sometimes carry a dated snapshot id
    /// (claude-haiku-4-5-20251001). An unknown model prices at zero rather than
    /// guessing — showing a made-up dollar figure is worse than showing none.
    private static (double In, double Out) Rates(string model) => model switch
    {
        _ when model.StartsWith("claude-fable-5", StringComparison.OrdinalIgnoreCase)   => (10.0, 50.0),
        _ when model.StartsWith("claude-mythos-5", StringComparison.OrdinalIgnoreCase)  => (10.0, 50.0),
        _ when model.StartsWith("claude-opus-4-", StringComparison.OrdinalIgnoreCase)   => (5.0, 25.0),
        _ when model.StartsWith("claude-sonnet-", StringComparison.OrdinalIgnoreCase)   => (3.0, 15.0),
        _ when model.StartsWith("claude-haiku-", StringComparison.OrdinalIgnoreCase)    => (1.0, 5.0),
        _ => (0.0, 0.0),
    };

    private static long ContextMax(string model) =>
        model.StartsWith("claude-haiku-", StringComparison.OrdinalIgnoreCase) ? 200_000 : 1_000_000;

    private static InspectorVitals? Vitals(Tail tail)
    {
        if (tail.Model.Length == 0) return null;
        return new InspectorVitals(
            Pretty(tail.Model), tail.Input, tail.Output, tail.CacheRead, tail.CacheWrite,
            Math.Round(tail.Cost, 4), tail.LastContext, ContextMax(tail.Model));
    }

    /// "claude-fable-5" → "fable-5". The rail is 336px; the vendor prefix is
    /// noise there, and every model in the strip is a Claude one anyway.
    private static string Pretty(string model) =>
        model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ? model.Substring(7) : model;

    /// Fold each RUN of identical consecutive tool calls into one row carrying
    /// a count. Runs only — a Read of the same file before and after an Edit is
    /// two separate looks at a changed file, not thrash, and merging them would
    /// invent a repeat that never happened.
    private static IReadOnlyList<InspectorEvent> Collapse(List<InspectorEvent> events)
    {
        var outp = new List<InspectorEvent>(events.Count);
        foreach (var e in events)
        {
            var prev = outp.Count > 0 ? outp[^1] : null;
            if (e.Kind == "work" && prev is { Kind: "work" } &&
                prev.Verb == e.Verb && prev.Target == e.Target && e.Target.Length > 0)
            {
                outp[^1] = prev with { Repeat = prev.Repeat + 1 };
                continue;
            }
            outp.Add(e);
        }
        return outp;
    }

    // ---- JSON helpers -------------------------------------------------------

    private static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static long Num(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt64(out var n) ? n : 0;

    private static string Leaf(string path)
    {
        if (path.Length == 0) return "";
        var i = path.LastIndexOfAny(new[] { '/', '\\' });
        return i >= 0 && i < path.Length - 1 ? path.Substring(i + 1) : path;
    }

    private static string Clip(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
