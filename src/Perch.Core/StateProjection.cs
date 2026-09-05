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
    public static object BuildSnapshot(
        SessionStore store, Guid? activePaneId, int fontSize, bool onboardingSeen,
        ProjectStore? projects = null, string sidebarMode = "sessions",
        IReadOnlyList<ModelUsageLimit>? modelLimits = null, bool inspectorOpen = true,
        bool wideLayout = false, bool localPerchOnly = false,
        Func<Guid, object?>? teamOf = null, bool teamFacesColor = false,
        IReadOnlyList<CodexModel>? codexModels = null)
    {
        return new
        {
            type = "state",
            activeSessionId = store.ActiveSessionId?.ToString("D") ?? "",
            activePaneId    = activePaneId?.ToString("D") ?? "",
            // User profile dir, so the page can expand a "~\…" path (Claude
            // Code abbreviates the home dir in its file recaps) into a real
            // file:// URL for the HTML-file link menu.
            homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            prefs = new { fontSize, onboardingSeen, sidebarMode, inspectorOpen, wideLayout, localPerchOnly, teamFacesColor },
            // Account-wide model rate limits (usually empty — the endpoint 429s).
            // Only the AT-LIMIT models ship: the picker disables exactly these
            // and annotates each with its reset time. Empty / absent → every
            // model enabled, no annotations. Ferried with every push like prefs.
            modelLimits = (modelLimits ?? Array.Empty<ModelUsageLimit>())
                .Where(l => l.AtLimit)
                .Select(l => new { alias = l.Alias, resetsAtMs = l.ResetsAtMs })
                .ToArray(),
            // What the model picker offers on a CODEX pane. Read from codex's
            // own catalogue rather than hardcoded, so the list can't rot; empty
            // when codex isn't installed, and the picker then stays hidden for
            // codex panes exactly as it did before.
            codexModels = (codexModels ?? Array.Empty<CodexModel>())
                .Select(m => new { slug = m.Slug, label = m.Label })
                .ToArray(),
            // Registered repos, for the sidebar's project mode. Ferried with
            // every push like prefs — the list is tiny and the page then never
            // has to ask for it separately.
            projects = (projects?.Projects ?? new List<Project>()).Select(p => new
            {
                id = p.Id.ToString("D"),
                name = p.Name,
                path = p.Path,
                hidden = p.Hidden,
                // The project's team (bots + positions), or null when it has
                // none. Bots are also ordinary rows in `sessions`; this is
                // what lets the sidebar badge them and show the room's door.
                team = teamOf?.Invoke(p.Id),
            }).ToArray(),
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
                    // Boards excluded: this count answers "how much was running
                    // in that tab", and a board is never running anything.
                    paneCount = leaves.Count(p => !p.IsBoard),
                    // Either agent's saved conversation counts — both can be
                    // resumed, just with different commands (see ResumeCommand).
                    resumableCount = leaves.Count(p => !string.IsNullOrEmpty(p.ClaudeSessionId)
                                                    || !string.IsNullOrEmpty(p.CodexSessionId)),
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
        // "3 panes · 1 waiting" is a statement about work in flight, so a board
        // — which never runs anything — is not one of the three.
        var workLeaves = leaves.Where(p => !p.IsBoard).ToArray();
        var paneCount = workLeaves.Length;
        var waitingCount = workLeaves.Count(p => p.AgentState is AgentState.Waiting or AgentState.Permission);
        var workingCount = workLeaves.Count(p => p.AgentState == AgentState.Working);
        // Panes sharing a cwd measure the SAME working tree (a split inside
        // one project), so summing their diff stats double-counted every
        // line. Within a cwd group keep the single largest measurement — a
        // coherent (added, deleted, files) triple from one pane, typically
        // the one with the oldest baseline, whose diff already contains the
        // others' — and sum across distinct cwds. Panes with no known cwd
        // can't be correlated, so each stays its own group (old behavior).
        // Different subdirs of one repo still read as distinct groups; rare,
        // and resolving them would need a git call in a pure projection.
        // EXCEPT: a pane whose stats were hook-attributed (DiffAttributed —
        // the shared-tree case, filtered to its own agent's touched files)
        // is already disjoint from its siblings' by construction, so it keeps
        // its own group and SUMS; deduping would drop real work.
        var diffLeaves = leaves
            .GroupBy(p => p.DiffAttributed || string.IsNullOrEmpty(p.Cwd)
                        ? "pane:" + p.Id.ToString("D")
                        : "cwd:" + p.Cwd,
                     StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.LinesAdded + p.LinesDeleted).First())
            .ToArray();
        return new
        {
            id    = s.Id.ToString("D"),
            title = s.Title,
            shell = s.DisplayShell,
            /* The project (registered repo) this tab belongs to, or "" when it
             * isn't filed under one — project mode puts those under "Other". */
            projectId = s.ProjectId?.ToString("D") ?? "",
            /* Deliberately slept by the user: the sidebar files it under its
             * project's collapsed "Idle" group instead of the active list.
             * A flag, not a reading of agentState — see Session.Dormant. */
            dormant = s.Dormant,
            /* The branch this tab's worktree was cut on; "" when it has no
             * worktree (a plain tab, or any non-project session). The page uses
             * it to offer "also delete the worktree folder" when closing — and
             * to say WHICH branch survives, since that's the reassuring part. */
            worktreeBranch = s.WorktreeBranch,
            /* Absolute path to this tab's board folder, "" when it has none.
             * The board leaf reads it from here rather than carrying its own
             * copy, so there is exactly one place the path can be wrong. */
            boardPath = s.BoardPath,
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
            /* Cross-session pairing: the partner tab's id ("" when unpaired) and
             * the last peer message that arrived here (null when none). The
             * sidebar pulls paired rows adjacent, joins them with a gutter
             * bracket, and renders the note as a quiet info line — never an
             * attention state. */
            pairedWith = s.PairedWithId?.ToString("D") ?? "",
            pairNote = string.IsNullOrEmpty(s.PairNoteText) ? null : new
            {
                from  = s.PairNoteFrom,
                text  = s.PairNoteText,
                level = LevelToString(s.PairNoteLevel),
                atMs  = s.PairNoteAtMs,
            },
            // Which agents run in this tab: one entry per distinct agent in
            // pane order ("claude" / "codex"), none for plain shells. The
            // sidebar row and the dashboard card wear one mark per entry.
            agents = leaves.Select(p => p.AgentType)
                           .Where(a => !string.IsNullOrEmpty(a))
                           .Distinct()
                           .ToArray(),
            // Pane breakdown so the sidebar can say "3 panes · 1 waiting".
            paneCount,
            waitingCount,
            workingCount,
            // Git signal aggregated across panes: total diff size (the
            // session's whole footprint, deduped by cwd — see diffLeaves
            // above) and the largest unpushed count (panes usually share
            // a branch).
            linesAdded   = diffLeaves.Sum(p => p.LinesAdded),
            linesDeleted = diffLeaves.Sum(p => p.LinesDeleted),
            filesChanged = diffLeaves.Sum(p => p.FilesChanged),
            ahead        = leaves.Select(p => p.Ahead).DefaultIfEmpty(0).Max(),
            // Commits THIS session's agents claimed via the git.commit hook,
            // still unpushed. Summed, not Maxed: each sha is claimed by exactly
            // one pane (the one whose Bash call made it), so panes are disjoint.
            aheadMine    = leaves.Sum(p => p.AheadMine),
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
                // Leaf kind, second discriminator after `url`. The board's PATH
                // is not here — it belongs to the session (see Session.BoardPath)
                // and is projected there, so a leaf only says "I am the window
                // onto this tab's board".
                isBoard = node.IsBoard,
                colorIndex = node.ColorIndex,
                // Per-pane state — shows up in the pane header so each
                // pane's agent status is visible at a glance, no clicking
                // through the sidebar to figure out which one needs you.
                agentState = StateToString(node.AgentState),
                // Which agent runs here ("claude" / "codex" / "") — drives the
                // small CC badge in the header.
                agentType = node.AgentType,
                // The model label under the pane's name. Two sources, and the
                // agent decides which is true: Perch CHOOSES Claude's (the
                // alias the user picked, which the launcher passes), so that's
                // the honest one there — while codex resolves its own and its
                // user can change it inside the TUI, so for codex the only
                // truthful answer is what the agent reported. Falls back to the
                // picked alias when nothing has been reported yet.
                model = node.AgentType == "codex" && !string.IsNullOrEmpty(node.AgentModel)
                    ? node.AgentModel
                    : node.Model,
                activityDetail = node.ActivityDetail,
                branch = node.Branch,
                ports  = node.Ports,
                /* Commits AUTHORED here since cc session-start (HEAD baseline).
                 * 0 when no session is active. Surfaces as "+N commits" chip in
                 * the pane header so the user can see at a glance how much work
                 * the agent has actually landed. Commits a `git pull` brought in
                 * aren't the agent's and don't count. */
                commitCount = node.CommitCount,
                /* Session diff size (commits the agent authored + uncommitted +
                 * new untracked) and the unpushed-commit count — feed the
                 * "+A −D · ↑N" signal. */
                linesAdded   = node.LinesAdded,
                linesDeleted = node.LinesDeleted,
                filesChanged = node.FilesChanged,
                ahead        = node.Ahead,
                /* Of those, the ones THIS pane's agent made (hook-claimed shas
                 * ∩ unpushed) — the honest per-tab number where several tabs
                 * share one branch and `ahead` reads the same on all of them. */
                aheadMine    = node.AheadMine,
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
