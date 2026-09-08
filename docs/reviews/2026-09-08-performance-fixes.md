# Performance fixes and installed-build comparison — 2026-09-08

Follow-up to [the performance review](2026-09-08-performance.md). Changes are
implemented locally and built in Debug and Release. The owner's installed app
and running sessions were not replaced or restarted.

## Same-workload comparison

Baseline: installed Release build at `d1c41d2`. Candidate: Release build of this
working tree. Both ran sequentially on the same Windows machine/WebView runtime,
with fresh PERCH_DATA_DIR, Claude, Codex and gcloud profiles, identical cmd shells,
window dimensions, fonts and CDP anti-throttling flags. No real agents ran.

The workload used 40 synthetic terminal-input-to-render samples per scenario,
40 tab switches, background output, a 1,500-line history fixture plus an alternate
screen, sleep/wake, and a 1,000-event synthetic Inspector journal followed by an
unchanged refresh and one appended event. Native shell input traversed the real
page bridge, Core, ConPTY and xterm renderer. TUI frames and background output
were injected at the page's existing output consumer; this is not a real-agent
benchmark or a saturated native-output transport benchmark.

| Measurement | Installed current | Candidate | Difference |
|---|---:|---:|---:|
| App private bytes at startup | 417.0 MiB | 413.6 MiB | Essentially unchanged |
| App private bytes after workload | 680.5 MiB | 474.7 MiB | **205.8 MiB / 30.2% lower** |
| GPU-process private bytes after workload | 308.3 MiB | 139.5 MiB | **54.8% lower** |
| Renderer private bytes after workload | 119.5 MiB | 81.2 MiB | 32.1% lower |
| Ordinary-shell echo p95 | 17.2 ms | 17.4 ms | Essentially unchanged |
| Synchronized-redraw echo median | 56.95 ms | 16.95 ms | **70.2% lower** |
| Synchronized-redraw echo p95 | 64.7 ms | 29.3 ms | **54.7% lower** |
| Background-output echo median | 11.65 ms | 7.30 ms | 37.3% lower |
| Background-output echo p95 | 19.8 ms | 17.8 ms | 10.1% lower |
| Unchanged journal reply | 127,048 bytes | 288 bytes | **99.77% smaller** |
| One appended journal event | 127,176 bytes | 415 bytes | 99.67% smaller |
| Unchanged journal DOM rows | Replaced | Preserved | Selection/expansion nodes survive |
| Addons after tab switches (two terminals) | 19 + 20 | 3 + 4 | Candidate counts stay flat |
| Live terminal objects while one tab sleeps | 2 | 1 | Sleeping terminal reclaimed |
| Normal and alternate-screen history after wake | Fixture incomplete | Both preserved | Startup cannot overwrite final screen |

Private bytes are committed process memory, **not** resident RAM or dedicated
VRAM. Totals include only the Perch host and its WebView process tree; shells,
agents and other applications are excluded. Working sets are recorded separately
and are not summed because they can double-count shared pages.

This is one controlled A/B pass, not a confidence interval or a guarantee for
every workload. Background-output maxima were 185.1 ms and 87.4 ms respectively;
an earlier candidate probe also had a 125 ms outlier. Occasional stalls remain.
The previously observed 1.86 GiB production session is a different, long-running
workload and must not be compared directly with these fresh-instance totals.
No claim is made that every user's memory use will drop by 30%.

Raw results: [comparison.json](2026-09-08-performance-probes/comparison.json),
[baseline](2026-09-08-performance-probes/current/measurements.json),
[candidate](2026-09-08-performance-probes/fixed/measurements.json). Process samples
and journal measurements are alongside them. Earlier development probes are
kept in the parent probe directory and are not the final A/B data.

## Implementation

- Pin fit 0.10, WebGL 0.18 and serialize 0.13 to the existing xterm 5.5 core.
  Remove the incompatible WebGL disposal workaround and validate real tab
  switching rather than assuming a catch releases ownership.
- `QueuedPty` provides one ordered background writer for all input sources.
  The page sends at most 16 KiB until the native write finishes; readiness,
  sequence and input-instance IDs prevent early input and stale acknowledgements.
  Non-page producer queues have an explicit 16 MiB budget and surface failures.
  Native write completion remains separate from agent prompt-submit acceptance.
- `PaneOutputBatcher` coalesces bursts, yields between bounded per-pane and
  aggregate slices, and orders exit markers after queued output. Original
  byte-count acknowledgements and native output backpressure are retained.
- The synchronized-output quiet gap remains 16 ms to include trailing cursor
  returns. A separate deadline caps continuous batches at 32 ms, or 24 ms around
  input, instead of allowing a 100 ms continuously extended hold.
- Per-conversation transcript workers stream complete JSONL rows with a 64 KiB
  read buffer, preserve partial-row/image offsets, and reuse unchanged immutable
  projections. Serialization leaves the state thread. Model discovery triggers
  asynchronous team-limit reconciliation; recently requested team snapshots
  survive through the next poll so sleeping bots' final replies remain readable.
- Inspector responses use revision-checked suffixes and request correlation.
  A missing cache base causes a full refresh. Unchanged rows remain mounted;
  expansion measurements run only for changed rows. History and search remain
  complete; the implementation does not truncate the journal or virtualize away
  searchable rows.
- Dormant terminals wait for exit and parsed output before snapshot/disposal.
  Snapshot completion checks that the pane is still dormant, still the same
  instance and has received no newer output. Wake restores the final screen into
  scrollback before a fresh shell can clear it, including alternate-screen text.
  Snapshots are in-memory and follow the existing app-restart persistence policy.

## Validation

Web: 257 tests and typechecking passed. Both Windows configurations built.
Core: 644 passed. Windows: 646 passed. Each suite has five existing
platform skips. The Mac host cross-builds on Windows; this is not a native
Mac run. Existing warnings in TeamControllerTests and the CLI HookHandler are
unrelated to these changes.

New behavioral tests cover ordered blocked writes, disposal, exact byte transport,
output fairness/exit ordering, partial Unicode JSONL records, cache eviction and
active team reuse, journal revision handling, large paste backpressure, cold
readiness, and synchronized-frame deadlines. Live scripts exercise the actual
application with fresh profiles, not owner sessions:

```powershell
node scripts/performance-live.mjs PORT OUTPUT_DIR             # candidate
node scripts/performance-live.mjs PORT OUTPUT_DIR --baseline  # collect old behavior
node scripts/inspector-performance-live.mjs PORT SCRATCH_ROOT OUTPUT_DIR
./scripts/performance-memory.ps1 -ProcessId PID -OutputPath OUTPUT_FILE
```

These scripts require an explicitly isolated instance and port. They never
launch real agents or use the fixed control pipe. The memory script requires
process-parent access. The journal script registers only a synthetic transcript
with the explicitly selected test pane's ordinary IPC endpoint.

Fresh WebView screenshots were inspected with opaque capture backgrounds;
terminal geometry was 390.4 × 562 CSS pixels, and the Inspector's stream geometry
remained valid with 1,001 rows. The existing chrome, fonts, spacing and controls
were preserved. Screenshots are in the probe directory.

Real Claude/Codex cursor behavior under every redraw pattern and native Mac
behavior remain release-gate work. No real-agent delivery gate, deployment,
installation replacement, commit or publication was performed.
