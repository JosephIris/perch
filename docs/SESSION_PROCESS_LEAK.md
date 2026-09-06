# Session process leak — measurements from the 2026-09-06 incident

Seven Perch agent sessions spawned on 2026-09-04 were still holding memory
56 hours later, on a Perch instance that had never been restarted. This file
records **what was measured**, not what the fix should be. Decide the policy
from the numbers below.

---

## 1. The headline numbers

| Metric | Value |
|---|---|
| Machine RAM | 31.5 GB |
| Free RAM before cleanup | 1.0 GB (97% used) |
| Free RAM after cleanup | 5.2 GB |
| Processes killed | 35 |
| Memory reclaimed | 5,732 MB (5.6 GB) |
| Leaked Perch sessions | 7 |
| Age at kill | 56.5 hours |
| Cost per leaked session | ~700–820 MB |

Perch's share of total RAM at the moment of discovery was ~18%, second only to
Firefox (5.9 GB across 23 processes) — and unlike Firefox, none of it was in use.

## 2. What one leaked session costs

Each leaked session held **four** processes, not one:

| Process | Role | RSS |
|---|---|---|
| `pwsh.exe` | the pane shell (`-NoExit -Command $env:PERCH_PIPE=...`) | 71–73 MB |
| `claude.exe` | Claude Code itself | 328–459 MB |
| `bun.exe` (`server.ts`) | Telegram MCP plugin server | 261–346 MB |
| `bun.exe` (`bun run --cwd ...telegram/0.0.7`) | plugin launcher | 18–40 MB |

The MCP plugin server is the surprise: it is **as expensive as Claude itself**,
and there is one per session. Any reap that kills `claude.exe` but not the job's
other members recovers barely half the memory.

## 3. Timeline

| Time | Event |
|---|---|
| 2026-09-04 00:00:02 | `Perch.exe` (PID 51452) starts. Still running at time of writing. |
| 2026-09-04 00:00:40 – 00:06:50 | 7 `perch.exe wrap-claude` sessions spawn (`--resume`, one `--session-id`). |
| 2026-09-04 → 2026-09-06 | No further activity from any of them. Nothing in the log. |
| 2026-09-06 08:38:34 | Killed externally. `ConPty.process-exited pid=… code=-1` × 7 appears in `errors.log` — the **first** mention in 56 hours. |

The `ConPty` watcher threads were alive the entire time. Perch had a live handle
on every one of these processes and never acted on it.

Leaked session ids (from the `--resume` argument of each `perch.exe wrap-claude`):

```
415d7f45-…  d41212d2-…  a28ce585-…  e40b41a4-…
2b93510a-…  efd556f2-…  7ad5e597-…
```

## 4. State-file evidence

`%APPDATA%\Perch\sessions.json` at time of incident:

- `Sessions`: 24 · `ClosedSessions`: 10 · `ActiveSessionId`: `None`
- **5 of the 7 leaked session ids were absent from the file entirely.**
  Only `415d7f45` and `7ad5e597` still had records.

So for five of them the session record was dropped while the process tree lived
on. Perch had already forgotten these sessions existed; nothing in the app was
ever going to reap them. That is the leak in one sentence.

## 5. Why the existing safety nets did not catch it

All three are working as written. None covers this case.

- **`PaneJob` (PaneJob.cs)** — deliberately sets *no* limit flags, explicitly
  **not** `KILL_ON_JOB_CLOSE`. The header comment gives the reason: closing a
  pane must leave its servers running, because a server outliving its pane is
  exactly what the Local panel exists to show. Correct for `python app.py`.
  An idle `claude.exe` + MCP plugin is not that.
- **`JobObjectGuard.AssignSelfToKillOnCloseJob()` (App.xaml.cs:74)** — the
  app-wide job does reap everything, but only when Perch exits. Perch had run
  56 hours straight. This net only fires on quit.
- **`ShutdownPaneAsync` (MainWindow.xaml.cs ~1341)** — Esc, Esc, `/exit`, 3 s
  grace, then `DestroyPty`. 293 `no session-end within grace; hard kill` lines
  exist across the log's lifetime, so the path runs often. Whether these 7 ever
  entered it is unknown: nothing was logged for them at close time.

**ANSWERED (2026-09-06, against `main` past v1.70.0).** Bot sessions *do* go
through `ShutdownPaneAsync` — and these seven never reached it, because nobody
ever closed them. It is a missing reaper, not a missing call.

- Bots are ordinary tabs. `TeamController` holds two delegates, `CreateTab` and
  `CloseSession` (TeamController.cs), wired to the same handlers the sidebar
  uses (AppController.cs) → `OnSessionClose` → `CloseTeardownAsync` →
  `ShutdownPaneAsync`. There is no bot-only spawn or teardown path.
- `ClaudeHeadless` cannot be the culprit: it runs `claude -p
  --no-session-persistence` through `ProcRunner` under a 5-minute timeout that
  kills the tree. It never makes a pane and never passes `--resume`.
- `BoardController` spawns no processes at all.
- **The proof they were never torn down:** `ConPty`'s watcher thread checks
  `if (_disposed) return;` *before* it logs (ConPty.cs). A properly disposed PTY
  is therefore SILENT at exit — so the absence of a log line at close time
  proves nothing. But all seven logged `process-exited code=-1` at the external
  kill, which means `_disposed` was still false, which means `Dispose` →
  `DestroyPty` → `ShutdownPaneAsync`'s `finally` had never run for any of them.

So they were live, open, idle panes the app was still holding — restored at
launch and untouched for 56 hours. Nothing reaped an idle agent pane.

**Two corrections to §4 above.** The "leaked session ids" are Claude
*conversation* ids, and `pane.ClaudeSessionId` is overwritten every time the
SessionStart hook reports a new one (AppController.cs) — so absence from
`sessions.json` is weaker evidence than it reads. Checked anyway: all five
absent ids are team-bot worktrees (anton, alush, big-dawg, joe, snobber) whose
transcripts froze at 2026-09-04 00:11 and never wrote again. Their tab records
really are gone while their processes lived on. **How those records vanished is
still unknown** — nothing was logged — which is precisely why the fix must not
depend on the answer (constraint 3).

## 6. Detection already exists

`LocalPoller.cs` already attributes any stray process to its owning pane by job
membership (`FindOwnerByJob`), surviving orphaning and pid reuse. The information
needed to spot "pane gone, job still populated, zero CPU for N hours" is already
being collected every poll. Nothing new has to be measured to detect this state.

## 7. Secondary finding

`%APPDATA%\Perch\errors.log` is **18 MB and never rotated**. It is also the only
forensic record — the 56-hour silence in it is what dated this incident. Worth a
size cap that does not destroy the history.

---

## What a fix has to satisfy

Any proposal should answer all of these, since the numbers above constrain each:

1. **Reclaim the whole job, not just `claude.exe`** — the MCP plugin server is
   ~45% of the per-session cost.
2. **Do not regress the deliberate `PaneJob` design** — dev servers started from
   a pane must still outlive it. An agent session and a backgrounded dev server
   need to be distinguishable, or the policy needs a different axis (idle time,
   session-record presence) than "is it in a pane job".
3. **Cover the forgotten-session case** — 5 of 7 had no session record at all.
   A reaper that walks `sessions.json` would have missed the majority of this
   incident.
4. **Fire without a Perch restart** — the app-wide kill-on-close job is not a
   safety net on a machine that stays up for days.
5. **Be observable** — this incident left no trace for 56 hours. Whatever
   reaps should log what it reaped and why.


---

## The fix, as built (2026-09-06)

Four pieces. Sleeping, not killing, is the spine of it: `OnSessionDormant`
already does the right teardown — polite `/exit` so the transcript is saved,
PTYs destroyed, **the tab kept**, `claude --resume` on wake — and it is the same
cold path `verify-comms.ps1` exercises. The reaper rides tested machinery
rather than inventing a second way to end a session.

| Piece | File | Constraint |
|---|---|---|
| Auto-sleep for idle agent tabs (4 h default, `Settings → Sleep idle agent tabs`) | `IdleReaper.SweepIdle` | 2, 4 |
| Reclaim the rest of a torn-down pane's tree, sparing anything serving a port | `JobSweep` | 1, 2 |
| Destroy any PTY that belongs to no awake tab | `IdleReaper.SweepOrphans` | 3 |
| `Reap.*` log lines + a toast + the tab visibly goes dormant | throughout | 5 |
| `errors.log` rolls at 8 MB, keeps 3 generations | `Log.RollIfBig` | §7 |

Eligibility, deliberately narrow: the tab must have run an agent, must not be
the active one, must not be mid-turn (Working/Waiting/Permission), must not
hold a loopback listener, and must have no room post parked for it. Idle is
measured from the freshest pane in the tab, on two clocks — the idle
watchdog's *sustained* output signal (so a statusline repaint doesn't count as
life) and the last write into the pane (so slow typing does).

`JobSweep` snapshots the pane's job members *before* teardown (membership is
unanswerable once the handle closes), waits 2 s, then kills survivors — except
any process holding a loopback listener **or parenting one**, since killing that
parent would orphan the listener. That is the same definition of "server" the
Local panel uses, so the two features can never disagree.

**The trade-off we accepted.** Distinguishing an idle agent from a
deliberately-backgrounded job stays a heuristic. A *detached*, non-listening,
silent job started from an agent pane (a long build that prints nothing) will
die when its tab is slept. A foreground job would have died at any close
anyway; only that narrow case is newly affected. Mitigated by sleeping rather
than killing, the 4-hour default, and a "Never" option in Settings.

Tests: `IdleReaperTests` (the policy matrix, one test per constraint) and
`JobSweepTests` (the serving/ancestor exemption).
