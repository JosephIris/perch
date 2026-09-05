# Testing the macOS build

Two harnesses drive the real mac host (`src/Perch.Mac`) through the control
pipe (`PERCH_ENABLE_TEST_IPC`). Both own their app lifecycle, use an isolated
`PERCH_DATA_DIR`, and kill only the PID they launched — the user's own
Perch.app can stay open (one instance per data dir, not per machine).

| Script | What it is | Cost |
|---|---|---|
| `scripts/mac-e2e.mjs` | **The release gate.** Real Claude Code, the Dock's bare PATH, the codex shim, the team room. | a few cents of haiku, ~8 min (`--quick`: none, ~2 min) |
| `scripts/mac-usability.mjs` | Feature suite: splits, boards, URL panes, settings, persistence, screenshots. | free, ~3 min |
| `scripts/ctl.mjs` | One raw JSON verb to the control pipe. | — |

Build first: `dotnet build src/Perch.Mac/Perch.Mac.csproj` (bundles the web
page and publishes the `perch` CLI + shims into `bin/Debug/net8.0/tools`).

## The end-to-end gate

```sh
node scripts/mac-e2e.mjs                 # everything; must be green before a v* tag
node scripts/mac-e2e.mjs --quick         # a–d only: no tokens, after a host change
node scripts/mac-e2e.mjs --sections e1,e2
node scripts/mac-e2e.mjs --app /Applications/Perch.app/Contents/MacOS/Perch
```

The app is launched with **launchd's PATH** (`/usr/bin:/bin:/usr/sbin:/sbin:
/usr/local/bin`), exactly as a Dock click starts it, because that is the
condition under which "starting a Claude in a session" broke: the pane's
initial command runs under `sh -c` before any rc file, the bundled shim could
not find the real `claude`, and the tab fell into a bare shell — while a
`claude` typed by hand kept working. `MacShellEnv` now adopts the login
shell's PATH at startup; section a asserts it did and section c proves the
consequence. `--inherit-path` opts out.

| # | Section | Proves |
|---|---------|--------|
| a | boot | pane spawned; `ShellEnv` adopted a PATH that contains the real `claude`; zero ERRORs |
| b | typed | `claude` typed into a pane → trust prompt answered by the suite → `type=session` hook, `agentType=claude`, a Claude session id |
| c | tab | `project.tab.new agent=claude` starts Claude **by itself** and reports its session (the Dock-PATH bug) |
| d | codex | a codex tab runs the shim against a fake `codex`: `type=agent` codex, `agentType=codex`, `perch.config.toml` in `CODEX_HOME` with unquoted hook paths to the bundled `perch` |
| e1 | room, warm | a post is typed into a running bot, the prompt-submit hook confirms it, the bot's one-line answer lands in the room |
| e2 | room, cold | after a restart (launch prompt unanswered → no terminal), a post starts the bot, nothing is typed until its Claude is up, the post lands and is marked delivered |
| e3 | send again | `team.deliver.retry` re-types the line with no second post |
| e4 | late Allow | a permission card answered 15 s later still runs the command |
| e5 | bot to bot | Ada's SendMessage reaches Bo idle and mid-turn; Bo acts on both |
| log | — | zero ERROR lines across the run |

Sections e1–e5 are the Windows gate (`scripts/verify-comms.ps1`) section for
section; the log tags they wait on (`Team.deliver`, `Team.submit`,
`Team.start.needed`, `Team.perm.ask`, `TEAM_DUMP`) come from Perch.Core and
are identical on both hosts.

### How the suite sees the screen

The host keeps no terminal buffer (xterm owns the screen), so under test IPC
`PaneManager` records each pane's last 16 KB of raw output and `pty.tail`
logs it (`PTY_TAIL pane=<id> <base64>`). The suite strips escape sequences
and whitespace — a TUI positions every word with a cursor move — and matches
words: Claude's "Quick safety check … ❯ No, exit / Yes, I trust this folder"
is answered with Down+Enter (`pty.send {paneId, text}`), the same keys the
room sends for a bot. Every hook payload is also appended to
`<data>/perch/hook-dump.jsonl` (`PERCH_HOOK_DUMP`), which is what to read
when a session never reports.

### What it has caught

- **Dock PATH** (above): every app-started Claude failed on a packaged install.
- **Pipe bursts lost messages.** .NET backs a named pipe with a Unix socket
  and unlinks it when the last server instance is disposed; the accept loop
  disposed one per client, so a hook's five back-to-back messages lost two
  or three — `session` among them often enough that a tab's Claude never
  showed its id, and `status` so often that agent state was a coin toss.
  Fixed with an anchor instance in `PerchIpcServer` / `ControlIpcServer`
  plus a three-try `Send` in perch-cli; `PerchIpcBurstTests` guards it.
- Earlier, from the usability suite: `OnSettingsRequest` NRE with a null
  updater, Photino's `UseOsDefaultSize` ignoring `SetSize`, the first-run
  onboarding masking every capture.

## The usability suite

```sh
node scripts/mac-usability.mjs --out /tmp/perch-usability
```

Seeds `OnboardingSeen`, launches fresh, SIGTERM-restarts to prove
persistence, screenshots each step by CGWindowID (`scripts/mac-window-id.swift
<pid>` + `screencapture -l`, never foregrounding the app). Exit code = failed
tests; `results.json` + PNGs land in `--out`.

| # | Feature | Assertion |
|---|---------|-----------|
| 01 | Boot | session present, zero ERROR lines |
| 02 | Terminal I/O | pty byte counter grows after `pty.send` |
| 03–04 | Session new / rename | session count, survival |
| 05–07 | Split right / down, close | pane counts in `state.dump` |
| 08 | Font-size pref | `prefs.fontSize` round-trip |
| 09 | Board pane + note | pane appears; note persisted (`board.md`) + visible |
| 09b | URL pane | WKWebView subview created, title/URL in state |
| 09c | Splitter drag | synthetic pointer moves the divider (weight changes) |
| 09d | Right-click paste | clipboard text reaches the pty |
| 10–11 | Agent IPC (`perch status` / `notify`) | `agentState`, `notification` |
| 12 | Renderer responsiveness | `render.ping` → pong logged |
| 13 | Project registration | no errors; sidebar row (screenshot) |
| 14 | Session close / restore | `closedSessions` round-trip |
| 15 | Settings dialog | opens with detected shells (screenshot) |
| 16 | Persistence | same sessions/pane layout after SIGTERM + relaunch |
| 17 | Clean log | zero ERROR lines across the whole run |

## Not covered (manual for now)

- Real mouse/keyboard beyond the synthetic pointer rig: pane-header drag
  reorder, copy-on-select, ⌘-chords from the room's composer.
- Auto-update end to end (Velopack feed), the dashboard overlay.
- A real `codex` (the gate uses a fake so it needs no install; run a codex
  pane by hand once per release if codex is installed).
- The Windows build — `scripts/*.ps1` remain the Windows harnesses.
