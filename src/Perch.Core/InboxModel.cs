using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Perch;

/// The pure half of the inbox: parsing what the Gmail export script writes,
/// and merging the shared state file. No IO, so all of it is unit-tested.
///
/// The export (product-tools-prod scripts/claude-inbox/Code.gs) writes one
/// Drive folder per Gmail thread, named `yyyy-MM-dd Subject [threadId]`,
/// holding `thread.md` and the attachments. Perch owns nothing in those
/// folders; its only file is `perch-state.json` at the inbox root.
internal static class InboxModel
{
    public const string StateFileName = "perch-state.json";
    public const string ThreadFileName = "thread.md";

    /// The states a thread can be in. "new" is the default for a thread the
    /// state file has never heard of, so it is never written for its own sake.
    public static readonly string[] States = { "new", "read", "pending", "done" };

    private static readonly Regex FolderRx = new(@"\[([^\[\]]+)\]\s*$");

    /// The Gmail thread id from a folder name, or null for a folder the
    /// export didn't make.
    public static string? ThreadIdFromFolder(string name)
    {
        var m = FolderRx.Match(name ?? "");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    // ---- thread.md -------------------------------------------------------

    internal sealed record Message(string From, string To, string Cc, DateTimeOffset? Date, string Subject, string Body, IReadOnlyList<string> Attachments);
    internal sealed record Thread(string Subject, IReadOnlyList<Message> Messages);

    private static readonly Regex MsgHeaderRx = new(@"^## Message \d+\s*$");

    /// Parse the export's markdown. Lenient by design: a body can contain
    /// anything, including its own `---` lines, so a message starts only at a
    /// `---` immediately followed by `## Message N`.
    public static Thread ParseThread(string md)
    {
        var lines = (md ?? "").Replace("\r\n", "\n").Split('\n');
        var subject = lines.Length > 0 && lines[0].StartsWith("# ") ? lines[0][2..].Trim() : "";
        var starts = new List<int>();
        for (int i = 0; i + 1 < lines.Length; i++)
            if (lines[i].Trim() == "---" && MsgHeaderRx.IsMatch(lines[i + 1])) starts.Add(i);

        var messages = new List<Message>();
        for (int k = 0; k < starts.Count; k++)
        {
            var from = starts[k] + 2;
            var to = k + 1 < starts.Count ? starts[k + 1] : lines.Length;
            messages.Add(ParseMessage(lines[from..to]));
        }
        if (subject.Length == 0 && messages.Count > 0) subject = messages[0].Subject;
        return new Thread(subject, messages);
    }

    private static Message ParseMessage(string[] lines)
    {
        string fromH = "", toH = "", cc = "", subj = "";
        DateTimeOffset? date = null;
        int i = 0;
        for (; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Length == 0) { i++; break; }   // blank line ends the headers
            if (l.StartsWith("From: ")) fromH = l[6..];
            else if (l.StartsWith("To: ")) toH = l[4..];
            else if (l.StartsWith("Cc: ")) cc = l[4..];
            else if (l.StartsWith("Date: ") && DateTimeOffset.TryParse(l[6..], out var d)) date = d;
            else if (l.StartsWith("Subject: ")) subj = l[9..];
        }
        // Attachment lines trail the body; peel them (and the blank before
        // them) off the end.
        var end = lines.Length;
        while (end > i && lines[end - 1].Trim().Length == 0) end--;
        var attachments = new List<string>();
        while (end > i && lines[end - 1].StartsWith("Attachment: "))
        {
            attachments.Insert(0, lines[end - 1][12..].Trim());
            end--;
        }
        while (end > i && lines[end - 1].Trim().Length == 0) end--;
        var body = string.Join("\n", lines[i..end]);
        return new Message(fromH, toH, cc, date, subj, body, attachments);
    }

    /// "Dana Levi <dana@x.com>" → "Dana Levi"; a bare address stays as is.
    public static string DisplayName(string from)
    {
        var s = (from ?? "").Trim();
        var lt = s.IndexOf('<');
        if (lt > 0) s = s[..lt].Trim().Trim('"');
        return s.Length > 0 ? s : (from ?? "");
    }

    /// First meaningful lines of the latest message, for the list row. Quoted
    /// replies ("> …", "On … wrote:") are what every reply ends with and say
    /// nothing new, so they are skipped.
    public static string Snippet(Thread t, int max = 160)
    {
        var last = t.Messages.LastOrDefault();
        if (last == null) return "";
        var parts = new List<string>();
        foreach (var raw in last.Body.Split('\n'))
        {
            var l = raw.Trim();
            if (l.StartsWith(">")) continue;
            if (Regex.IsMatch(l, @"^On .+wrote:$")) break;
            if (l.Length == 0) continue;
            parts.Add(l);
            if (parts.Sum(p => p.Length) > max) break;
        }
        var s = string.Join(" ", parts);
        return s.Length > max ? s[..max].TrimEnd() + "…" : s;
    }

    public static bool IsImage(string name)
    {
        var ext = System.IO.Path.GetExtension(name ?? "").ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
    }

    // ---- state file ------------------------------------------------------

    internal sealed class Entry
    {
        [JsonPropertyName("state")] public string State { get; set; } = "new";
        [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
        /// Machine that made the change; only for the curious reader of the file.
        [JsonPropertyName("by")] public string By { get; set; } = "";
    }

    internal sealed class StateFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("threads")] public Dictionary<string, Entry> Threads { get; set; } = new(StringComparer.Ordinal);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static StateFile ParseState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new StateFile();
        try
        {
            var f = JsonSerializer.Deserialize<StateFile>(json, Json) ?? new StateFile();
            f.Threads = new Dictionary<string, Entry>(f.Threads ?? new(), StringComparer.Ordinal);
            return f;
        }
        catch (JsonException) { return new StateFile(); }
    }

    public static string SerializeState(StateFile f) => JsonSerializer.Serialize(f, Json);

    /// Per thread, the later change wins. Two PCs editing different threads
    /// both keep their edits; the same thread settles on whoever touched it
    /// last. Returns a new file; neither input is modified.
    public static StateFile Merge(StateFile a, StateFile b)
    {
        var o = new StateFile();
        foreach (var src in new[] { a, b })
            foreach (var (id, e) in src.Threads)
                if (!o.Threads.TryGetValue(id, out var cur) || e.UpdatedAt > cur.UpdatedAt)
                    o.Threads[id] = new Entry { State = e.State, UpdatedAt = e.UpdatedAt, By = e.By };
        return o;
    }

    /// The state a thread shows. A thread re-exported (re-labelled in Gmail)
    /// after its last state change has new mail in it, so a done or read
    /// thread comes back as new — the same way a mail client re-bolds a thread
    /// that got a reply.
    public static string Effective(Entry? e, DateTimeOffset threadModified)
    {
        if (e == null) return "new";
        if (threadModified > e.UpdatedAt && e.State is "done" or "read") return "new";
        return States.Contains(e.State) ? e.State : "new";
    }
}
