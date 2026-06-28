# Session-resume end-to-end self-test.
#
# Exercises the whole "restore the same Claude sessions" feature against a
# real running Perch, using a MOCK claude that simulates Claude Code's
# SessionStart hook (incl. `claude --resume <id>`). No real Claude needed.
#
# What it proves:
#   Phase 0  per-pane cwd is persisted (OSC 7 → pane.Cwd in sessions.json).
#   Phase 1  the SessionStart hook's session_id is captured + persisted on
#            the pane (run a mock `claude` IN a real pane via the PTY).
#   Phase 2  on relaunch a resumable pane DEFERS its spawn until the resume
#            decision, then spawns with `claude --resume <id>` injected — and
#            the resumed mock re-fires its hook (host re-captures the id).
#   Phase 3  closing archives to Recently-closed; restore brings it back;
#            purge drops it for good.
#
# Mechanism: swaps <out>/tools/claude.cmd with a mock for the run (restored in
# finally), drives verbs over the control pipe (`perch test ...`), and reads
# state via `perch test state.dump` (STATE_DUMP json in errors.log) +
# sessions.json. Window stays minimized; no keystroke synthesis.

[CmdletBinding()]
param(
    [string]$OutDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64",
    [switch]$KeepVisible
)

$ErrorActionPreference = 'Continue'
$ExePath   = Join-Path $OutDir 'Perch.exe'
$ToolsDir  = Join-Path $OutDir 'tools'
$PerchExe  = Join-Path $ToolsDir 'perch.exe'
$ClaudeCmd = Join-Path $ToolsDir 'claude.cmd'
$ClaudeBak = Join-Path $ToolsDir 'claude.cmd.realbak'
# Isolated data root (PERCH_DATA_DIR) so the test instance never collides with
# a real/running Perch — separate sessions.json, settings, errors.log AND
# WebView2 user-data folder (a shared WebView2 folder is locked single-writer,
# which is what 0x8007139F means). Short path to dodge WebView2's deep dirs.
$DataDir   = 'C:\tmp\perch-resume-test'
$ClaudeDir = Join-Path $DataDir 'claude'         # isolated CLAUDE_CONFIG_DIR
$LogPath   = Join-Path $DataDir 'perch\errors.log'
$SessPath  = Join-Path $DataDir 'perch\sessions.json'
$SID       = "mock-sid-$(Get-Random)"
$Transcript = Join-Path $ClaudeDir "projects\test\$SID.jsonl"

if (-not (Test-Path $ExePath))  { throw "Perch.exe not found at $ExePath (build first)" }
if (-not (Test-Path $PerchExe)) { throw "perch.exe missing at $PerchExe (build first)" }
if (-not (Test-Path $ClaudeCmd)){ throw "claude.cmd missing at $ClaudeCmd (build first)" }

if (-not ('Perch.WinShowR' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace Perch { public static class WinShowR {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  public const int SW_MINIMIZE = 6;
} }
'@
}

function Stop-Perch {
    Get-Process -Name Perch -EA SilentlyContinue |
        Where-Object { $_.Path -like '*\bin\Debug\*' } |
        Stop-Process -Force -EA SilentlyContinue
    Start-Sleep -Milliseconds 400
}

function Launch-Perch {
    $env:PERCH_ENABLE_TEST_IPC = '1'
    $env:PERCH_TEST_SID = $SID
    $env:PERCH_DATA_DIR = $DataDir
    # Isolated Claude config root so the mock's stub transcripts (and the host's
    # resume pre-flight) don't touch the real ~/.claude.
    $env:CLAUDE_CONFIG_DIR = $ClaudeDir
    $p = Start-Process -PassThru -FilePath $ExePath
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        $p.Refresh()
        if ($p.HasExited) { throw "perch exited early code=$($p.ExitCode)" }
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($p.MainWindowHandle -eq [IntPtr]::Zero) { throw "main window never appeared" }
    if (-not $KeepVisible) {
        [Perch.WinShowR]::ShowWindow($p.MainWindowHandle, [Perch.WinShowR]::SW_MINIMIZE) | Out-Null
    }
    return $p
}

function Test-Verb {
    param([string]$Verb, [hashtable]$Fields)
    $a = @('test', $Verb)
    if ($Fields) { foreach ($k in $Fields.Keys) { $a += @("--$k", $Fields[$k]) } }
    $out = & $PerchExe @a 2>&1
    if ($LASTEXITCODE -ne 0) { throw "perch test $Verb exited $LASTEXITCODE`: $out" }
}

function Wait-Pattern {
    param([string]$Pattern, [int]$TimeoutSec = 12)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $hit = Get-Content $LogPath -EA SilentlyContinue |
            Where-Object { $_ -match $Pattern } | Select-Object -Last 1
        if ($hit) { return $hit }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function Get-State {
    # Fire state.dump, then parse the last STATE_DUMP{...} line from the log.
    Test-Verb -Verb 'state.dump'
    Start-Sleep -Milliseconds 300
    $line = Get-Content $LogPath -EA SilentlyContinue |
        Where-Object { $_ -match 'STATE_DUMP\{' } | Select-Object -Last 1
    if (-not $line) { return $null }
    if ($line -match 'STATE_DUMP(\{.*\})\s*$') { return $matches[1] | ConvertFrom-Json }
    return $null
}

function Fail($msg, $proc) {
    if ($proc) { Stop-Process -Id $proc.Id -Force -EA SilentlyContinue }
    throw "FAIL: $msg"
}

$mock = @'
@echo off
rem MOCK claude — session-resume self-test. Simulates Claude Code's
rem SessionStart hook so the host captures/persists a session id, AND writes a
rem stub transcript under CLAUDE_CONFIG_DIR\projects so the host's resume
rem pre-flight (TranscriptExists) sees a resumable session. Reads PERCH_PIPE
rem from the pane env (set by the host) via perch.exe.
setlocal
if "%~1"=="--resume" (
  echo [mock-claude] RESUMED %~2
  echo {"session_id":"%~2","source":"resume"} | "%~dp0perch.exe" hooks claude session-start
) else (
  if not exist "%CLAUDE_CONFIG_DIR%\projects\test" mkdir "%CLAUDE_CONFIG_DIR%\projects\test"
  echo {"type":"summary"} > "%CLAUDE_CONFIG_DIR%\projects\test\%PERCH_TEST_SID%.jsonl"
  echo [mock-claude] STARTED %PERCH_TEST_SID%
  echo {"session_id":"%PERCH_TEST_SID%","source":"startup"} | "%~dp0perch.exe" hooks claude session-start
)
endlocal
'@

$p = $null
try {
    # --- Swap in the mock claude --------------------------------------------
    if (-not (Test-Path $ClaudeBak)) { Copy-Item $ClaudeCmd $ClaudeBak -Force }
    Set-Content -Path $ClaudeCmd -Value $mock -Encoding ascii
    Write-Host "Mock claude staged at $ClaudeCmd (SID=$SID)"

    Stop-Perch
    # Fresh isolated data + Claude dirs.
    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    New-Item -ItemType Directory -Force (Join-Path $DataDir 'perch') | Out-Null
    New-Item -ItemType Directory -Force $ClaudeDir | Out-Null

    # --- Phase 1+0: cold launch, capture session id via a real pane ----------
    Write-Host "`nPhase 1/0: capture session id + per-pane cwd"
    $p = Launch-Perch
    Write-Host "  perch pid=$($p.Id)"
    if (-not (Wait-Pattern -Pattern 'Pane\.spawn' -TimeoutSec 15)) { Fail "initial pane never spawned" $p }
    Start-Sleep -Seconds 3   # let pwsh reach an interactive prompt

    # Type `claude` into the active pane → runs the mock → fires the hook.
    Test-Verb -Verb 'pty.send' -Fields @{ text = "claude`r`n" }

    $st = $null
    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline) {
        $st = Get-State
        $active = $st.sessions | Where-Object { $_.active } | Select-Object -First 1
        $pane = $active.panes | Select-Object -First 1
        if ($pane.claudeSessionId -eq $SID) { break }
        Start-Sleep -Milliseconds 500
    }
    $active = $st.sessions | Where-Object { $_.active } | Select-Object -First 1
    $pane = $active.panes | Select-Object -First 1
    if ($pane.claudeSessionId -ne $SID) { Fail "session id not captured (got '$($pane.claudeSessionId)', want '$SID')" $p }
    Write-Host "  [+] captured claudeSessionId=$($pane.claudeSessionId)"
    if ([string]::IsNullOrEmpty($pane.cwd)) { Fail "per-pane cwd not captured/persisted (OSC 7)" $p }
    Write-Host "  [+] per-pane cwd persisted: $($pane.cwd)"

    # --- persistence to disk -------------------------------------------------
    $disk = Get-Content $SessPath -Raw | ConvertFrom-Json
    $rootPane = $disk.Sessions[0].Root
    if ($rootPane.ClaudeSessionId -ne $SID) { Fail "sessions.json missing ClaudeSessionId" $p }
    if ([string]::IsNullOrEmpty($rootPane.Cwd)) { Fail "sessions.json missing per-pane Cwd" $p }
    Write-Host "  [+] sessions.json persisted ClaudeSessionId + Cwd"
    if (-not (Test-Path $Transcript)) { Fail "mock did not write a stub transcript at $Transcript" $p }
    Write-Host "  [+] stub transcript present (resume pre-flight will pass)"

    # --- Phase 2: relaunch → defer → resume injection ------------------------
    Write-Host "`nPhase 2: relaunch defers + injects claude --resume"
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    Start-Sleep -Milliseconds 500
    Remove-Item $LogPath -Force -EA SilentlyContinue
    $p = Launch-Perch
    Write-Host "  relaunch pid=$($p.Id)"

    if (-not (Wait-Pattern -Pattern 'Pane\.resize\.defer' -TimeoutSec 15)) {
        Fail "resumable pane did not defer its spawn (resume prompt gating broken)" $p
    }
    Write-Host "  [+] resumable pane deferred its spawn (awaiting decision)"

    Test-Verb -Verb 'resume.decision' -Fields @{ accept = 'true' }

    $spawn = Wait-Pattern -Pattern "claude --resume $([regex]::Escape($SID))" -TimeoutSec 12
    if (-not $spawn) { Fail "spawn command did not inject 'claude --resume $SID'" $p }
    Write-Host "  [+] resume injected into spawn command"

    # The resumed mock re-fires the SessionStart hook → host re-receives it.
    if (-not (Wait-Pattern -Pattern 'PerchIpc\.recv .* type=session' -TimeoutSec 12)) {
        Fail "resumed claude did not re-report its session id" $p
    }
    Write-Host "  [+] resumed agent re-reported (restore-progress 'ready' signal)"

    # --- Phase 2b: NO transcript → pre-flight suppresses resume ---------------
    Write-Host "`nPhase 2b: stale id (no transcript) spawns a clean shell, no resume"
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    Start-Sleep -Milliseconds 500
    Remove-Item $Transcript -Force -EA SilentlyContinue   # the conversation is "gone"
    Remove-Item $LogPath -Force -EA SilentlyContinue
    $p = Launch-Perch
    Write-Host "  relaunch pid=$($p.Id)"
    # With no transcript the pane must spawn normally (not deferred) and WITHOUT
    # a resume command. Wait for a real spawn, then assert the absences.
    if (-not (Wait-Pattern -Pattern 'Pane\.spawn:' -TimeoutSec 15)) { Fail "pane never spawned on stale-id relaunch" $p }
    Start-Sleep -Seconds 1
    $logTxt = Get-Content $LogPath -Raw -EA SilentlyContinue
    if ($logTxt -match 'Pane\.resize\.defer') { Fail "deferred spawn for a non-resumable (no-transcript) session" $p }
    if ($logTxt -match 'claude --resume')      { Fail "injected resume for a session with no transcript" $p }
    Write-Host "  [+] no-transcript session spawned a clean shell (no defer, no resume)"

    # --- Phase 3: archive → restore → purge ----------------------------------
    Write-Host "`nPhase 3: archive / restore / purge"
    Test-Verb -Verb 'session.new'
    Start-Sleep -Milliseconds 800
    $st = Get-State
    $newId = ($st.sessions | Where-Object { $_.active }).id
    if (-not $newId) { Fail "session.new produced no active session" $p }
    Write-Host "  created session $newId"

    Test-Verb -Verb 'session.close' -Fields @{ id = $newId }
    Start-Sleep -Milliseconds 800
    $st = Get-State
    if ($st.sessions.id -contains $newId) { Fail "closed session still live" $p }
    if (-not ($st.closedSessions.id -contains $newId)) { Fail "closed session not archived to Recently closed" $p }
    Write-Host "  [+] close archived it to Recently closed"

    Test-Verb -Verb 'session.restore' -Fields @{ id = $newId }
    Start-Sleep -Milliseconds 800
    $st = Get-State
    if (-not ($st.sessions.id -contains $newId)) { Fail "restore did not bring the session back" $p }
    if ($st.closedSessions.id -contains $newId) { Fail "restored session still in Recently closed" $p }
    Write-Host "  [+] restore brought it back (and out of Recently closed)"

    Test-Verb -Verb 'session.close' -Fields @{ id = $newId }
    Start-Sleep -Milliseconds 600
    Test-Verb -Verb 'session.purge' -Fields @{ id = $newId }
    Start-Sleep -Milliseconds 600
    $st = Get-State
    if ($st.closedSessions.id -contains $newId) { Fail "purge did not remove from Recently closed" $p }
    Write-Host "  [+] purge dropped it from Recently closed"

    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    Start-Sleep -Milliseconds 400

    $errs = @(Get-Content $LogPath -EA SilentlyContinue | Where-Object { $_ -match '\bERROR\b' })
    if ($errs.Count -gt 0) {
        Write-Host "`nWARN: ERROR lines during run:" -ForegroundColor Yellow
        $errs | Select-Object -Last 12 | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    }

    Write-Host "`nALL PASS  --  session resume + archive/restore verified" -ForegroundColor Green
    exit 0
}
finally {
    if ($p) { Stop-Process -Id $p.Id -Force -EA SilentlyContinue }
    if (Test-Path $ClaudeBak) { Move-Item $ClaudeBak $ClaudeCmd -Force -EA SilentlyContinue }
    Remove-Item Env:PERCH_TEST_SID -EA SilentlyContinue
    Remove-Item Env:PERCH_DATA_DIR -EA SilentlyContinue
    Remove-Item Env:CLAUDE_CONFIG_DIR -EA SilentlyContinue
}
