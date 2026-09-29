using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Perch;

/// A project chat's board: one card per item under review (a ticket, a
/// check), filled by the coordinator and its threads with
/// `perch thread board …` and drawn by the page as columns — Checking,
/// Needs you, Ready to approve, Done. Perch knows nothing about what the
/// items are; a preset tells the chat what goes on it.
///
/// Kept beside the chat's prompts as `board.json`. A card's draft is a file
/// the thread wrote; its text is read fresh each time the board is sent, so an
/// edit to the file shows without another command.
internal static class ReviewBoard
{
    internal sealed class Doc
    {
        public string Title { get; set; } = "";
        public string Summary { get; set; } = "";
        public List<Item> Items { get; set; } = new();
    }

    internal sealed class Item
    {
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        public string FullTitle { get; set; } = "";
        public int Thread { get; set; }
        /// checking | ok | bad | manual | pending (Tone); "" is checking.
        public string Verdict { get; set; } = "";
        /// What the verdict chip says; "" is the verdict's own word.
        public string Label { get; set; } = "";
        /// What the item would move to when approved ("In Progress").
        public string Status { get; set; } = "";
        public string Finding { get; set; } = "";
        /// Something only the user can settle; puts the card in Needs you.
        public string Question { get; set; } = "";
        /// Absolute path of the draft (a comment to post) the card shows.
        public string Draft { get; set; } = "";
        public string Url { get; set; } = "";
        /// Decided: "posted", "skipped" or any word; puts the card in Done.
        public string Done { get; set; } = "";
        public string Note { get; set; } = "";
        public long UpdatedMs { get; set; }
    }

    /// A draft's text is cut here when it is sent to the page.
    internal const int MaxDraftChars = 24 * 1024;

    internal static string PathFor(Session lead) => Path.Combine(ThreadController.DirFor(lead), "board.json");

    public static Doc Load(Session lead)
    {
        try
        {
            var path = PathFor(lead);
            if (File.Exists(path)) return JsonSerializer.Deserialize<Doc>(File.ReadAllText(path)) ?? new Doc();
        }
        catch (Exception ex) { Log.Error("Board.load", ex); }
        return new Doc();
    }

    public static void Save(Session lead, Doc doc)
    {
        Directory.CreateDirectory(ThreadController.DirFor(lead));
        AtomicFile.WriteAllText(PathFor(lead), JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// The verdict words a chat may use, to the five tones the page draws.
    /// Null when the word isn't one of them.
    internal static string? Tone(string word)
    {
        var w = word.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ').Replace("'", "");
        return w switch
        {
            "" or "checking" or "running" or "in progress" => "checking",
            "ok" or "verified" or "pass" or "passed" or "works" or "working" => "ok",
            "bad" or "not working" or "fail" or "failed" or "broken" => "bad",
            "manual" or "manual check" or "cant verify" or "cannot verify" or "unverified" => "manual",
            "pending" or "waiting" or "blocked" => "pending",
            _ => null,
        };
    }

    internal static string ToneLabel(string tone) => tone switch
    {
        "ok" => "Verified",
        "bad" => "Not working",
        "manual" => "Manual check",
        "pending" => "Pending",
        _ => "Checking",
    };

    /// Keys that `set` takes, as the CLI sends them.
    internal static readonly string[] Fields =
        { "title", "full-title", "thread", "verdict", "label", "status", "finding", "question", "draft", "url", "done", "note" };

    /// Apply `perch thread board set` fields to a card. Pure. Returns an
    /// error for the caller, or null.
    internal static string? Apply(Item it, IReadOnlyDictionary<string, string> f)
    {
        foreach (var (k, raw) in f)
        {
            var v = (raw ?? "").Trim();
            switch (k)
            {
                case "title": it.Title = v; break;
                case "full-title": it.FullTitle = v; break;
                case "thread":
                    if (v.Length == 0) { it.Thread = 0; break; }
                    if (!int.TryParse(v.TrimStart('#'), out var n) || n < 0) return $"--thread takes a thread number, not \"{v}\".";
                    it.Thread = n; break;
                case "verdict":
                    if (Tone(v) is not string tone) return $"--verdict \"{v}\" isn't one of: checking, verified, not-working, manual, pending.";
                    it.Verdict = tone; break;
                case "label": it.Label = v; break;
                case "status": it.Status = v; break;
                case "finding": it.Finding = v; break;
                case "question": it.Question = v; break;
                case "draft": it.Draft = v; break;
                case "url": it.Url = v; break;
                case "done": it.Done = v; break;
                case "note": it.Note = v; break;
                default: return $"Unknown field --{k}.";
            }
        }
        return null;
    }

    /// The board as the page draws it, or null when it has no cards.
    public static object? View(Session lead)
    {
        var doc = Load(lead);
        if (doc.Items.Count == 0) return null;
        return new
        {
            title = doc.Title,
            summary = doc.Summary,
            items = doc.Items.Select(i =>
            {
                var tone = Tone(i.Verdict) ?? "checking";
                return new
                {
                    key = i.Key, title = i.Title, fullTitle = i.FullTitle, thread = i.Thread,
                    tone, label = i.Label.Length > 0 ? i.Label : ToneLabel(tone),
                    status = i.Status, finding = i.Finding, question = i.Question,
                    draftPath = i.Draft, draft = ReadDraft(i.Draft), url = i.Url,
                    done = i.Done, note = i.Note, updatedMs = i.UpdatedMs,
                };
            }).ToArray(),
        };
    }

    private static string ReadDraft(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try
        {
            if (!File.Exists(path)) return "";
            var text = File.ReadAllText(path);
            return text.Length > MaxDraftChars ? text[..MaxDraftChars] + "\n\n[Cut here: the draft is longer than 24 KB.]" : text;
        }
        catch (Exception ex) { Log.Error("Board.draft", ex); return ""; }
    }

    /// `perch thread board show`: the board as lines.
    internal static string Text(Doc doc)
    {
        if (doc.Items.Count == 0) return "The board is empty.\n";
        var sb = new StringBuilder();
        if (doc.Title.Length > 0) sb.Append(doc.Title).Append('\n');
        foreach (var i in doc.Items)
        {
            var tone = Tone(i.Verdict) ?? "checking";
            sb.Append($"{i.Key} — {(i.Title.Length > 0 ? i.Title : "(no title)")} — {(i.Label.Length > 0 ? i.Label : ToneLabel(tone))}");
            if (i.Status.Length > 0) sb.Append($" → {i.Status}");
            if (i.Thread > 0) sb.Append($" — #{i.Thread}");
            if (i.Done.Length > 0) sb.Append($" — done: {i.Done}{(i.Note.Length > 0 ? $" ({i.Note})" : "")}");
            sb.Append('\n');
            if (i.Question.Length > 0) sb.Append($"   question: {i.Question}\n");
            if (i.Draft.Length > 0) sb.Append($"   draft: {i.Draft}\n");
        }
        return sb.ToString();
    }

    /// Run a `perch thread board <op>` for a chat. `thread` is the thread
    /// number the command came from (0 for the coordinator), used when a
    /// card doesn't say which thread it is. Returns the CLI's reply
    /// ("ok\n…" or "error\n…") and whether the board changed.
    internal static (string Reply, bool Changed) Run(Session lead, int thread, string op, string key, string? fieldsJson)
    {
        var doc = Load(lead);
        switch (op)
        {
            case "show":
                return ("ok\n" + Text(doc), false);
            case "clear":
                Save(lead, new Doc());
                return ("ok\nThe board is cleared.\n", true);
            case "title":
            {
                var f = Parse(fieldsJson);
                if (f == null) return ("error\nBad board fields.", false);
                doc.Title = key.Trim();
                if (f.TryGetValue("summary", out var s)) doc.Summary = s.Trim();
                Save(lead, doc);
                return ("ok\nBoard titled.\n", true);
            }
            case "remove":
            {
                var n = doc.Items.RemoveAll(i => i.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase));
                if (n == 0) return ($"error\nNo card {key} on the board.", false);
                Save(lead, doc);
                return ($"ok\nRemoved {key}.\n", true);
            }
            case "set":
            {
                key = key.Trim();
                if (key.Length == 0 || key.Length > 80) return ("error\nGive the card a key: perch thread board set <key> …", false);
                var f = Parse(fieldsJson);
                if (f == null) return ("error\nBad board fields.", false);
                var it = doc.Items.FirstOrDefault(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                var added = it == null;
                it ??= new Item { Key = key };
                if (Apply(it, f) is string err) return ("error\n" + err, false);
                if (it.Thread == 0 && thread > 0) it.Thread = thread;
                it.UpdatedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (added) doc.Items.Add(it);
                Save(lead, doc);
                return ($"ok\n{(added ? "Added" : "Updated")} {key}.\n", true);
            }
            default:
                return ("error\nUnknown board command. Use: set <key> … | title \"…\" | remove <key> | show | clear", false);
        }
    }

    private static Dictionary<string, string>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch (JsonException) { return null; }
    }
}
