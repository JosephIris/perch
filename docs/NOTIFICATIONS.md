# Push notifications from inside a pane (OSC 9)

perch listens for **OSC 9** escape sequences in any pane's output stream
and surfaces the message on that session's sidebar row with a colored dot.
Same protocol iTerm2, Ghostty, and Windows Terminal recognise — anything
already wired for those terminals' notifications works here unchanged.

## Wire format

A complete sequence is three parts: introducer, payload, terminator.

```
ESC ] 9 ; <payload> BEL
```

- Introducer: `\x1b]9;` (ESC `]` `9` `;`)
- Terminator: `\x07` (BEL) — or the long form `\x1b\\` (ESC `\`, "ST")
- Payload: UTF-8 text. ≤ 4 KB; longer sequences are dropped.

perch also accepts a **levelled extension** so the dot reflects severity:

```
ESC ] 9 ; perch:<level> ; <text> BEL
```

where `<level>` is one of `info`, `success`, `warn`, `error`. Anything else
falls back to `info`. Other terminals will see the literal `perch:warn;…`
text as the notification body — harmless, just slightly noisy.

## Shell snippets

### PowerShell

```powershell
# Plain
Write-Host "`e]9;Build OK`a" -NoNewline

# Levelled
function Notify {
    param([string]$Text, [ValidateSet('info','success','warn','error')][string]$Level = 'info')
    Write-Host "`e]9;perch:$Level;$Text`a" -NoNewline
}
Notify -Text 'Tests failing' -Level error
```

### cmd

cmd doesn't expand `\e` natively. Use PowerShell or a helper batch file:

```bat
@echo off
powershell -NoLogo -NoProfile -Command "Write-Host \"`e]9;%~1`a\" -NoNewline"
```

Save as `notify.cmd`, then `notify.cmd "Build OK"`.

### bash / zsh / fish (inside WSL or git-bash)

```bash
notify() {
    local level="${2:-info}"
    printf '\e]9;perch:%s;%s\a' "$level" "$1"
}
notify "Tests passing" success
```

## Behavior

- The most recent OSC 9 from a session wins. The dot updates immediately.
- Notification persists until the next `OSC 9` in that session, or the pane
  closes. There's no auto-clear timeout in this version.
- An empty payload (`\e]9;\a`) clears the notification.
- Notifications fire from ANY pane in a session; the sidebar row reflects
  the *session*, not the specific pane.
- The parser is streaming — agents can emit the sequence across multiple
  flushes / writes without losing it.

## Agent integration patterns

The intended use is for AI coding agents (Claude Code, Codex, Aider, etc.)
to surface what they're doing without polling. Wire it into your hooks:

- After a tool run completes: `notify "edits applied" success`
- When tests are failing: `notify "3 tests red" error`
- When waiting on user input: `notify "needs review" warn`
- On long-running command start: `notify "building..." info`

Because OSC 9 is just bytes on stdout, this works from inside any subprocess
the agent spawns — no IPC, no socket, no env var setup.

## Comparison to perch for macOS

perch (mac) exposes a `perch notify` CLI that writes to a workspace socket.
That's richer (it can also set git branch / cwd / ports per workspace) but
requires the CLI and a socket protocol.

perch uses OSC 9 instead because:
- It's already a standard recognised across the terminal ecosystem.
- It needs no IPC infrastructure — works through any subprocess pipeline.
- It composes with `tee`, `ssh`, `tmux`, etc. — anywhere bytes flow.

The trade-off: OSC 9 only carries text + (in our extension) a severity.
Workspace metadata like git branch and listening ports would need a
separate mechanism. If that becomes load-bearing, the cleanest path is a
local named pipe (`\\.\pipe\perch\<session-id>`) plus a tiny `perch` CLI —
documented but not built yet.

## Working directory reporting (OSC 7 / OSC 9;9)

perch also tracks the **current working directory** per session so the
next time you open the session, it starts where you left off. Two
escape-sequence dialects are accepted:

```
ESC ] 7 ; file:///C:/Users/josep BEL          # universal (Ghostty, iTerm, etc.)
ESC ] 9 ; 9 ; C:\Users\josep    BEL          # Windows Terminal shell-integration
```

When either fires, the session's `Cwd` is updated and persisted to
`sessions.json`. New panes / next launches open in that directory; no panel
work required.

### Default cwd

If a session has no recorded `Cwd` (fresh install, never visited that
session), it falls back to `Settings.DefaultCwd` (default: `%USERPROFILE%`).
This is the fix that stops fresh sessions launching inside the install
folder.

### Making your shell report cwd

cmd and the default pwsh prompt **don't emit OSC 7 / OSC 9;9 by themselves.**
You opt in once per shell.

**PowerShell (works for both pwsh 7 and Windows PowerShell 5.1):** add this
to your `$PROFILE`:

```powershell
function Global:prompt {
    $loc = (Get-Location).ProviderPath
    Write-Host -NoNewline "`e]7;file:///$($loc -replace '\\','/')`a"
    "PS $loc> "
}
```

**bash / zsh (WSL, git-bash, etc.):**

```bash
# OSC 7 — universal
update_cwd_osc() {
    printf '\e]7;file://%s%s\a' "$HOSTNAME" "$PWD"
}
PROMPT_COMMAND="update_cwd_osc${PROMPT_COMMAND:+;$PROMPT_COMMAND}"
```

**Windows Terminal shell integration** — if you've already enabled WT
shell integration (it emits OSC 9;9), it works automatically; perch
recognises the WT dialect.

## Limitations

- The parser is `EasyWindowsTerminalControl.TermPTY.TerminalOutput`-fed.
  Conpty / Microsoft.Terminal.Wpf is expected to pass OSC 9 through
  untouched (tested with the Campbell Win11 build). If a future version of
  the renderer starts swallowing OSC 9 we'd need to feed our parser from
  upstream of conpty, which would be a more invasive change.
- The notification is per-session, not per-pane. With many panes in one
  session, the row shows the most recent message from any of them.
- No history. To inspect older notifications, scroll the pane.
