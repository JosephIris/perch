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
        /// A line that could not be submitted after every retry.
        public Action<Session, string>? GaveUp { get; init; }
    }

    private sealed class Line
    {
        public required int Seq { get; init; }
        public required string Text { get; init; }
        public bool Typed;
        public int Holds;
    }

    /// Seconds after typing to look for the echo; Enter is pressed again at
    /// each miss, then the line waits for the tab's next free moment.
    internal static readonly TimeSpan[] Checks = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10) };
    /// Free moments a typed line may wait through before it is given up on.
    internal const int HoldLimit = 3;
    /// Longest line typed. Longer text goes in a file the line points at.
    internal const int MaxChars = 1500;

    private readonly Host _h;
    private readonly Dictionary<Guid, List<Line>> _queues = new();
    private readonly HashSet<Guid> _checking = new();
    private int _seq;

    public LineDelivery(Host host) { _h = host; }

    public static string Tag(int seq) => $"[Perch #{seq}]";

    /// Claude Code submits on a typed newline, so a line must be one line.
    public static string Flatten(string text)
    {
        var s = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        s = string.Join("  ", s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return s.Length > MaxChars ? s[..MaxChars] + "…" : s;
    }

    /// Queue a line for a tab and try it at once. Returns its sequence number.
    public int Enqueue(Guid sessionId, string text)
    {
        var seq = ++_seq;
        var line = new Line { Seq = seq, Text = $"{Tag(seq)} {Flatten(text)}" };
        if (!_queues.TryGetValue(sessionId, out var q)) _queues[sessionId] = q = new List<Line>();
        q.Add(line);
        Log.Info("Delivery.queue", $"session={sessionId:N} seq={line.Seq} queued={q.Count}");
        Pump(sessionId);
        return line.Seq;
    }

    public int Queued(Guid sessionId) => _queues.TryGetValue(sessionId, out var q) ? q.Count : 0;

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
        if (!head.Typed || !(detail ?? "").TrimStart().StartsWith(Tag(head.Seq), StringComparison.Ordinal)) return;
        q.RemoveAt(0);
        Log.Info("Delivery.submitted", $"session={sessionId:N} seq={head.Seq}");
        // The Claude is working on it now; the next line waits for it to finish.
        if (q.Count > 0) OnFree(sessionId);
    }

    public void Pump(Guid sessionId)
    {
        if (!_queues.TryGetValue(sessionId, out var q) || q.Count == 0) return;
        if (_checking.Contains(sessionId)) return;
        var sess = _h.SessionById(sessionId);
        if (sess == null) { _queues.Remove(sessionId); return; }
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
                if (q.Count > 0) OnFree(sessionId);
                return;
            }
            _h.PressEnter(sess);
        }
        else
        {
            if (!_h.Type(sess, head.Text)) return;
            head.Typed = true;
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
                if (_h.SessionById(sessionId) is Session s) _h.PressEnter(s);
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
