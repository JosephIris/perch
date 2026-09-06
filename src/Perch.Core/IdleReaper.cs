using System;
using System.Collections.Generic;
using System.Linq;

namespace Perch;

/// One session the reaper judged. Pure data so the policy can be tested
/// without a live app: the sweep builds these on the UI thread, the policy
/// decides, the sweep acts.
internal sealed record ReapView(
    Guid SessionId,
    string Title,
    bool IsActive,
    bool Dormant,
    bool HasLivePty,
    bool HasAgent,
    bool Busy,
    bool HasPorts,
    bool Parked,
    double IdleHours);

/// Why a session was left alone — logged so a machine that never reaps can be
/// asked why without attaching a debugger.
internal enum ReapVerdict { Sleep, NoPty, NotAnAgent, Active, AlreadyAsleep, Busy, ServingPorts, Parked, NotIdleYet }

/// Auto-sleep for idle agent sessions, and the two sweeps that catch what
/// close and sleep cannot.
///
/// The incident this exists for: seven agent panes restored at launch sat
/// untouched for 56 hours on an app that never restarted, holding 5.7 GB —
/// four processes each (pane shell, claude, and TWO bun processes for one MCP
/// plugin server, which alone costs as much as claude). Nothing was going to
/// reclaim them. The close path (ShutdownPaneAsync) was never entered because
/// nobody ever closed them; the app-wide kill-on-close job only fires on quit;
/// and PaneJob deliberately sets no kill-on-close so a dev server outlives its
/// pane. All three were working as written. None covers "open, idle, forever".
///
/// The policy, and why each piece:
///
///   - **Sleep, don't kill.** OnSessionDormant already does the right teardown
///     — polite /exit so the transcript is saved, PTYs destroyed, the TAB KEPT,
///     and `claude --resume` on wake. It is reversible and it is the same cold
///     path verify-comms.ps1 exercises, so the reaper rides tested machinery
///     rather than inventing a second way to end a session.
///   - **Only agent sessions.** A session with no pane that ever reported an
///     agent is never touched, however idle. That is the axis that keeps the
///     PaneJob design intact: a shell you left a build running in is not an
///     agent session.
///   - **Never one that is serving.** A pane holding a loopback listener is a
///     dev server the Local panel exists to show you. Untouched.
///   - **Never the active tab, never a busy agent, never one with a team post
///     parked for it.**
///
/// The residual trade-off, stated honestly: sleeping a session tears down its
/// panes, so a DETACHED, non-listening background job started from an agent
/// pane (a long build that prints nothing) dies with it after the idle window.
/// A foreground job would have died at any close anyway; only the detached,
/// silent, portless case is newly affected.
internal sealed class IdleReaper
{
    /// Live sessions, as the store holds them.
    public required Func<IEnumerable<Session>> Sessions { get; init; }
    /// Terminal leaves of a session.
    public required Func<Session, IEnumerable<PaneNode>> Leaves { get; init; }
    /// Does this pane still own a PTY?
    public required Func<Guid, bool> HasPty { get; init; }
    /// Stopwatch ticks of the pane's last REAL activity — sustained output or
    /// a write into it. Null when the pane has never been seen.
    public required Func<Guid, long?> LastActivityTicks { get; init; }
    /// The tab the user is looking at.
    public required Func<Guid?> ActiveSessionId { get; init; }
    /// Is a team post queued for this session (waiting on its bot)?
    public required Func<Guid, bool> Parked { get; init; }
    /// Put the session to sleep — OnSessionDormant.
    public required Action<Guid> Sleep { get; init; }
    /// Every pane id that currently owns a PTY.
    public required Func<IEnumerable<Guid>> LivePaneIds { get; init; }
    /// Destroy a pane's PTY outright (no polite exit) — the orphan path, where
    /// there is no tab left to be polite on behalf of.
    public required Action<Guid> DestroyPane { get; init; }
    /// Told after each sweep, when it did something worth surfacing.
    public Action<string>? Announce { get; init; }

    /// Hours of no activity before an idle agent session is slept. 0 = off.
    public double IdleHours { get; set; } = 4;

    /// Judge one session. Pure — the order of the checks is the order of the
    /// reasons we want in the log, most-specific first.
    public static ReapVerdict Judge(ReapView v, double idleHours)
    {
        if (idleHours <= 0) return ReapVerdict.NotIdleYet;
        if (!v.HasLivePty) return ReapVerdict.NoPty;
        if (v.Dormant) return ReapVerdict.AlreadyAsleep;
        if (!v.HasAgent) return ReapVerdict.NotAnAgent;
        if (v.IsActive) return ReapVerdict.Active;
        if (v.Busy) return ReapVerdict.Busy;
        if (v.HasPorts) return ReapVerdict.ServingPorts;
        if (v.Parked) return ReapVerdict.Parked;
        if (v.IdleHours < idleHours) return ReapVerdict.NotIdleYet;
        return ReapVerdict.Sleep;
    }

    /// Build the view for one session. Idle is measured from the FRESHEST pane
    /// in the session: one busy pane keeps its quiet siblings alive, because
    /// they are one tab and sleep is a tab-level action.
    public ReapView Look(Session sess, long now, long freq)
    {
        var active = ActiveSessionId();
        var leaves = Leaves(sess).Where(p => p.IsTerminal).ToList();
        var live = leaves.Where(p => HasPty(p.Id)).ToList();
        long? freshest = null;
        foreach (var p in live)
        {
            var t = LastActivityTicks(p.Id);
            if (t is long ticks && (freshest == null || ticks > freshest)) freshest = ticks;
        }
        // No reading at all for a live pane = treat as fresh, not as ancient.
        // A pane we have never clocked is one we have no business sleeping.
        var idleHours = freshest is long f ? (now - f) / (double)freq / 3600.0 : 0;
        return new ReapView(
            SessionId: sess.Id,
            Title: sess.Title ?? "",
            IsActive: active == sess.Id,
            Dormant: sess.Dormant,
            HasLivePty: live.Count > 0,
            HasAgent: live.Any(p => !string.IsNullOrEmpty(p.AgentType)),
            Busy: live.Any(p => p.AgentState is AgentState.Working or AgentState.Waiting or AgentState.Permission),
            HasPorts: live.Any(p => p.Ports.Length > 0),
            Parked: Parked(sess.Id),
            IdleHours: idleHours);
    }

    /// The idle sweep. Returns how many sessions it slept.
    public int SweepIdle()
    {
        if (IdleHours <= 0) return 0;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var freq = System.Diagnostics.Stopwatch.Frequency;
        var doomed = new List<ReapView>();
        foreach (var sess in Sessions().ToList())
        {
            var view = Look(sess, now, freq);
            if (Judge(view, IdleHours) == ReapVerdict.Sleep) doomed.Add(view);
        }
        foreach (var v in doomed)
        {
            Log.Info("Reap.idle",
                $"session={v.SessionId:N} \"{v.Title}\" idle={v.IdleHours:F1}h ≥ {IdleHours:F1}h — sleeping (tab kept, resumes on wake)");
            Sleep(v.SessionId);
        }
        if (doomed.Count > 0)
            Announce?.Invoke(doomed.Count == 1
                ? $"Put “{doomed[0].Title}” to sleep after {doomed[0].IdleHours:F0}h idle — click it to resume"
                : $"Put {doomed.Count} idle agent tabs to sleep — click one to resume");
        return doomed.Count;
    }

    /// The orphan sweep — constraint 3, the forgotten-session case.
    ///
    /// Five of the seven leaked sessions had no record in sessions.json at all,
    /// so a reaper that walked the store would have missed the majority of the
    /// incident. This walks the other way: every pane that OWNS A PTY right now
    /// must belong to a live, awake tab. One that doesn't is holding a process
    /// tree nothing in the app can ever reach again — destroy it.
    ///
    /// Cheap (a set difference over live panes) and it needs no theory about
    /// HOW the record went missing, which is what makes it the right net.
    public int SweepOrphans()
    {
        var owned = new HashSet<Guid>();
        foreach (var sess in Sessions().ToList())
        {
            if (sess.Dormant) continue;   // a slept tab must have no PTY
            foreach (var p in Leaves(sess)) owned.Add(p.Id);
        }
        var orphans = LivePaneIds().Where(id => !owned.Contains(id)).ToList();
        foreach (var id in orphans)
        {
            Log.Info("Reap.orphan", $"pane={id:N} has a live PTY but belongs to no awake tab — destroying");
            DestroyPane(id);
        }
        return orphans.Count;
    }
}
