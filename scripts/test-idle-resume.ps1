# Idle (sleep) -> wake self-test.
#
# The bug this pins: sleeping a tab tears its PTYs down, and waking it is
# supposed to respawn them with `claude --resume <id>` injected. The respawn
# only ever happens when the PAGE reports the pane's size again (pane.resize is
# the sole trigger for a lazy spawn) -- and a stage's panes stay mounted while
# hidden, so nothing re-reports on its own unless the page treats the switch as
# an ENTRY into that stage and forces a refit.
#
# Sleeping the LAST live tab in a project leaves you on the empty workspace,
# which the page did not count as "a session was on screen". Coming back from
# it was therefore not an entry: no refit, no pane.resize, no spawn. The tab
# returned showing its old scrollback -- a dead shell prompt in the right
# directory -- and the armed resume never ran. That is exactly the reported
# symptom, so this test sleeps a SINGLE-tab workspace on purpose.
#
# Mechanism (same as test-session-resume.ps1): a mock `claude` that fires the
# real SessionStart hook and writes a stub transcript, an isolated PERCH_DATA_DIR
# + CLAUDE_CONFIG_DIR, and the control pipe (`perch test <verb>`) to drive the
# same session.dormant / session.select verbs the sidebar sends. No real Claude,
# no tokens, no contact with your running Perch.

# NOTE: no [CmdletBinding()] here on purpose -- see test-session-resume.ps1.
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
$DataDir   = 'C:\tmp\perch-idle-test'
$ClaudeDir = Join-Path $DataDir 'claude'
$LogPath   = Join-Path $DataDir 'perch\errors.log'
$SessPath  = Join-Path $DataDir 'perch\sessions.json'
$SID       = "mock-sid-$(Get-Random)"
$Transcript = Join-Path $ClaudeDir "projects\test\$SID.jsonl"

if (-not (Test-Path $ExePath))  { throw "Perch.exe not found at $ExePath (build first)" }
if (-not (Test-Path $PerchExe)) { throw "perch.exe missing at $PerchExe (build first)" }
if (-not (Test-Path $ClaudeCmd)){ throw "claude.cmd missing at $ClaudeCmd (build first)" }

if (-not ('Perch.WinShowI' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace Perch { public static class WinShowI {
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
        [Perch.WinShowI]::ShowWindow($p.MainWindowHandle, [Perch.WinShowI]::SW_MINIMIZE) | Out-Null
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
rem MOCK claude - idle/wake self-test. Fires the real SessionStart hook so the
rem host captures a session id, and writes a stub transcript so the wake's
rem resume pre-flight passes. `cmd /k` keeps the "agent" alive, so sleeping the
rem tab exercises the polite ESC ESC /exit teardown rather than a bare shell.
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
cmd /k
endlocal
'@

$p = $null
try {
    if (-not (Test-Path $ClaudeBak)) { Copy-Item $ClaudeCmd $ClaudeBak -Force }
    Set-Content -Path $ClaudeCmd -Value $mock -Encoding ascii
    Write-Host "Mock claude staged at $ClaudeCmd (SID=$SID)"

    Stop-Perch
    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    New-Item -ItemType Directory -Force (Join-Path $DataDir 'perch') | Out-Null
    New-Item -ItemType Directory -Force $ClaudeDir | Out-Null

    # --- Phase 1: one tab, one agent ----------------------------------------
    Write-Host "`nPhase 1: single tab with a captured agent session"
    $p = Launch-Perch
    Write-Host "  perch pid=$($p.Id)"
    if (-not (Wait-Pattern -Pattern 'Pane\.spawn' -TimeoutSec 15)) { Fail "initial pane never spawned" $p }
    Start-Sleep -Seconds 3   # let pwsh reach an interactive prompt

    Test-Verb -Verb 'pty.send' -Fields @{ text = "claude`r`n" }

    $st = $null
    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline) {
        $st = Get-State
        $act = $st.sessions | Where-Object { $_.active } | Select-Object -First 1
        if (($act.panes | Select-Object -First 1).claudeSessionId -eq $SID) { break }
        Start-Sleep -Milliseconds 500
    }
    $act = $st.sessions | Where-Object { $_.active } | Select-Object -First 1
    if (-not $act) { Fail "no active session after launch" $p }
    $sessionId = $act.id
    $paneId    = ($act.panes | Select-Object -First 1).id
    if (($act.panes | Select-Object -First 1).claudeSessionId -ne $SID) {
        Fail "session id not captured (got '$(($act.panes|Select-Object -First 1).claudeSessionId)', want '$SID')" $p
    }
    if (-not (Test-Path $Transcript)) { Fail "mock did not write a stub transcript at $Transcript" $p }
    Write-Host "  [+] session=$sessionId pane=$paneId claudeSessionId=$SID, transcript on disk"
    if (($st.sessions | Measure-Object).Count -ne 1) {
        Fail "expected exactly one session (the empty-workspace path is the point of this test)" $p
    }

    # --- Phase 2: sleep it --------------------------------------------------
    Write-Host "`nPhase 2: sleep the tab (moon button -> session.dormant)"
    Test-Verb -Verb 'session.dormant' -Fields @{ id = $sessionId }
    # The polite exit runs on a ~3.5s grace before the hard kill. Wait it out so
    # the wake is a clean respawn rather than a cancel-the-grace race.
    $paneIdN = ($paneId -replace '-', '')
    if (-not (Wait-Pattern -Pattern "Shutdown: pane=$([regex]::Escape($paneIdN))" -TimeoutSec 15)) {
        Fail "sleeping the tab never tore its pane down" $p
    }
    Start-Sleep -Milliseconds 800
    $disk = Get-Content $SessPath -Raw | ConvertFrom-Json
    if (-not $disk.Sessions[0].Dormant) { Fail "sessions.json does not record the tab as Dormant" $p }
    $st = Get-State
    if ($st.sessions | Where-Object { $_.active }) {
        Fail "expected the empty workspace after sleeping the only tab (test premise broken)" $p
    }
    Write-Host "  [+] tab is dormant, panes are down, workspace is empty"

    # --- Phase 3: wake it ---------------------------------------------------
    # Rotate the log so every assertion below is about the WAKE, not the launch.
    Write-Host "`nPhase 3: wake it (select the Idle-drawer row -> session.select)"
    Remove-Item $LogPath -Force -EA SilentlyContinue
    Test-Verb -Verb 'session.select' -Fields @{ id = $sessionId }

    # THE regression assertion: the page must re-report the pane's size, because
    # a lazy spawn has no other trigger.
    if (-not (Wait-Pattern -Pattern 'Pane\.resize\.spawn' -TimeoutSec 15)) {
        Fail "woken tab never respawned a PTY (page sent no pane.resize -- the tab is a dead terminal)" $p
    }
    Write-Host "  [+] page re-reported the pane size -> PTY respawned"

    if (-not (Wait-Pattern -Pattern "claude --resume $([regex]::Escape($SID))" -TimeoutSec 12)) {
        Fail "respawn did not inject 'claude --resume $SID'" $p
    }
    Write-Host "  [+] respawn injected claude --resume $SID"

    if (-not (Wait-Pattern -Pattern 'PerchIpc\.recv .* type=session' -TimeoutSec 15)) {
        Fail "resumed agent never re-reported its session id (resume didn't take)" $p
    }
    Write-Host "  [+] resumed agent re-reported in (wake is complete)"

    $disk = Get-Content $SessPath -Raw | ConvertFrom-Json
    if ($disk.Sessions[0].Dormant) { Fail "woken tab is still flagged Dormant on disk" $p }
    Write-Host "  [+] Dormant flag cleared"

    Write-Host "`nPASS: sleep -> wake respawns the panes and resumes the agent." -ForegroundColor Green
}
finally {
    if ($p) { Stop-Process -Id $p.Id -Force -EA SilentlyContinue }
    if (Test-Path $ClaudeBak) { Move-Item $ClaudeBak $ClaudeCmd -Force -EA SilentlyContinue }
    Remove-Item Env:PERCH_ENABLE_TEST_IPC, Env:PERCH_TEST_SID, Env:PERCH_DATA_DIR, Env:CLAUDE_CONFIG_DIR -EA SilentlyContinue
}
