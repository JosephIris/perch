# Perch performance review — 2026-09-08

Implemented fixes and a controlled installed-build comparison are recorded in
[the follow-up](2026-09-08-performance-fixes.md). Findings below describe the
original reviewed revision.

Reviewed main at `d1c41d2b089ac51c115d2184adaca6e9494a0bc9` after successful
`git pull --ff-only` (already up to date). The running installed app's WebView
command line identifies that same revision. Owner changes were preserved.
This is a review, with live process accounting and isolated mechanism probes;
application behavior has not been changed.

## Live application memory, excluding agents

Read-only Windows process-parent inspection identified Perch PID 720, its
WebView browser PID 19616, and that browser's children. Sample:

| Component | PID | Working set MiB | Private bytes MiB |
|---|---:|---:|---:|
| Native Perch host | 720 | 235.3 | 315.8 |
| WebView browser | 19616 | 118.6 | 41.3 |
| WebView GPU process | 25752 | 840.7 | 1089.8 |
| WebView renderer | 6900 | 438.2 | 432.0 |
| Crash handler | 18408 | 12.1 | 3.1 |
| Network service | 44148 | 35.9 | 12.0 |
| Storage service | 43724 | 18.9 | 9.6 |

Total private bytes: approximately **1.86 GiB**, excluding terminal shells,
agents, wrappers, and other applications' WebViews. The GPU process accounts
for about 57% of this total. Private bytes measure committed process memory,
not resident physical RAM or dedicated GPU VRAM. Summed working sets may
double-count shared pages. This snapshot establishes substantial app overhead,
but does not establish growth versus an older release or identify GPU objects.

## Findings, in priority order

### P1 — Tab switches retain failed WebGL addons

`src/web/src/pane.ts:468` catches addon disposal failure and restores the DOM
renderer. That fixes the black-pane symptom but not addon ownership.
Installed WebGL 0.19 accesses `_core._store._isDisposed` during disposal;
installed xterm 5.5 has `_isDisposed` on its older Disposable implementation.
The failure is explicitly documented in the current Pane implementation.

In xterm's `src/common/public/AddonManager.ts:54`, disposal marks the addon
disposed, calls its implementation, then removes it from `_addons`. When the
implementation throws, removal never happens. Subsequent disposal returns
immediately because the disposed flag is already set. Each return to a tab
creates another addon, so repeated switches accumulate retained addons,
including their renderer/canvas references, until the terminal is collected.

Probe of the **installed AddonManager source**, with the documented exception
modeled by a stub addon: 100 cycles retain 100 addons; successful-disposal
control retains zero. This proves the ownership mechanism, not how much of the
live GPU process is attributable to it. Renderer teardown does delete some GL
resources before the exception; it would be incorrect to claim all textures
remain allocated.

Fix direction: use compatible core/addon versions and verify actual browser
load/hide/show/dispose cycles leave addon counts flat. Validate rendering and
GPU-process memory across repeated switches on both hosts. Do not merely add
another catch around disposal.

### P1 — Transcript work competes directly with input on the UI thread

`src/Perch.Core/AppController.cs:3278` calls `InspectorFor` before its first
await. `TranscriptReader.cs:135` synchronously opens and reads the entire
unread file portion, allocates a matching byte array, decodes and parses rows,
and retains all events. Even an unchanged file still incurs a full
`Collapse(tail.Events)` traversal and new list. The handler projects and
serializes the full event history for each response.

On the page, `inspector.ts:588` replaces and reconstructs the stream DOM;
working-agent polling is every two seconds. Long conversations therefore
increase work on both threads that service typing. The cold read is especially
exposed to large transcripts and disk delays. This is source-confirmed; no
latency percentage or live transcript timing is claimed.

Fix direction: serialize transcript ownership on a background worker, deliver
versioned snapshots/deltas, cache unchanged projections, and incrementally
update/window the journal. Preserve complete history on disk. Merely making
the handler async does not move the pre-await parsing off the UI thread.

### P2 — Synchronized output intentionally adds perceptible echo latency

`src/web/src/sync-output.ts:96` starts holding on a synchronized-output begin
marker and ignores the end marker for flushing. Every subsequent chunk resets
the 16 ms quiet timer. Continuous arrivals keep the frame, including visible
typing feedback, buffered until the 100 ms elapsed check is reached.

The actual batcher, fed a complete begin/end frame and subsequent chunks at
10 ms intervals, held output for **104.2 ms** in the initial probe. A plain
shell stream with no sync begin passes synchronously. Thus this specifically
explains a possible TUI redraw delay, not every ordinary-shell keystroke.
Timer scheduling can add further delay; 100 ms is not a strict deadline.

Fix direction: preserve the cursor-position stabilization this batching was
introduced for, but make completion/input-sensitive flushing bounded by a
shorter deadline. Compare actual key-to-visible-echo latency and cursor
stability under sustained Codex redraws before changing the policy.

### P2 — Sleeping sessions keep terminal history and transcript caches

`src/web/src/workspace.ts:181` disposes a stage only when its session ID leaves
the snapshot. Dormant sessions remain present. Hiding a stage releases its
active WebGL renderer but retains xterm buffers (10,000 scrollback lines per
terminal), DOM, and addon state. `AppController.cs:4765` also forgets transcript
caches only for removed pane IDs, not sleeping panes.

This is deliberate history preservation, not an unbounded single-terminal
scrollback leak. Nevertheless, sleeping agents does not reclaim all Perch-side
resources; memory scales with previously opened sessions and retained history.
It compounds the addon retention above. Fix direction: add a measured dormant
cache budget with explicit terminal history snapshot/restore and reloadable
transcript eviction, preserving user-visible history.

## Additional input-path exposure

`AppController.cs:2480` synchronously writes PTY input on the state/UI thread;
Windows `ConPty.Write` calls synchronous FileStream.Write and Flush. A blocked
consumer or large paste can block the entire host dispatcher. Every PTY output
chunk also posts an individual dispatcher callback and JSON/base64 message
(`AppController.cs:4804`). Existing backpressure bounds bytes **per pane**, not
aggregate message work across all panes. These warrant caller-level latency
profiling; they are not presented as measured causes of the reported lag.

## Validation and limits

- `node scripts/review-performance.mjs` runs isolated ownership and batching
  probes using installed/source modules, with a successful-disposal control
  and a plain-shell pass-through control. No agents or app state are touched.
- Live app process ownership and memory were inspected without restarting,
  navigating, typing into, or otherwise altering the owner's sessions.
- No older-build comparison, native macOS run, heap/GPU allocation profile,
  or actual key-to-paint benchmark was performed. The precise share of lag
  caused by each finding remains unmeasured.
- The old `scripts/test-perf-flow.ps1` was inspected and intentionally not run:
  it deletes normal APPDATA state and kills all Debug Perch instances. A future
  end-to-end benchmark must use isolated PERCH_DATA_DIR/agent homes and track
  only its launched process. Its 500 ms ping threshold is also too generous to
  rule out a perceptibly laggy terminal.
