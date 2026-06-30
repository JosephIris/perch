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
  | { type: "session.new"; shell?: string }
  | { type: "session.select"; id: string }
  | { type: "session.rename"; id: string; title: string }
  | { type: "session.close"; id: string }
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
  /* Pane cwd update from xterm's OSC 7 handler. Host uses it to auto-fill
   * the branch chip via `git rev-parse`. */
  | { type: "pane.cwd"; paneId: string; cwd: string }
  /* URL pane layout — page reports a rect for the placeholder; host
   * sizes a real WebView2 control to match. First layout creates the
   * WebView2; subsequent layouts reposition/resize. */
  | { type: "urlpane.layout"; paneId: string; url: string; x: number; y: number; w: number; h: number }
  | { type: "urlpane.dispose"; paneId: string }
  /* User preferences (terminal font size, etc.) — host persists to
   * Settings.cs so it survives restart. */
  | { type: "prefs.set"; fontSize?: number }
  /* Recap: page asks the host for the unpushed-commit list behind a pane's
   * "↑N" chip (the hover tooltip / lightbox open lazily fetch it). Host
   * replies with a commits.data message for the same paneId. */
  | { type: "commits.request"; paneId: string }
  /* Settings dialog: page asks the host for current settings + the list
   * of detected shells, host replies with a settings.data message. */
  | { type: "settings.request" }
  /* Settings dialog save. Each field is optional so the page can send a
   * partial update; the host only overwrites provided keys. defaultShell
   * is the shell COMMAND LINE (matching one of settings.data.shells[].cmd)
   * or "" for auto-detect. */
  | { type: "settings.save"; defaultShell?: string; defaultCwd?: string; fontSize?: number; resumeAgentsOnLaunch?: boolean }
  /* Page dismissed the onboarding lightbox → host marks it seen so it won't
   * auto-open next launch. */
  | { type: "onboarding.seen" }
  /* User clicked the footer update pill. Host downloads the pending Velopack
   * update and relaunches into it (the process is replaced on success). */
  | { type: "update.apply" }
  /* Settings → "Check now": ask the host to check the feed right now. Unlike
   * the silent background checks, the host replies with an update.status so the
   * dialog can show the outcome (and still reveals the pill if one is found). */
  | { type: "update.check" };

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

export type StateMessage = {
  type: "state";
  activeSessionId: string;
  activePaneId: string;
  sessions: SessionView[];
  /* Recently-closed sessions, most-recent first, for the restore list. */
  closedSessions: ClosedSessionView[];
  /* User preferences ferried with every state push — cheap and means the
   * page never has to ask. fontSize is the default terminal cell size
   * applied to new Panes; existing panes follow it too on every state.
   * onboardingSeen gates the first-launch welcome lightbox. */
  prefs: { fontSize: number; onboardingSeen?: boolean };
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
  | ToastMessage
  | SettingsDataMessage
  | CommitsDataMessage
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
  | { type: "update.status"; state: "uptodate" | "available" | "error" | "unsupported"; version?: string | null };

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
