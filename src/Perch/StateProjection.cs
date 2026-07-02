using System;
using System.Collections.Generic;
using System.Linq;

namespace Perch;

/// Builds the `state` snapshot the host pushes to the page, and owns the
/// agent-state string mappings + the session-row aggregation rules. Pure
/// functions over the session model — no window, no WebView2 — so the
/// aggregation subtleties (Done outranks Working, turn clocks filtered to the
/// panes' CURRENT state, …) are unit-tested instead of re-discovered as
/// sidebar bugs.
internal static class StateProjection
{
    public static AgentState ParseAgentState(string? s) => s switch
    {
        "working"    => AgentState.Working,
        "done"       => AgentState.Done,
        "waiting"    => AgentState.Waiting,
        "permission" => AgentState.Permission,
        _            => AgentState.Idle,
    };

    public static NotificationLevel ParseLevel(string? s) => s switch
    {
        "success" => NotificationLevel.Success,
        "warn"    => NotificationLevel.Warn,
        "warning" => NotificationLevel.Warn,
        "error"   => NotificationLevel.Error,
        _         => NotificationLevel.Info,
    };

    public static string StateToString(AgentState s) => s switch
    {
        AgentState.Working    => "working",
        AgentState.Done       => "done",
        AgentState.Waiting    => "waiting",
        AgentState.Permission => "permission",
        _                     => "idle",
    };

    public static string LevelToString(NotificationLevel l) => l switch
    {
        NotificationLevel.Success => "success",
        NotificationLevel.Warn    => "warn",
        NotificationLevel.Error   => "error",
        _                         => "info",
    };

    /// Most-urgent state across panes. Drives the session row indicator.
    /// Order: Permission > Waiting > Done > Working > Idle. Done outranks
    /// Working so a session with one finished pane (your move) surfaces as
    /// "ready" even while its other panes still churn.
    public static AgentState AggregateState(IEnumerable<PaneNode> leaves)
    {
        var seen = AgentState.Idle;
        foreach (var p in leaves)
        {
            if (p.AgentState == AgentState.Permission) return AgentState.Permission;
            // Rank the remaining states; never let a lower one overwrite a
            // higher one already seen.
            var rank = Rank(p.AgentState);
            if (rank > Rank(seen)) seen = p.AgentState;
        }
        return seen;

        static int Rank(AgentState s) => s switch
        {
            AgentState.Waiting    => 3,
            AgentState.Done       => 2,
            AgentState.Working    => 1,
            _                     => 0, // Idle
        };
    }

    /// The full `state` message payload (anonymous object tree, serialized by
    /// the caller). Prefs are ferried with every push — cheap, and the page
    /// never has to ask.
    public static object BuildSnapshot(SessionStore store, Guid? activePaneId, int fontSize, bool onboardingSeen)
    {
        return new
        {
            type = "state",
            activeSessionId = store.ActiveSessionId?.ToString("D") ?? "",
            activePaneId    = activePaneId?.ToString("D") ?? "",
            prefs = new { fontSize, onboardingSeen },
            sessions = store.Sessions.Select(ProjectSession).ToArray(),
            // Recently-closed sessions for the sidebar's restore list. Just
            // the summary the row needs — title, pane/agent counts, and when
            // it was closed (the page renders "closed 5m ago" live).
            closedSessions = store.ClosedSessions.Select(s =>
            {
                var leaves = PaneTree.AllLeaves(s.Root).ToArray();
                return new
                {
                    id = s.Id.ToString("D"),
                    title = s.Title,
                    paneCount = leaves.Length,
                    resumableCount = leaves.Count(p => !string.IsNullOrEmpty(p.ClaudeSessionId)),
                    closedAtMs = s.ClosedAtUnixMs,
                };
            }).ToArray(),
        };
    }

    /// One session row: per-pane state aggregated to the sidebar's "most
    /// urgent wins" summary. The first pane with the winning state also lends
    /// its activity detail and notification (so the sidebar shows the one
    /// that wants attention).
    public static object ProjectSession(Session s)
    {
        var leaves = PaneTree.AllLeaves(s.Root).ToArray();
        var aggState = AggregateState(leaves);
        var attentionPane = leaves.FirstOrDefault(p => p.AgentState == aggState)
                         ?? leaves.FirstOrDefault();
        var anyNotify = leaves.FirstOrDefault(p => p.HasNotification);
        var paneCount = leaves.Length;
        var waitingCount = leaves.Count(p => p.AgentState is AgentState.Waiting or AgentState.Permission);
        var workingCount = leaves.Count(p => p.AgentState == AgentState.Working);
        return new
        {
            id    = s.Id.ToString("D"),
            title = s.Title,
            shell = s.DisplayShell,
            rootPane = ProjectPane(s.Root),
            agentState = StateToString(aggState),
            activityDetail = attentionPane?.ActivityDetail ?? "",
            // Branch + ports aggregate by union; user typically
            // has one branch per pane (one per worktree).
            branch = leaves.Select(p => p.Branch).FirstOrDefault(b => !string.IsNullOrEmpty(b)) ?? "",
            ports  = leaves.SelectMany(p => p.Ports).Distinct().ToArray(),
            notification = anyNotify == null ? null : new
            {
                text  = anyNotify.NotificationText,
                level = LevelToString(anyNotify.NotificationLevel),
            },
            // Pane breakdown so the sidebar can say "3 panes · 1 waiting".
            paneCount,
            waitingCount,
            workingCount,
            // Git signal aggregated across panes: total diff size
            // (the session's whole footprint) and the largest
            // unpushed count (panes usually share a branch).
            linesAdded   = leaves.Sum(p => p.LinesAdded),
            linesDeleted = leaves.Sum(p => p.LinesDeleted),
            filesChanged = leaves.Sum(p => p.FilesChanged),
            ahead        = leaves.Select(p => p.Ahead).DefaultIfEmpty(0).Max(),
            // Earliest working pane's start → "this session has been
            // working Xm". 0 when nothing is working.
            turnStartMs  = leaves
                .Where(p => p.AgentState == AgentState.Working && p.TurnStartUnixMs > 0)
                .Select(p => p.TurnStartUnixMs)
                .DefaultIfEmpty(0)
                .Min(),
            // Most-recent turn-end among panes that are CURRENTLY at
            // rest → "this session finished Xm ago". 0 when none is
            // done. Filtered to Done so a working pane's stale prior
            // turn-end never leaks into the live "ago".
            doneAtMs     = leaves
                .Where(p => p.AgentState == AgentState.Done && p.DoneAtUnixMs > 0)
                .Select(p => p.DoneAtUnixMs)
                .DefaultIfEmpty(0)
                .Max(),
            // Relative "last activity" for the dashboard card footer.
            lastActivity = s.LastActivityRelative,
        };
    }

    public static object ProjectPane(PaneNode node)
    {
        if (node.IsLeaf)
            return new
            {
                kind = "leaf",
                paneId = node.Id.ToString("D"),
                // Size weight within the parent split (flex-grow). See
                // PaneNode.Weight. Applied by the web on each rebuild.
                weight = node.Weight,
                name = node.Name ?? "pane",
                // Full first-prompt text for the header hover tooltip; the
                // label above is a 40-char cut of it. Empty when the pane was
                // never auto-named from a prompt (placeholder / user-named).
                nameFull = node.NamePrompt ?? "",
                url = node.Url,
                colorIndex = node.ColorIndex,
                // Per-pane state — shows up in the pane header so each
                // pane's agent status is visible at a glance, no clicking
                // through the sidebar to figure out which one needs you.
                agentState = StateToString(node.AgentState),
                // Which agent runs here ("claude" / "codex" / "") — drives the
                // small CC badge in the header.
                agentType = node.AgentType,
                activityDetail = node.ActivityDetail,
                branch = node.Branch,
                ports  = node.Ports,
                /* Commits made since cc session-start (HEAD baseline). 0 when
                 * no session is active. Surfaces as "+N commits" chip in the
                 * pane header so the user can see at a glance how much work
                 * the agent has actually landed. */
                commitCount = node.CommitCount,
                /* Diff size since baseline (committed + uncommitted) and the
                 * unpushed-commit count — feed the "+A −D · ↑N" signal. */
                linesAdded   = node.LinesAdded,
                linesDeleted = node.LinesDeleted,
                filesChanged = node.FilesChanged,
                ahead        = node.Ahead,
                /* Unix-ms the pane started its current working spell (0 when
                 * not working) — the page ticks "working · 2m" against it. */
                turnStartMs  = node.TurnStartUnixMs,
                /* Unix-ms the pane last finished a turn (0 if never) — the page
                 * ticks "finished · 2m ago" against it on done rows. */
                doneAtMs     = node.DoneAtUnixMs,
                notification = string.IsNullOrEmpty(node.NotificationText) ? null : new
                {
                    text  = node.NotificationText,
                    level = LevelToString(node.NotificationLevel),
                },
            };
        return new
        {
            kind = "split",
            // Stable id so pane.resizeSplit can address THIS split node when
            // the user drags one of its gutters.
            id = node.Id.ToString("D"),
            // This split's own size weight inside its parent split (1.0 at the
            // root, where it's ignored). Lets a nested split keep its share.
            weight = node.Weight,
            orientation = node.Split == SplitOrientation.Horizontal ? "h" : "v",
            children = node.Children.Select(ProjectPane).ToArray(),
        };
    }
}
