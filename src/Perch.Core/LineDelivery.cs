using System;
using System.Collections.Generic;
using System.Linq;

namespace Perch;

/// Types lines into a tab's Claude so that each one is actually SUBMITTED —
/// the job the team room's delivery did, cut down to what a project chat and
/// its threads need, with no bots or room ledger attached.
///
/// A line is typed only when the Claude is up and free: not mid-turn, not on
/// a permission or waiting prompt. Every line starts with a tag,
/// `[Perch #12]`, and the prompt-submit hook echoes the head of whatever was
/// submitted; seeing our tag there is the only proof a line went in. A typed
/// line with no echo gets Enter pressed again (Claude Code's paste detection
/// can swallow the first), and then waits for the next free moment — it is
/// never RE-TYPED, because a line that did go in unobserved would then arrive
/// twice. One line in flight per tab; the rest queue in order.
internal sealed class LineDelivery
{
    internal sealed class Host
    {
        public required Func<Guid, Session?> SessionById { get; init; }
        /// A Claude pane with a live terminal that has reported its session.
        public required Func<Session, bool> ClaudeUp { get; init; }
        /// Mid-turn, or blocked on a permission / waiting prompt.
        public required Func<Session, bool> Busy { get; init; }
        public required Func<Session, string, bool> Type { get; init; }
        public required Func<Session, bool> PressEnter { get; init; }
        public required Action<Action, TimeSpan> Delay { get; init; }
        /// Start (or wake) the tab's terminal; its Claude reports in later.
        public required Action<Session> EnsureRunning { get; init; }
        /// Before a line goes in: true when the tab's Claude is being started
        /// over in a fresh session (a thread grown long); the line waits for
        /// the new one to come up.
        public Func<Session, bool>? StartFresh { get; init; }
        /// A line that could not be submitted after every retry.
        public Action<Session, string>? GaveUp { get; init; }
        /// A tab's queue changed (a line added, submitted or given up on) —
        /// what the page shows as "waiting to send" under a thread.
        public Action<Guid>? Changed { get; init; }
        /// Keep a message too long to type in a file the Claude can read:
        /// (tab, full text) → its path, or null when it could not be written.
        public Func<Guid, string, string?>? SaveLong { get; init; }
    }

    private sealed class Line
    {
        public required int Seq { get; init; }
        public required string Text { get; init; }
        /// The line as the user wrote it: no tag, one line.
        public required string Shown { get; init; }
        /// The text as it was queued, for saving across a restart.
        public required string Original { get; init; }
        public bool Typed;
        public int Holds;
    }

    /// Seconds after typing to look for the echo; Enter is pressed again at
    /// each miss, then the line waits for the tab's next free moment.
    internal static readonly TimeSpan[] Checks = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10) };
    /// Free moments a typed line may wait through before it is given up on.
    internal const int HoldLimit = 3;
    /// Longest text typed. Longer text goes in a file the line points at.
    /// Measured on a thread (Claude Code 2.1.283, Windows): a line typed in
    /// one write arrives as a paste, and up to ~950 characters that paste is
    /// submitted whole. Past ~1,000 the first ~1,013 characters were LOST and
    /// only the tail was submitted — four times out of four — so the thread
    /// answered "your message was cut off, I only see '…t). Keep queries
    /// cheap'" and the project chat resent, split, and resent again. 800
    /// leaves room for the tag and a prefix under the size that always landed.
    internal const int MaxChars = 800;
    /// How much of a long message the typed line still carries, so the
    /// thread's conversation reads as what was said.
    internal const int PreviewChars = 300;

    private readonly Host _h;
    private readonly Dictionary<Guid, List<Line>> _queues = new();
    private readonly HashSet<Guid> _checking = new();
    private int _seq;

    public LineDelivery(Host host) { _h = host; }

    public static string Tag(int seq) => $"[Perch #{seq}]";

    /// Claude Code submits on a typed newline, so a line must be one line.
    public static string Flatten(string text)
    {
        var s = OneLine(text);
        return s.Length > MaxChars ? s[..MaxChars] + "…" : s;
    }

    private static string OneLine(string? text)
    {
        var s = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        return string.Join("  ", s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    /// The line typed for a message too long to type: its opening words, then
    /// where the whole of it is. Pure.
    internal static string PointerLine(string oneLine, string path)
    {
        var head = oneLine.Length <= PreviewChars ? oneLine : oneLine[..PreviewChars];
        var cut = head.LastIndexOf(' ');
        if (oneLine.Length > PreviewChars && cut > PreviewChars / 2) head = head[..cut];
        return $"{head.TrimEnd()}… (This message is longer than can be typed; the whole of it is in {path} — read it all before acting.)";
    }

    /// What gets typed for `text`: the text as one line, or — when that is
    /// too long to type safely — a pointer to a file holding all of it.
    /// Never silently cut: without a place to keep it, the cut is marked.
    private string Typed(Guid sessionId, string text)
    {
        var one = OneLine(text);
        if (one.Length <= MaxChars) return one;
        string? path = null;
        try { path = _h.SaveLong?.Invoke(sessionId, text); }
        catch (Exception ex) { Log.Error("Delivery.saveLong", ex); }
        return path != null ? PointerLine(one, path) : Flatten(text);
    }

    /// Queue a line for a tab and (unless `pumpNow` is false — the caller has
    /// just pressed a key that must land first) try it at once. Returns its
    /// sequence number.
    public int Enqueue(Guid sessionId, string text, bool pumpNow = true)
    {
        var seq = ++_seq;
        var typed = Typed(sessionId, text);
        var one = OneLine(text);
        var shown = one.Length > MaxChars ? one[..MaxChars] + "…" : one;
        var line = new Line { Seq = seq, Text = $"{Tag(seq)} {typed}", Shown = shown, Original = text };
        if (!_queues.TryGetValue(sessionId, out var q)) _queues[sessionId] = q = new List<Line>();
        q.Add(line);
        Log.Info("Delivery.queue", $"session={sessionId:N} seq={line.Seq} queued={q.Count}");
        _h.Changed?.Invoke(sessionId);
        if (pumpNow) Pump(sessionId);
        return line.Seq;
    }

    public int Queued(Guid sessionId) => _queues.TryGetValue(sessionId, out var q) ? q.Count : 0;

    /// The lines still waiting to go into a tab, oldest first, as written.
    public IReadOnlyList<string> Pending(Guid sessionId) =>
        _queues.TryGetValue(sessionId, out var q) ? q.Select(l => l.Shown).ToArray() : Array.Empty<string>();

    /// The lines not typed yet, as queued — what is saved so a restart
    /// doesn't lose them. A line already typed is left out: it may have gone
    /// in unseen, and a line must never arrive twice.
    public string[] Unsent(Guid sessionId) =>
        _queues.TryGetValue(sessionId, out var q) ? q.Where(l => !l.Typed).Select(l => l.Original).ToArray() : Array.Empty<string>();

    /// The tab's Claude just came up (session-start hook): let its paint
    /// settle, then deliver.
    public void OnAgentUp(Guid sessionId) => _h.Delay(() => Pump(sessionId), TimeSpan.FromSeconds(4));

    /// The tab's Claude finished a turn or went idle.
    public void OnFree(Guid sessionId) => _h.Delay(() => Pump(sessionId), TimeSpan.FromMilliseconds(600));

    /// A prompt was submitted in the tab. `detail` is the head of it, as the
    /// prompt-submit hook reports it.
    public void OnPromptSubmitted(Guid sessionId, string detail)
    {
        if (!_queues.TryGetValue(sessionId, out var q) || q.Count == 0) return;
        var head = q[0];
        if (!head.Typed || !Echoes(detail, head.Seq)) return;
        q.RemoveAt(0);
        Log.Info("Delivery.submitted", $"session={sessionId:N} seq={head.Seq}");
        _h.Changed?.Invoke(sessionId);
        // The Claude is working on it now; the next line waits for it to finish.
        if (q.Count > 0) OnFree(sessionId);
    }

    /// Whether a submitted prompt's head is line `seq`. A long line typed in
    /// one write trips Claude Code's paste detection and is submitted wrapped
    /// — `\n\n<pasted_content id="6ffc">\n[Perch #3] …` — which a plain
    /// StartsWith never matched: the line went in, was never confirmed, had
    /// Enter pressed at it for a minute and was then reported as undelivered,
    /// while every line queued behind it waited. And after a Stop, Claude Code
    /// puts the stopped prompt back in the box, so the next line could go in
    /// glued behind it: `[Perch #9] …[Perch #10] …` (seen live). So the tag
    /// counts wherever it is — a tag is unique to the one line it was made
    /// for. Pure.
    internal static bool Echoes(string? detail, int seq) =>
        (detail ?? "").Contains(Tag(seq), StringComparison.Ordinal);

    public void Pump(Guid sessionId)
    {
        if (!_queues.TryGetValue(sessionId, out var q) || q.Count == 0) return;
        if (_checking.Contains(sessionId)) return;
        var sess = _h.SessionById(sessionId);
        if (sess == null) { _queues.Remove(sessionId); _h.Changed?.Invoke(sessionId); return; }
        // Before waking or typing: a head line already typed is in the old
        // session's box, so it goes on there.
        if (!q[0].Typed && _h.StartFresh?.Invoke(sess) == true) return;
        if (!_h.ClaudeUp(sess)) { _h.EnsureRunning(sess); return; }
        if (_h.Busy(sess)) return;

        var head = q[0];
        if (head.Typed)
        {
            // Typed earlier and never confirmed: it is sitting in the input.
            // Enter again rather than typing it a second time.
            if (++head.Holds > HoldLimit)
            {
                q.RemoveAt(0);
                Log.Info("Delivery.gaveup", $"session={sessionId:N} seq={head.Seq}");
                _h.GaveUp?.Invoke(sess, head.Text);
                _h.Changed?.Invoke(sessionId);
                if (q.Count > 0) OnFree(sessionId);
                return;
            }
            _h.PressEnter(sess);
        }
        else
        {
            if (!_h.Type(sess, head.Text)) return;
            head.Typed = true;
            _h.Changed?.Invoke(sessionId);
        }
        _checking.Add(sessionId);
        Check(sessionId, head.Seq, 0);
    }

    private void Check(Guid sessionId, int seq, int step)
    {
        _h.Delay(() =>
        {
            var q = _queues.GetValueOrDefault(sessionId);
            if (q == null || q.Count == 0 || q[0].Seq != seq) { _checking.Remove(sessionId); return; }   // confirmed
            if (step + 1 < Checks.Length)
            {
                // Enter again — but never into a Claude that is now asking
                // something: on a permission prompt Enter is "Yes", on a
                // question it picks the highlighted answer. A line that did
                // go in unseen costs nothing to skip; the hold below retries.
                if (_h.SessionById(sessionId) is Session s && !_h.Busy(s)) _h.PressEnter(s);
                Check(sessionId, seq, step + 1);
                return;
            }
            // Out of checks: hold it for the next free moment — a finished turn
            // brings one, and so does this retry, for a Claude that is simply
            // sitting idle and will report no further state change.
            _checking.Remove(sessionId);
            Log.Info("Delivery.held", $"session={sessionId:N} seq={seq}");
            _h.Delay(() => Pump(sessionId), TimeSpan.FromSeconds(20));
        }, Checks[step]);
    }
}
