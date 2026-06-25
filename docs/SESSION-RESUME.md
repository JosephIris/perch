# Claude Code session resume (agent-layer crash/restart restore)

> Status: **TODO — spec complete, not yet implemented.** Scoped 2026-05-29.
> Upstream behaviour confirmed against a live fetch of
> `github.com/manaflow-ai/cmux` (not memory). Tracked in `docs/PARITY.md`.

## Goal

After a restart **or crash**, a pane that was running a Claude Code session
reopens straight into that conversation via `claude --resume <id>`, so the
agent's context continues instead of dropping the user at a blank shell.

This is the agent half of upstream perch's "Reopen Previous Session". Upstream
restores layout + cwd + scrollback (best-effort) + browser state, and—for
supported agents—resumes the agent session "when hooks have saved a native
session ID." We already restore the **layout half**; this adds the **agent
half** for Claude Code.

## What already works (don't rebuild)

- **Layout/cwd/names/colours/URL panes already survive crashes.**
  `SessionStore.Save()` fires on every *structural* change (split, close,
  rename, recolour, cwd auto-title, new session), not just on graceful exit,
  and `SessionStore.Load()` restores on startup. A hard crash loses at most
  the last unsaved structural change.
- **The wrapper + hook pipeline.** `claude` in a pane resolves to
  `tools/claude.cmd` → `perch.exe wrap-claude` (`ClaudeWrapper.cs`), which
  injects `--settings` hooks and execs the real `claude`. Hooks call back
  `perch hooks claude <event>` → `HookHandler.cs` → per-pane pipe → host.
  `SessionStart` already fires here today (we use it for the git baseline).

## What's missing

1. We never capture Claude Code's **session id**.
2. `PaneNode` has nowhere to **persist** it.
3. Respawned panes start a **bare shell**, never `claude --resume <id>`.

## Design

### 1. Capture the session id (CLI → host)

Claude Code's hook payloads carry `session_id` (alongside `cwd`,
`transcript_path`, and—on `SessionStart`—`source` ∈ startup/resume/clear/
compact). In `HookHandler` `session-start`, pull `session_id` with the same
root/`hook_input`/`data` fallback `StringFrom` already uses, and emit a new
IPC message:

```json
{ "type": "session", "id": "<uuid>" }
```

`PerchIpc.cs`: add `SessionMessage(Id)` + a `"session"` dispatch case + an
`OnSession` event — mirror the existing `git.baseline` plumbing exactly.

`MainWindow.OnAgentSession(sess, paneId, msg)`: set
`pane.ClaudeSessionId = msg.Id` and `_store.Save()`.
**Overwrite** on each `session-start`; do **not** clear on `session-end`
(`claude --resume` works on an ended session — we want the last id to stick).

### 2. Persist the id (model)

`Session.cs` / `PaneNode`: add

```csharp
/// Last Claude Code session id seen on this pane (from the SessionStart
/// hook). PERSISTED (unlike the transient agent state) so a relaunch can
/// `claude --resume <id>`. Just a UUID; harmless if stale.
public string? ClaudeSessionId { get; set; }
```

Note this is one of the few **persisted** pane fields — all the
`AgentState`/`Branch`/etc. fields are `[JsonIgnore]`; this one is not.

### 3. Resume on respawn (host → shell)

- Add `Settings.ResumeAgentsOnLaunch` (bool, default **true**) — mirrors
  upstream's toggle.
- `SpawnPty`: when building the startup command, if
  `pane.ClaudeSessionId is not null` **and** the setting is on **and** this is
  the **first** spawn of this pane since load (guard with a transient
  `HashSet<Guid> _resumeAttempted` so a later manual re-split never
  auto-launches claude), pass an `initialCommand` of
  `claude --resume <id>` into `Shell.BuildStartupCommandLine`.
- `Shell.BuildStartupCommandLine`: add an optional `string? initialCommand`.
  - **pwsh:** append `; claude --resume '<id>'` after the env/cwd setup
    inside the `-Command`. `-NoExit` is already passed, so when claude exits
    the user keeps the interactive shell.
  - **cmd:** append as another `&& claude --resume <id>` part.
  - **wsl:** later.

`claude` here resolves to the shim → wrapper, so the resumed run **re-injects
hooks** and re-emits `SessionStart` with the **same** id — re-capture is
idempotent.

### 4. Failure / edges

- **Stale id** (Claude pruned the session) → `claude --resume` errors; with
  `-NoExit` the user lands in a normal shell with one error line. Matches
  upstream's "reopens as a normal terminal" fallback. Optional polish: detect
  the failure and clear `ClaudeSessionId`.
- **Pane never ran claude** → id null → normal shell, no change.
- **Manual new pane / split** → no saved id, and the `_resumeAttempted` guard
  prevents a respawn within the same run from auto-launching. No surprises.
- **URL panes** → N/A.
- Each pane carries its own id — independent resume.

### 5. Settings UI (optional for v1)

Add a toggle to the settings dialog: *"Resume Claude Code sessions on
launch"* (default on), persisted via the existing `prefs.set` path. Can ship
with the setting defaulting true and wire the UI in a follow-up.

## Touch points

| File | Change |
|---|---|
| `tools/perch-cli/HookHandler.cs` | extract `session_id` on `session-start`, send `{type:"session",id}` |
| `src/Perch/PerchIpc.cs` | `SessionMessage` record + dispatch + `OnSession` |
| `src/Perch/Session.cs` | `PaneNode.ClaudeSessionId` (persisted) |
| `src/Perch/MainWindow.xaml.cs` | `OnAgentSession` handler; wire `OnSession`; `SpawnPty` resume injection + `_resumeAttempted` guard |
| `src/Perch/Shell.cs` | optional `initialCommand` param + pwsh/cmd injection |
| `src/Perch/Settings.cs` (+ dialog) | `ResumeAgentsOnLaunch` toggle |

## Out of scope (separate parity items)

- Terminal **scrollback** persistence (upstream does this best-effort).
- Resuming **non-Claude** agents (Codex/OpenCode) — same shape, different
  hook field + resume flag; follow-up once the Claude path proves out.
- A manual **"reopen previous session"** command / `restore-session` CLI.

## Verify before building

- Confirm the installed Claude Code's flag is `claude --resume <id>` (vs
  `--continue` for "most recent"), and that the `SessionStart` hook payload
  field is `session_id`, against `claude --help` / a captured hook payload on
  the target machine. Flags drift between Claude Code versions.
- Decide **auto-run vs pre-fill**: auto-running `claude --resume` (reopen
  straight into the agent) matches upstream; pre-filling the command for the
  user to press Enter is safer. Recommend auto-run gated by the setting.
