# Working in Perch

This repository is **Perch**, a Windows/macOS coding-agent workspace. The folder
name `cmux-win` is historical. Read [docs/CODEBASE-CONTEXT.md](docs/CODEBASE-CONTEXT.md)
for architecture and runtime flows. The first Codex review is in
[docs/reviews/2026-09-06.md](docs/reviews/2026-09-06.md); findings describe that
revision, so check current code before treating them as open issues. Read
[the fix follow-up](docs/reviews/2026-09-06-fixes.md) for implemented changes and
the outstanding CLI approval and real-agent/native-Mac checks.

## Start here

- Read `git status --short` and preserve existing changes. This workspace often
  contains the owner's mockups, screenshots, browser profiles, and Claude worktrees.
- Read `CLAUDE.md` for design intent and platform conventions. Its opening
  **Two hosts, one Core** section describes the current architecture. Later
  all-WPF/HwndHost/TerminalControl rules are historical; apply UI guidance to
  the actual web components, not a new WPF implementation.
- `docs/PARITY.md` is a historical inventory, not a current backlog. Projects,
  worktrees, CLI, resume, port discovery, and URL panes already exist.
- Inspect relevant implementation and tests before relying on comments. Several
  comments describe earlier behavior; the review records specific mismatches.

## Architecture constraints

- Shared application logic belongs in `src/Perch.Core` (net8.0; no WPF).
- Windows adapters live in `src/Perch`; macOS adapters in `src/Perch.Mac`.
  Extend `Hosting.cs`, `UiThread.cs`, or `Pty.cs` interfaces and implement both
  hosts when adding a native capability.
- Session/pane state is confined to `IUiThread`. On macOS this is the managed
  `AppDispatcher` thread, distinct from AppKit's main thread.
- UI is vanilla TypeScript/CSS in `src/web`, with xterm.js and esbuild. Keep
  existing components and tokens; do not add a frontend framework.
- `src/Perch/wwwroot` and the `bin/**/wwwroot` copies are generated. Edit web
  source, then bundle and build the host. esbuild alone does not update the
  copy served by a built executable.
- Page protocol: `bridge.ts` ↔ `PageMessages.cs`/`MessageRouter.cs` and
  `AppController.BuildRouter`. Agent IPC is a separate protocol in `PerchIpc.cs`
  and `tools/perch-cli`. Update both ends and relevant protocol tests.
- `tools/perch-cli` includes Claude/Codex wrappers and hooks. Some sources are
  linked into Core by its csproj; do not duplicate their implementations.
- Keyboard chords and labels use `chordMod()`/`modKeyLabel` from `bridge.ts`.
  Preserve Unix pipe anchors and platform-specific shell initialization.

## Build and validation

Requires .NET 8 and Node 20+. From the repo root:

```powershell
npm --prefix src/web ci                         # fresh checkout only
./scripts/build.ps1                            # Windows: typecheck, bundle, host
dotnet test src/Perch.Tests -f net8.0 --nologo
dotnet test src/Perch.Tests -f net8.0-windows --nologo
```

Run web tests from their own directory (the runner uses relative paths):

```powershell
Push-Location src/web
npm test
npm run typecheck
Pop-Location
```

- For host-only work with a valid existing bundle, use `-p:SkipWebBundle=true`.
- On macOS: bundle first, build `src/Perch.Mac`, compile `src/Perch`, then run
  tests with `-f net8.0`. A Windows machine cannot validate native mac behavior.
  The mac project now cross-builds on Windows (chmod is platform-gated), but
  compilation is not native runtime validation.
- Run focused behavioral tests for changes, then the relevant suites. Preserve
  spawn-budget and negative-proof tests; add coverage at the actual caller
  when introducing polling, not only at a cached helper.
- Before releases, preserve the existing delivery gates in `CLAUDE.md`:
  `scripts/verify-comms.ps1` on Windows and `node scripts/mac-e2e.mjs` on macOS.
  They need real Claude authentication and use tokens. Unit tests do not replace
  the cold-start/submit-acknowledgement checks.

## Isolated application runs

- Set `PERCH_DATA_DIR` to a unique scratch root **before starting Perch**.
  Data lives in its `perch/` child. Changing `APPDATA` alone does not isolate it.
- Mock agent tests also need isolated `CLAUDE_CONFIG_DIR` / `CODEX_HOME`.
  The Codex wrapper writes `perch.config.toml` in its configured home.
- `PERCH_ENABLE_TEST_IPC=1` enables the fixed `perch\control` pipe; separate data
  roots do not create separate control-pipe names. Run one IPC harness at a time.
- Inspect a harness before launching it. Some older Windows scripts stop every
  Debug Perch process or replace built shims. Prefer tracking and stopping only
  the PID launched for the current check; preserve the user's running app.
- Drive input through test IPC/CDP, not global keystrokes. UI changes need fresh
  visual verification per `CLAUDE.md`; use DOM geometry to check DPI/capture issues.
- Do not use the owner's sessions or cloud resources as disposable test fixtures.

## Keep the handoff useful

Update the context document when architecture, commands, isolation, or storage
contracts change. Keep dated findings in the review report; do not turn this
file into a growing bug log. Distinguish a source-level finding, an isolated
reproduction, and a verified native end-to-end result.
