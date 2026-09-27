using System;
using System.Collections.Generic;

namespace Perch;

/// The two controls a project chat's Overview gives an open thread: Stop and
/// the "Steer this thread…" box. Both reach the thread's Claude the way a
/// person at its terminal would — Escape, and a typed line — but a thread is
/// usually watched from the Overview, not its terminal, so this also owns
/// what the terminal would have shown for free:
///
///   * Stop is Escape, and Claude Code fires NO hook on an interrupt. Left
///     alone the thread kept reading "working" (Stop button and all) until
///     the idle watchdog noticed the silence, and a second click then landed
///     a second Escape on an idle prompt — which is Claude Code's "rewind"
///     menu, where the next typed line went. So the pane is marked done at
///     once (inferred: the watchdog walks it back if output resumes), only a
///     Claude that is actually busy gets the key, and a double click sends
///     one Escape.
///   * A line to a thread stopped on a QUESTION ("It's waiting for your
///     answer — reply in the box below") could never go in: the delivery
///     waits for a Claude that isn't asking anything, and this one was
///     waiting for exactly that line. The question is dismissed (Escape) and
///     the reply goes in as the next prompt, a beat later so the key lands
///     first. On a permission prompt a line waits for the answer instead —
///     the card has Allow/Deny, and Escape there would be a silent deny.
internal sealed class ThreadSteering
{
    internal sealed class Host
    {
        /// The thread's Claude pane with a live terminal; null when none.
        public required Func<Session, PaneNode?> ClaudePane { get; init; }
        public required Action<Guid, byte[]> Write { get; init; }
        public required Action PushState { get; init; }
        /// The pane was just sent Escape and marked done: its "Interrupted"
        /// repaint must not read to the idle watchdog as the turn resuming.
        public Action<Guid>? Escaped { get; init; }
        /// Monotonic milliseconds (Environment.TickCount64).
        public Func<long> NowMs { get; init; } = () => Environment.TickCount64;
    }

    public enum StopResult { NoClaude, NotBusy, AlreadyStopping, Stopped }
    public enum SendResult { Empty, Queued, AnsweredQuestion }

    /// A second Stop within this window is the same click, not a new one.
    internal const long StopDebounceMs = 1500;

    private readonly Host _h;
    private readonly LineDelivery _delivery;
    private readonly Dictionary<Guid, long> _escapedAt = new();

    public ThreadSteering(Host host, LineDelivery delivery)
    {
        _h = host;
        _delivery = delivery;
    }

    public StopResult Stop(Session t)
    {
        if (_h.ClaudePane(t) is not PaneNode p)
        {
            Log.Info("Thread.stop", $"session={t.Id:N} no running Claude");
            return StopResult.NoClaude;
        }
        // Escape at an idle prompt is not harmless: twice is the rewind menu.
        if (p.AgentState is not (AgentState.Working or AgentState.Permission or AgentState.Waiting))
            return StopResult.NotBusy;
        if (_escapedAt.TryGetValue(p.Id, out var at) && _h.NowMs() - at < StopDebounceMs)
            return StopResult.AlreadyStopping;
        Escape(t, p);
        Log.Info("Thread.stop", $"session={t.Id:N} pane={p.Id:N}");
        // Anything the user queued while it worked goes in now.
        _delivery.OnFree(t.Id);
        return StopResult.Stopped;
    }

    public SendResult Send(Session t, string? text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return SendResult.Empty;
        t.ThreadResolved = false;
        if (_h.ClaudePane(t) is PaneNode { AgentState: AgentState.Waiting } p)
        {
            Escape(t, p);
            Log.Info("Thread.send", $"session={t.Id:N} dismissed its question for the reply");
            _delivery.Enqueue(t.Id, text, pumpNow: false);
            _delivery.OnFree(t.Id);
            return SendResult.AnsweredQuestion;
        }
        _delivery.Enqueue(t.Id, text);
        _h.PushState();
        return SendResult.Queued;
    }

    private void Escape(Session t, PaneNode p)
    {
        try { _h.Write(p.Id, new byte[] { 0x1b }); }
        catch (Exception ex) { Log.Info("Thread.stop", $"escape into {p.Id:N} failed: {ex.Message}"); return; }
        _escapedAt[p.Id] = _h.NowMs();
        // No hook reports an interrupt: say "done" now. Inferred, so the idle
        // watchdog puts it back to working if the Claude carries on after all.
        p.AgentState = AgentState.Done;
        p.StateInferred = true;
        p.TurnStartUnixMs = 0;
        p.DoneAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        p.NotificationText = "";
        t.ThreadAsk = "";
        _h.Escaped?.Invoke(p.Id);
        _h.PushState();
    }
}
