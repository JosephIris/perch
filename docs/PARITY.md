# perch vs upstream perch — parity tracker

> **Source caveat:** the upstream column in the table below was drafted from
> prior knowledge of `github.com/manaflow-ai/cmux`, not a fresh fetch of the
> live repo (the research agent's network access was sandboxed at the time).
> Verify each upstream feature against the live tree before scheduling work
> off this doc.

Upstream perch is positioned as a **parallel coding-agent workspace** — its
centre of gravity is launching N AI agents against branches/worktrees of
the same repo and comparing results. The terminal grid is a delivery
vehicle for that, not the product. Our port has the terminal vehicle but
is missing most of the agent-orchestration layer on top.

## Feature inventory

| Category | Upstream perch | perch | Status |
|---|---|---|---|
| Workspace model | Repo-rooted; each run is a git worktree on its own branch | Flat session list, no repo/worktree concept | ❌ |
| Multi-agent parallel runs | Same prompt across N agents/models, diff side-by-side | None | ❌ |
| Agent as first-class concept | Agent presets (Claude Code, Codex, Aider, …) | `Session.Shell` string + ClaudeWrapper/HookHandler | 🟡 |
| Run review UI | Diff viewer, file tree, accept/discard per run | None | ❌ |
| Session/pane terminal | xterm.js + splits | xterm.js + recursive splits + lazy spawn + JobObject cleanup | ✅ |
| Crash/session restore | Saves layout + cwd + scrollback (best-effort) + browser state on quit; resumes agent sessions via saved native session id; `Reopen Previous Session` / `perch restore-session` | Layout + cwd + names + colours + URL panes persist & restore (incremental save → survives crashes). No scrollback, no agent resume yet. Agent resume spec'd in `docs/SESSION-RESUME.md` | 🟡 |
| Per-session named pipe / IPC | Unix socket + `perch` CLI verbs | Identical verb set over `\\.\pipe\perch\<paneId>` | ✅ server / ❌ CLI |
| `perch` CLI binary | Ships a CLI agents invoke inside the pane | None — agents have to write JSON to the pipe themselves | ❌ |
| Claude Code hooks | Wraps Claude, intercepts hook events → status/notify | `ClaudeWrapper.cs`, `HookHandler.cs` | 🟡 |
| Git branch awareness | Auto-detects branch per workspace | Branch chip exists, but pushed by agent via `perch meta` — no auto-detect | 🟡 |
| Worktree management | Creates/destroys git worktrees per run | None | ❌ |
| Port detection | Auto-scans listening ports of child processes | Ports chip exists, but agent must push via `meta`; no auto-detect | 🟡 |
| Notifications | Native + in-app, severity levels | OSC 9 + `perch:<level>` extension, sidebar pill, toast | ✅ |
| OSC 7 / cwd tracking | Standard | OSC 7 + Windows Terminal OSC 9;9 | ✅ |
| Settings / config | Repo-rooted `.perch.json` per project + global | Global `settings.json` only | 🟡 |
| Keyboard model | Splits/close + command palette | Ctrl+Shift+T/D/S/W only | 🟡 |
| Command palette | Ctrl+Shift+P, fuzzy command list | None | ❌ |
| Theming | Light/dark/system | Mica + dark only, no toggle | 🟡 |
| Multi-window | Multiple workspace windows | Single MainWindow / WebView2 | ❌ |
| In-pane search | xterm SearchAddon UI | SearchAddon loaded, no UI wired | 🟡 |
| Webview panes (browser-in-pane) | For previewing localhost ports | `PaneNode.Url` field supports it; not exposed in chrome | 🟡 |
| Test/automation IPC | n/a | `ControlIpcServer` env-gated pipe | ✅ (we're ahead here) |

## Top 5 highest-leverage gaps

1. **Worktree-per-run + Run entity above Session.** Upstream's whole reason
   for existing. Without it we're "Windows Terminal with sidebars."
2. **`perch` CLI binary.** The IPC server already exists and matches
   upstream verbs — but every "agent integration" pattern starts with
   `perch notify` / `perch status`. A ~150-LOC .NET 8 AOT exe unlocks the
   ecosystem cheaply.
3. **Multi-agent parallel-run grid + diff/review UI.** Headline
   differentiator. Builds on (1).
4. **Auto-detect git branch and listening ports per pane.** Today the
   chips only light up if an agent manually calls `perch meta`. A polling
   Job-Object-scoped TCP scan + `git rev-parse` closes this for free.
5. **Command palette + in-pane search UI.** Table stakes for 2026; the
   xterm search addon is already loaded.

## Tracked TODOs

- [ ] **Claude Code session resume** (agent-layer crash/restart restore).
  Spec complete — see [`docs/SESSION-RESUME.md`](SESSION-RESUME.md). Capture
  `session_id` from the `SessionStart` hook → persist on `PaneNode` → respawn
  with `claude --resume <id>` behind a `ResumeAgentsOnLaunch` setting. ~6 small
  touch points; verify the `--resume` flag / `session_id` field against the
  installed Claude Code before building.

## Suggested Windows implementation notes

- Worktrees → `libgit2sharp` or shell out to `git.exe`; one worktree per
  run under `%LOCALAPPDATA%\perch\worktrees\<run-id>`.
- `perch` CLI → tiny .NET 8 native-AOT exe reading `PERCH_PIPE` /
  `PERCH_PANE_ID` (already injected by `Shell.BuildStartupCommandLine`).
- Port auto-detect → cheap timer per pane, `GetExtendedTcpTable` filtered
  to PIDs in the pane's Job Object.
- Branch auto-detect → `git -C <cwd> rev-parse --abbrev-ref HEAD` every
  ~5s, gated on cwd changes.
- Multi-window → second `MainWindow` per shortcut; `SessionStore` is
  already file-backed but needs a window-scope key.
