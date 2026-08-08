# macOS usability suite

`scripts/mac-usability.mjs` drives the mac build end-to-end through the
control pipe (`PERCH_ENABLE_TEST_IPC`) — every step is a user-visible
feature, asserted against `state.dump` and screenshotted (windowID capture,
no focus stealing) so a human can review what each feature actually looked
like.

Run:

```sh
dotnet build src/Perch.Mac/Perch.Mac.csproj
node scripts/mac-usability.mjs --out /tmp/perch-usability
```

The suite owns the app lifecycle (kills strays, launches fresh with an
isolated `PERCH_DATA_DIR`, seeds `OnboardingSeen` so the welcome dialog
doesn't cover the screenshots, SIGTERM-restarts to prove persistence).
Exit code = failed tests. Screenshots + `results.json` land in `--out`.

## Coverage

| # | Feature | Assertion |
|---|---------|-----------|
| 01 | Boot | session present, zero ERROR lines |
| 02 | Terminal I/O | pty byte counter grows after `pty.send` |
| 03–04 | Session new / rename | session count, survival |
| 05–07 | Split right / down, close | pane counts in `state.dump` |
| 08 | Font-size pref | `prefs.fontSize` round-trip |
| 09 | Board pane + note | pane appears; note persisted (`board.md`) + visible |
| 10–11 | Agent IPC (`perch status` / `notify`) | `agentState`, `notification` |
| 12 | Renderer responsiveness | `render.ping` → pong logged |
| 13 | Project registration | no errors; sidebar row (screenshot) |
| 14 | Session close / restore | `closedSessions` round-trip |
| 15 | Settings dialog | opens with detected shells (screenshot) |
| 16 | Persistence | same sessions/pane layout after SIGTERM + relaunch |
| 17 | Clean log | zero ERROR lines across the whole run |

Bugs this suite has caught so far: `OnSettingsRequest` NRE with a null
updater (mac has no Velopack), Photino's `UseOsDefaultSize` silently
ignoring `SetSize` (window opened 800×572), and the first-run onboarding
masking every capture.

## Not covered (manual for now)

- Real mouse/keyboard interaction (clicks, drags, Ctrl-chords) — the suite
  drives IPC, not the pointer. Splitter drags, pane-header drag-reorder,
  copy-on-select, right-click paste need a hand.
- URL panes (stubbed on mac), auto-update (no updater), clipboard **image**
  paste (text only), the dashboard overlay (page-side keyboard shortcut,
  no control verb yet).
- The Windows build — this suite is mac-only; `scripts/*.ps1` remain the
  Windows harnesses.
