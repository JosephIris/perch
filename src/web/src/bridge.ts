// Thin wrapper around WebView2's host bridge. All page <-> host messages
// flow through here so component code never reaches for chrome.webview
// directly and we can fake the host in plain-browser dev later if we want.

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage(msg: unknown): void;
        addEventListener(
          type: "message",
          listener: (e: MessageEvent) => void
        ): void;
      };
    };
  }
}

// ---- Outgoing message shapes (page -> host) --------------------------------

export type OutMessage =
  | { type: "ready" }
  | { type: "pane.in"; paneId: string; b64: string }
  /* Backpressure ack: sent once xterm finishes writing a pane.out chunk so
   * the host can shrink that PTY's unacked backlog and resume reading.
   * `bytes` is the ORIGINAL pane.out byte count (pre-underline-injection),
   * matching what the host counted when it sent the chunk. */
  | { type: "pane.ack"; paneId: string; bytes: number }
  | { type: "pane.resize"; paneId: string; cols: number; rows: number }
  /* Test-only: reply to a host render.ping, measuring renderer round-trip. */
  | { type: "render.pong"; id: number }
  | { type: "pane.focus"; paneId: string }
  /* When `url` is set the new leaf is a webview pane (iframe) instead of
   * a terminal. Used by the URL action menu's "Open in pane right/down". */
  | { type: "pane.split"; paneId: string; dir: "right" | "down"; url?: string }
  | { type: "pane.close"; paneId: string }
  /* Answer to the in-pane new-pane chooser (see the "pane.chooser" InMessage).
   *   "agent"   → start an agent (Claude / Codex) in the source pane's dir
   *   "same"    → plain shell in the source pane's dir
   *   "default" → plain shell in the configured default dir
   *   "cancel"  → dismiss; the host closes the never-spawned pane (undo split). */
  | { type: "pane.chooser.choose"; paneId: string; choice: "agent" | "same" | "default" | "cancel" }
  /* Drag-resize of a split: new flex-grow weights for the addressed split's
   * children, in order. `final` is false for throttled mid-drag updates and
   * true (or omitted) on the final mouseup; the host only persists on final. */
  | { type: "pane.resizeSplit"; splitId: string; weights: number[]; final?: boolean }
  /* Drag-to-rearrange: move `src` pane next to `target` on the given edge
   * ("left"/"right" → vertical split, "top"/"bottom" → horizontal split,
   * "center" → swap the two panes). Within-session only. */
  | { type: "pane.move"; src: string; target: string; edge: "left" | "right" | "top" | "bottom" | "center" }
  /* Keyboard move: shift the active pane one slot within its parent split
   * (Ctrl+Shift+arrows). left/right reorder a side-by-side split, up/down a
   * stacked one; perpendicular / edge is a no-op host-side. */
  | { type: "pane.moveDir"; paneId: string; dir: "left" | "right" | "up" | "down" }
  /* Sidebar drag-reorder: place movedId before/after targetId. kind "project"
   * reorders the project groups; kind "tab" reorders a session within its
   * project (targetId must be a sibling tab in the same project). Host reorders
   * the backing array and persists. */
  | { type: "sidebar.reorder"; kind: "project" | "tab"; movedId: string; targetId: string; edge: "before" | "after" }
  /* `projectId` files the new tab under a project and opens it in that repo.
   * Omitted (the plain "New session" button) → unfiled, default cwd. */
  | { type: "session.new"; shell?: string; projectId?: string }
  | { type: "session.select"; id: string }
  | { type: "session.rename"; id: string; title: string }
  /* `removeWorktree` also reclaims the tab's worktree folder, which makes the
   * close permanent (a restore would otherwise reopen into a directory that no
   * longer exists). The branch survives regardless. */
  | { type: "session.close"; id: string; removeWorktree?: boolean }
  /* Bring a closed session back from "Recently closed" (restores layout +
   * cwd, and resumes its Claude panes when enabled). */
  | { type: "session.restore"; id: string }
  /* Permanently drop a closed session from "Recently closed". */
  | { type: "session.purge"; id: string }
  /* Answer to the one-time launch resume prompt. accept=true reopens the
   * saved Claude sessions; false leaves the panes as plain shells. */
  | { type: "resume.decision"; accept: boolean }
  /* Open a URL externally — host resolves to the OS default browser. */
  | { type: "url.open"; url: string }
  /* Pane rename + color tag changes from the pane header chrome. */
  | { type: "pane.rename"; paneId: string; name: string }
  | { type: "pane.recolor"; paneId: string; colorIndex: number }
  /* Per-pane Claude Code model pick from the header's model menu. `model` is a
   * CLI alias ("fable"/"opus"/"sonnet"/"haiku") or "" for the account default.
   * The host persists it, writes the wrap-claude state file (applied at the next
   * launch), and types `/model <alias>` live when cc is already running. */
  | { type: "pane.model"; paneId: string; model: string }
  /* Pane cwd update from xterm's OSC 7 handler. Host uses it to auto-fill
   * the branch chip via `git rev-parse`. */
  | { type: "pane.cwd"; paneId: string; cwd: string }
  /* State reconciliation: the page watched a pane's terminal buffer and it
   * disagrees with the host's agent state. permissionVisible=false — the cc
   * permission dialog left the screen (answered, denied, or Esc'd — exits
   * that fire no hook); host demotes to an inferred "working". blockedVisible
   * =true — a blocked dialog sits on a pane the host thinks is done; host
   * promotes to "waiting" if that done was itself inferred. blockedVisible=
   * false — the dialog behind an INFERRED waiting left; host unwinds it. */
  | { type: "pane.probe"; paneId: string; permissionVisible?: boolean; blockedVisible?: boolean }
  /* URL pane layout — page reports a rect for the placeholder; host
   * sizes a real WebView2 control to match. First layout creates the
   * WebView2; subsequent layouts reposition/resize. */
  | { type: "urlpane.layout"; paneId: string; url: string; x: number; y: number; w: number; h: number }
  | { type: "urlpane.dispose"; paneId: string }
  /* Per-pane show/hide on stage switch. Hides the native WebView2 (not close),
   * so returning to the tab is instant and doesn't reload the page. */
  | { type: "urlpane.visible"; paneId: string; visible: boolean }
  /* Airspace fix: each URL pane is a native WebView2 child HWND that composites
   * ABOVE the host's HTML, so a DOM modal can't paint over it. While a full-
   * viewport modal is up the page asks the host to hide every web pane (and to
   * restore them when it closes). See webpane-suppress.ts. */
  | { type: "ui.webpanes.suppress"; suppress: boolean }
  /* User preferences (terminal font size, Inspector rail open/closed, wide
   * layout mode) — host persists to Settings.cs so they survive restart. Each
   * field is optional so the page can update one without asserting the others. */
  | { type: "prefs.set"; fontSize?: number; inspectorOpen?: boolean; wideLayout?: boolean; localPerchOnly?: boolean }
  /* Recap: page asks the host for the unpushed-commit list behind a pane's
   * "↑N" chip (the hover tooltip / lightbox open lazily fetch it). Host
   * replies with a commits.data message for the same paneId. */
  | { type: "commits.request"; paneId: string }
  /* Inspector rail: page asks the host for everything it shows about one pane
   * — the transcript-derived journal/activity stream, the per-file change list,
   * and the vitals. Host replies with inspector.data for the same paneId.
   *
   * Request/reply rather than riding `state`: the state snapshot is
   * re-serialized in full on every agent status change (several times a second
   * under load), and a few hundred journal rows per pane would make that hot
   * path quadratic for data only ONE pane's rail ever displays. */
  | { type: "inspector.request"; paneId: string }
  /* Inspector rail: fetch one conversation image's bytes. Journal image rows
   * carry only an id (see InspectorEventView) — pixels are pulled on demand,
   * "thumb" for the rail (host downscales to ≤320px JPEG), "full" when the
   * lightbox opens. Host replies with inspector.image.data. */
  | { type: "inspector.image"; paneId: string; imageId: string; variant: "thumb" | "full" }
  /* Settings dialog: page asks the host for current settings + the list
   * of detected shells, host replies with a settings.data message. */
  | { type: "settings.request" }
  /* Settings dialog save. Each field is optional so the page can send a
   * partial update; the host only overwrites provided keys. defaultShell
   * is the shell COMMAND LINE (matching one of settings.data.shells[].cmd)
   * or "" for auto-detect. */
  | {
      type: "settings.save";
      defaultShell?: string;
      defaultCwd?: string;
      fontSize?: number;
      resumeAgentsOnLaunch?: boolean;
      projectScanRoots?: string[];
      worktreeRoot?: string;
      worktreeSeedPaths?: string[];
    }
  /* Page dismissed the onboarding lightbox → host marks it seen so it won't
   * auto-open next launch. */
  | { type: "onboarding.seen" }
  /* Sidebar mode toggle. "sessions" = the flat state-partitioned list;
   * "projects" = registered repos as headers with their tabs nested. Persisted
   * host-side, so the sidebar comes back the way you left it. */
  | { type: "ui.mode"; mode: "sessions" | "projects" }
  /* Ask the host for repos worth registering: the ones already open in a pane,
   * plus a one-level scan of each configured root. Host replies with a
   * projects.candidates message. */
  | { type: "projects.scan" }
  /* Open the native folder picker and register whatever is chosen. The escape
   * hatch for a repo that's neither open nor under a scan root. */
  | { type: "project.browse" }
  | { type: "project.add"; path: string; name?: string }
  /* Unregister. The project's tabs are NOT closed — they fall back to "Other". */
  | { type: "project.remove"; id: string }
  /* Rename a project, or override what its worktrees get seeded with. An EMPTY
   * seedPaths means "inherit the global list", not "seed nothing". */
  | { type: "project.update"; id: string; name?: string; seedPaths?: string[] }
  /* Create a tab under a project. `name` becomes the tab title, the branch
   * (slugified), and the cc session's --name. `worktree` cuts it its own git
   * worktree so parallel agents can't overwrite each other's files (and so the
   * per-tab loc/commit chips are actually true). The host picks a color unused
   * by that project's other tabs. */
  | {
      type: "project.tab.new";
      projectId: string;
      name: string;
      agent: "claude" | "codex" | "shell" | "browser";
      worktree: boolean;
      /* Claude model alias for the new tab ("fable"/"opus"/"sonnet"/"haiku");
       * omitted / "" = account default. Only sent when agent is "claude". */
      model?: string;
      /* Normalized URL for a browser tab — the tab's root leaf is a webview
       * pointed here instead of a terminal. Only sent when agent is "browser";
       * name is optional in that case (webview auto-titles from <title>). */
      url?: string;
    }
  /* User clicked the footer update pill. Host downloads the pending Velopack
   * update and relaunches into it (the process is replaced on success). */
  | { type: "update.apply" }
  /* Settings → "Check now": ask the host to check the feed right now. Unlike
   * the silent background checks, the host replies with an update.status so the
   * dialog can show the outcome (and still reveals the pill if one is found). */
  | { type: "update.check" }
  /* Cloud panel opened/closed. Drives the poll cadence: every gcloud tick is a
   * subprocess, so the host polls slowly (5 min) in the background and speeds up
   * to 1 min only while you're actually looking at the panel. */
  | { type: "cloud.panel"; open: boolean }
  /* Refresh button in the panel header. */
  | { type: "cloud.refresh" }
  /* Delete one machine. `id` is the host's stable key ("cluster/<name>" or
   * "<zone>/<name>"), never a bare name — a VM and a Dataproc cluster take
   * different gcloud delete commands and confusing them strands the workers. */
  | { type: "cloud.delete"; id: string }
  /* "Delete all" in the orphan area. */
  | { type: "cloud.deleteOrphans" }
  /* Local dev-servers panel opened/closed. Drives scan cadence: a port scan is
   * cheap but still a subprocess, so the host scans slowly in the background and
   * speeds up while you're actually looking at the panel. */
  | { type: "local.panel"; open: boolean }
  /* Rescan button in the panel header. */
  | { type: "local.refresh" }
  /* Open http://localhost:<port> in the system browser — host-side, so it lands
   * in the real default browser, not a webview popup. */
  | { type: "local.open"; port: number }
  /* Kill one server by its EXACT pid (never by name — a stale dev server and a
   * real service can share a process name). The host kills the tree so
   * npm → node children go with it. */
  | { type: "local.kill"; pid: number }
  /* "Kill all" in the lingering area — every server whose owning pane is gone. */
  | { type: "local.killLingering" };

// ---- Incoming message shapes (host -> page) --------------------------------

// Agent states, calm → loud. Surfaced words differ from the internal names:
//   idle       — dormant shell / agent exited. No badge.
//   working    — actively generating or running a tool.
//   done       — finished its turn, at rest. Shown to the user as "idle":
//                your move, nothing blocked, no rush. NOT a call for attention.
//   waiting    — RESERVED for a genuine "blocked waiting on your reply". No
//                longer auto-fired by Claude's 60s idle nudge (that stays
//                "done"); grouped with permission under "Needs you".
//   permission — blocked on a permission prompt, can't proceed. The loud state.
export type AgentStateName = "idle" | "working" | "done" | "waiting" | "permission";
export type NotificationLevel = "info" | "success" | "warn" | "error";

export type PaneTreeView =
  | {
      kind: "leaf";
      paneId: string;
      name: string;
      /* Full first-prompt text the label was cut from; shown in the pane
       * header hover tooltip. Empty when the pane wasn't auto-named. */
      nameFull?: string;
      url?: string | null;
      /* Color tag (0–5) into the pane palette in style.css. */
      colorIndex: number;
      /* Per-pane agent state — pane header surfaces this directly so
       * each pane's status is visible without going through the sidebar. */
      agentState: AgentStateName;
      /* Which agent runs in this pane: "claude", "codex", or "" (shell).
       * Drives the small agent badge in the pane header. */
      agentType?: string;
      /* Selected Claude model alias ("fable"/"opus"/"sonnet"/"haiku") or ""
       * for the account default. Drives the header's quiet model label and the
       * checkmark in the model menu. Optional so plain-shell / test leaves need
       * not carry it. */
      model?: string;
      activityDetail: string;
      branch: string;
      ports: number[];
      notification: { text: string; level: NotificationLevel } | null;
      /* Commits made since the agent session's baseline. 0 when no
       * baseline is set. */
      commitCount: number;
      /* Diff size since the agent baseline (committed + uncommitted) and the
       * count of commits not yet pushed to upstream. All 0 when no baseline
       * is set. Feed the "+A −D · ↑N" signal. */
      linesAdded: number;
      linesDeleted: number;
      filesChanged: number;
      ahead: number;
      /* Unix-ms the pane entered its current working spell (0 when not
       * working). The page ticks elapsed against Date.now(). */
      turnStartMs: number;
      /* Unix-ms the pane last finished a turn (entered "done"). 0 if it never
       * has. The page ticks relative-ago against Date.now() on done rows. */
      doneAtMs: number;
      /* Size weight inside the parent split (flex-grow). Defaults to 1. */
      weight?: number;
    }
  | {
      kind: "split";
      /* Stable id so pane.resizeSplit can address this split when a gutter
       * is dragged. */
      id: string;
      orientation: "h" | "v";
      children: PaneTreeView[];
      /* This split's own size weight inside ITS parent split. */
      weight?: number;
    };

export type SessionView = {
  id: string;
  title: string;
  shell: string;
  /* The project (registered repo) this tab is filed under; "" when unfiled.
   * Project mode groups on this and puts the unfiled ones under "Other". */
  projectId: string;
  /* Branch of this tab's git worktree; "" when it has no worktree. Drives the
   * "also delete its worktree folder" option on close. */
  worktreeBranch: string;
  rootPane: PaneTreeView;
  /* Session-level fields are aggregations of the panes' per-pane state.
   * agentState = most-urgent across panes
   * (permission > waiting > done > working > idle). */
  agentState: AgentStateName;
  activityDetail: string;
  branch: string;
  ports: number[];
  notification: { text: string; level: NotificationLevel } | null;
  /* Pane-count breakdown so the sidebar can render "3 panes · 1 waiting". */
  paneCount: number;
  waitingCount: number;
  workingCount: number;
  /* Git signal aggregated across the session's panes: total diff size and
   * the largest unpushed-commit count. Drive the idle row's "+A −D · ↑N". */
  linesAdded: number;
  linesDeleted: number;
  filesChanged: number;
  ahead: number;
  /* Unix-ms the earliest working pane started (0 when nothing's working) —
   * drives the live "working · 2m" elapsed in the sidebar/dashboard. */
  turnStartMs: number;
  /* Unix-ms the most-recently-finished pane entered "done" (0 when no pane is
   * at rest) — drives the live "finished · 2m ago" on done rows. Supersedes
   * lastActivity for the live case; the page ticks it against Date.now(). */
  doneAtMs: number;
  /* Relative "last activity" string ("now" / "5m ago"), host-computed at push
   * time. Kept as a fallback for rows without a doneAtMs. */
  lastActivity: string;
};

/* A row in the sidebar's "Recently closed" list. Summary only — the panes
 * themselves live host-side until restored. */
export type ClosedSessionView = {
  id: string;
  title: string;
  paneCount: number;
  /* How many of its panes carry a saved Claude session id (i.e. will resume
   * when restored). 0 → restores as plain shells. */
  resumableCount: number;
  /* Unix-ms it was closed; the page ticks "closed 5m ago" against Date.now(). */
  closedAtMs: number;
};

/* A registered repo. Sidebar project mode renders one header per project with
 * its tabs (sessions) nested beneath. */
export type ProjectView = {
  id: string;
  name: string;
  path: string;
};

export type StateMessage = {
  type: "state";
  activeSessionId: string;
  activePaneId: string;
  /* The user's home directory (%USERPROFILE%). Lets the page expand a "~\…"
   * path — Claude Code abbreviates the home dir in its file recaps — into a
   * real file:// URL for the HTML-file link menu. */
  homeDir: string;
  sessions: SessionView[];
  /* Registered projects, ferried with every push like prefs — the list is tiny
   * and the page then never has to ask for it. */
  projects: ProjectView[];
  /* Recently-closed sessions, most-recent first, for the restore list. */
  closedSessions: ClosedSessionView[];
  /* User preferences ferried with every state push — cheap and means the
   * page never has to ask. fontSize is the default terminal cell size
   * applied to new Panes; existing panes follow it too on every state.
   * onboardingSeen gates the first-launch welcome lightbox.
   * sidebarMode is which sidebar view is showing. */
  prefs: {
    fontSize: number;
    onboardingSeen?: boolean;
    sidebarMode?: SidebarMode;
    /* Whether the Inspector rail is showing. Open by default. */
    inspectorOpen?: boolean;
    /* Wide layout: both side rails widen and the terminal gives up the room.
     * Off (Compact) by default. */
    wideLayout?: boolean;
    /* Local panel "Perch only" filter: count/show only servers Perch started,
     * hiding the "other" (started-outside-Perch) bucket. Off by default. */
    localPerchOnly?: boolean;
  };
  /* Account-wide Claude model rate limits — only the AT-LIMIT models appear, so
   * the model menu disables exactly these and annotates each with its reset
   * time. Usually absent / empty (the usage endpoint 429s), which the menu
   * reads as "every model enabled, no annotations". resetsAtMs is Unix-ms (the
   * menu formats a local "resets 14:30") or null when the bucket had no reset. */
  modelLimits?: { alias: string; resetsAtMs: number | null }[];
};

export type SidebarMode = "sessions" | "projects";

/* Reply to projects.scan: repos worth registering. `source` says where each
 * came from — "inUse" (already open in a pane) reads very differently from
 * "scanned" (found under a configured root), and the dialog groups on it. */
export type ProjectsCandidatesMessage = {
  type: "projects.candidates";
  candidates: { path: string; name: string; source: "inUse" | "scanned" }[];
  scanRoots: string[];
};

export type ToastMessage = {
  type: "toast";
  text: string;
  level: NotificationLevel;
  /* Pane that fired the notify. The page anchors the toast to that pane's
   * bottom-center; absent / not-in-view falls back to window-centered. */
  paneId?: string;
};

/* One file in a commit's diff (the recap lightbox lists these). added/deleted
 * are line counts; both 0 for a binary file. */
export type CommitFileView = { path: string; added: number; deleted: number };

/* One unpushed commit in the "ready to push" recap. committedIso is an ISO-8601
 * timestamp the page turns into a relative "2h ago" label. inSession marks
 * commits made during the current agent session (baseline..HEAD) so the views
 * can divide "this session" from "earlier unpushed". */
export type CommitView = {
  sha: string;
  subject: string;
  committedIso: string;
  author: string;
  added: number;
  deleted: number;
  inSession: boolean;
  files: CommitFileView[];
};

/* Reply to commits.request: the unpushed commits for one pane, newest first.
 * ahead == commits.length (kept explicit so a caller can sanity-check against
 * the leaf's ahead count). Empty commits with ahead 0 means nothing to push
 * (or no upstream / cwd not yet known). */
export type CommitsDataMessage = {
  type: "commits.data";
  paneId: string;
  ahead: number;
  commits: CommitView[];
};

/* One row of the Inspector's stream. Six kinds, one ordered list:
 *   "prompt"    — what you asked (a real user turn; slash-command noise stripped)
 *   "beat"      — what the agent SAID (an assistant text block)
 *   "work"      — what the agent DID (a tool call)
 *   "interrupt" — a turn you STOPPED (Esc / Ctrl-C); painted red as an alarm
 *   "skill"     — the agent invoked a Skill; its own kind, coloured violet
 *   "image"     — an image in the conversation; verb is "pasted" (you put it
 *                 in a prompt) or "shared" (a tool handed it to the agent),
 *                 target is the id for inspector.image fetches. No pixels in
 *                 the event itself — the journal payload stays small.
 * The rail renders beats as the spine and work as dimmed connective tissue, so
 * one list drives both the narrative and the activity views.
 *
 * `repeat` > 1 means a RUN of identical consecutive calls was folded into this
 * row ("Read perch.log ×6"). That's the thrash signal — the cheapest way to
 * see an agent spinning without reading a word. */
export type InspectorEventView = {
  kind: "prompt" | "beat" | "work" | "interrupt" | "skill" | "image";
  ts: string;
  text: string;
  verb: string;
  target: string;
  note: string;
  repeat: number;
};

/* What the pane is costing. `contextTokens / contextMax` is the number that
 * matters on a Claude subscription (there's no bill — only headroom);
 * `costUsd` is the API-equivalent estimate, and is 0 for a model we have no
 * published rate for (we'd rather show nothing than a made-up figure). */
export type InspectorVitalsView = {
  model: string;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  costUsd: number;
  contextTokens: number;
  contextMax: number;
};

export type InspectorFileView = { path: string; added: number; deleted: number };

/* Reply to inspector.image — one image's bytes, base64. Empty `data` means the
 * image couldn't be served (transcript rotated/truncated since it was indexed);
 * the page renders an "unavailable" placeholder rather than a broken img. */
export type InspectorImageDataMessage = {
  type: "inspector.image.data";
  paneId: string;
  imageId: string;
  variant: string;
  mediaType: string;
  data: string;
};

/* Reply to inspector.request. hasAgent=false means the pane has no Claude
 * session (a plain shell, or an agent that hasn't started one yet) — the rail
 * shows its empty state rather than a misleading zeroed-out journal. `files`
 * can still be populated in that case: a shell pane in a repo has git changes
 * even with nothing to narrate. */
export type InspectorDataMessage = {
  type: "inspector.data";
  paneId: string;
  hasAgent: boolean;
  events: InspectorEventView[];
  vitals: InspectorVitalsView | null;
  files: InspectorFileView[];
  added: number;
  deleted: number;
};

/* Reply to settings.request. shells is the host's detected-shell list;
 * cmd is the command line to store as defaultShell. defaultCwdResolved is
 * what an empty defaultCwd falls back to (shown as the input placeholder
 * so the user sees where new sessions actually land). */
export type SettingsDataMessage = {
  type: "settings.data";
  shells: { name: string; cmd: string }[];
  defaultShell: string;
  defaultCwd: string;
  defaultCwdResolved: string;
  fontSize: number;
  /* Whether the launch prompt to reopen previous Claude sessions is enabled
   * (Settings → "Resume Claude sessions on launch"). */
  resumeAgentsOnLaunch?: boolean;
  /* Parent folders scanned (one level deep) for repos to offer as projects. */
  projectScanRoots?: string[];
  /* Where project tabs' worktrees are created ("" = the built-in default, which
   * `worktreeRootResolved` spells out for the placeholder). */
  worktreeRoot?: string;
  worktreeRootResolved?: string;
  /* Default list of things seeded into a new worktree (.env*, node_modules, …).
   * A project can override it — see `projects[].seedPaths`. */
  worktreeSeedPaths?: string[];
  /* Registered projects, so Settings can rename / re-seed / unregister them.
   * An empty seedPaths means the project inherits the global list. */
  projects?: { id: string; name: string; path: string; seedPaths: string[] }[];
  /* The running version (the release this copy installed from), or null when
   * it can't be determined (dev `dotnet run` / portable). Shown in the
   * Updates row. */
  appVersion?: string | null;
  /* Whether this copy can self-update (a real Velopack install). False on a
   * dev/portable copy, where "Check now" is disabled. */
  updatable?: boolean;
};

/* One pane being brought back in the restore-progress lightbox. */
export type RestorePaneView = { paneId: string; name: string; sessionTitle: string };

export type InMessage =
  | StateMessage
  | { type: "pane.out"; paneId: string; b64: string }
  | { type: "pane.exit"; paneId: string; code: number }
  /* Boot cover for a Claude Code pane: show the "Setting up…" overlay (tinted
   * to colorIndex) while cc starts, then hide once it's up. */
  | { type: "pane.setup"; paneId: string; show: boolean; colorIndex: number }
  | ToastMessage
  | SettingsDataMessage
  | CommitsDataMessage
  | InspectorDataMessage
  | InspectorImageDataMessage
  | ProjectsCandidatesMessage
  | { type: "host.error"; message: string }
  /* One-time launch prompt: N saved Claude sessions can be reopened. The page
   * asks the user, then replies with resume.decision. */
  | { type: "resume.prompt"; paneCount: number; sessionCount: number }
  /* Show the centered in-pane new-pane chooser. Sent when a freshly-split
   * terminal pane — whose source pane had a known working directory — first
   * measures; the host parks that pane's shell spawn until the user answers
   * with pane.chooser.choose. `cwd` is the source pane's dir (label + where
   * "same" / "agent" land), `defaultCwd` the fallback for "default", and
   * `agentType` ("claude" / "codex" / "") picks the agent button's label. */
  | { type: "pane.chooser"; paneId: string; cwd: string; agentType: string; defaultCwd: string }
  /* Open the restore-progress lightbox for these panes (each starts as a
   * spinner). Sent when a resume/restore actually begins. */
  | { type: "restore.begin"; panes: RestorePaneView[] }
  /* Flip one pane's row: "resuming" (active spinner) → "ready" (done check). */
  | { type: "restore.progress"; paneId: string; state: "resuming" | "ready" | "error" }
  /* All panes handled — the lightbox auto-dismisses (3s) or the user closes it. */
  | { type: "restore.done" }
  /* Host-pushed cached clipboard text. The host reads the OS clipboard on
   * change (while foreground), on window activation, and at page-ready, and
   * ferries it here so right-click paste is synchronous — no async
   * navigator.clipboard.readText() stall that the user re-clicks into a
   * double paste. Empty when the clipboard holds no text or exceeds the
   * host's size cap (the page falls back to readText for the oversize case). */
  | { type: "clipboard.text"; text: string }
  /* UI commands the WPF host can issue to the webview (e.g. a chrome
   * button in the title bar telling the webview to flip a class). */
  | { type: "ui.sidebar.toggle" }
  /* Triggered on main-window move/resize so URL panes re-emit their
   * placeholder rect and the host can reposition the child Windows. */
  | { type: "ui.urlpane.relayout" }
  /* Test-only: host asks the page to round-trip a marker through its main
   * thread so the host can time renderer responsiveness under load. */
  | { type: "render.ping"; id: number }
  /* Host asks the page to open the settings dialog (title-bar gear or the
   * test harness). The page already has the open path wired to the
   * sidebar gear; this just lets the host trigger it too. */
  | { type: "ui.open-settings" }
  /* A newer release is available (Velopack found it on the GitHub feed).
   * `version` is the target version string. The page reveals the footer
   * update pill; clicking it sends update.apply. */
  | { type: "update.available"; version: string }
  /* The download/apply triggered by update.apply failed. The page resets the
   * pill to a retry state and toasts the message. */
  | { type: "update.error"; message: string }
  /* Result of a manual `update.check`, routed to the Settings dialog. `uptodate`
   * → already on the latest (version = current); `available` → a newer release
   * exists (version = target; the pill is shown too); `error` → the check
   * failed; `unsupported` → this copy can't self-update (dev/portable). */
  | { type: "update.status"; state: "uptodate" | "available" | "error" | "unsupported"; version?: string | null }
  /* Every billable GCP resource this user's agents created and that is still
   * running. Filtered server-side on the agent-owner label, so the ~200 unrelated
   * production instances in the project never reach us. */
  | CloudDataMessage
  /* Every loopback server listening right now: dev servers you started, plus any
   * that outlived the pane that spawned them. Only appears while something is
   * actually listening. */
  | LocalDataMessage;

/** One machine (or one Dataproc cluster — a cluster is ONE row, not five). */
export interface CloudResourceView {
  /** Stable key. "cluster/<name>" or "<zone>/<name>". Pass back to cloud.delete. */
  id: string;
  name: string;
  kind: "instance" | "cluster";
  machineType: string;
  zone: string;
  /** Member VMs. >1 only for clusters. */
  vmCount: number;
  isGpu: boolean;
  createdMs: number;
  usdPerHour: number;
  /** False when the machine type isn't in the price table. Render "—", NOT
   * "$0.00": a confident zero next to a running A100 reads as "this is free". */
  priceKnown: boolean;
  /** From the ledger — the pane's name and the prompt that caused this machine.
   * Absent if the ledger entry was lost, in which case the row still shows the
   * machine and its cost, just not the reason for it. */
  agentName?: string | null;
  task?: string | null;
  paneId?: string | null;
  /** Nothing alive owns this machine: its pane was closed or its session ended.
   * This is the whole point of the panel. */
  isOrphan: boolean;
  /** Live panes only: "working" | "done" | "waiting" | … */
  agentState?: string | null;
  /** False → surfaced by the GPU radar (a running accelerator Perch didn't
   * create), not one of our agent's machines. Radar rows are view-only: shown
   * and costed so a stray GPU can't hide, but never killed from here. */
  startedByPerch: boolean;
}

export interface CloudDataMessage {
  type: "cloud.data";
  resources: CloudResourceView[];
  /** Host clock at poll time — cost is computed page-side from (now - createdMs),
   * so uptime keeps ticking between polls instead of freezing for 5 minutes. */
  nowMs: number;
}

/** One loopback server listening right now — a dev server you started, or one
 * that outlived the pane that spawned it. */
export interface LocalResourceView {
  /** Stable key "<port>/<pid>". Pass pid to local.kill, port to local.open. */
  id: string;
  port: number;
  pid: number;
  /** Listen address: "127.0.0.1" | "::1" | "0.0.0.0". */
  addr: string;
  /** Best-effort framework label ("Vite", "Next", "Flask", …), else the runtime
   * ("Node", "Python", …). Cosmetic — the port + command carry the real weight. */
  framework: string;
  /** Cleaned command tail, e.g. "npm run dev". Cosmetic. */
  command: string;
  /** Process start (host clock). Uptime is computed page-side from nowMs so it
   * keeps ticking between scans. */
  startedMs: number;
  /** live = a still-open pane owns it; lingering = its pane closed but the port
   * is still held (the whole point of the panel); other = listening but Perch
   * never launched it (started by hand elsewhere). */
  kind: "live" | "lingering" | "other";
  /** live: the owning pane's name. lingering: the pane that used to own it. */
  paneName?: string | null;
  paneId?: string | null;
  /** live panes only: "working" | "done" | "waiting" | … */
  agentState?: string | null;
  /** lingering only: when it lost its owning pane (host clock). */
  closedMs?: number | null;
}

export interface LocalDataMessage {
  type: "local.data";
  servers: LocalResourceView[];
  /** Host clock at scan time — uptime ticks page-side from (now - startedMs). */
  nowMs: number;
}

// ---- Implementation --------------------------------------------------------

type Listener = (msg: InMessage) => void;

const listeners: Listener[] = [];

// The WebView2 host bridge, or undefined when we're not running inside the
// host (plain-browser dev, or a Node test that transitively imports this
// module). Guarded with `typeof window` so importing bridge never throws
// outside a DOM.
const hostWebView =
  typeof window !== "undefined" ? window.chrome?.webview : undefined;

export function send(msg: OutMessage): void {
  const wire = JSON.stringify(msg);
  if (hostWebView) {
    hostWebView.postMessage(wire);
  } else {
    console.log("[bridge.no-host]", wire);
  }
}

export function onMessage(listener: Listener): void {
  listeners.push(listener);
}

function dispatch(raw: unknown) {
  let msg: InMessage;
  if (typeof raw === "string") {
    try { msg = JSON.parse(raw) as InMessage; }
    catch { console.warn("[bridge] non-JSON string from host:", raw); return; }
  } else if (raw && typeof raw === "object") {
    msg = raw as InMessage;
  } else {
    return;
  }
  for (const l of listeners) {
    try { l(msg); }
    catch (err) { console.error("[bridge] listener threw:", err); }
  }
}

if (hostWebView) {
  hostWebView.addEventListener("message", (e: MessageEvent) => {
    dispatch(e.data);
  });
} else if (typeof window !== "undefined") {
  console.warn("[bridge] no chrome.webview -- running in plain browser");
}

// ---- Base64 helpers --------------------------------------------------------
// Round-trip bytes byte-for-byte. We avoid TextEncoder/TextDecoder for
// PTY output because the shell can emit partial multi-byte sequences
// (an emoji split across reads); xterm.js's VT parser handles reassembly.

export function b64ToBytes(b64: string): Uint8Array {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

export function bytesToB64(buf: Uint8Array): string {
  const CHUNK = 0x8000;
  let s = "";
  for (let i = 0; i < buf.length; i += CHUNK) {
    s += String.fromCharCode(...buf.subarray(i, i + CHUNK));
  }
  return btoa(s);
}
