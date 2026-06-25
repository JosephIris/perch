# End-to-end test against the *real* Claude Code CLI. Verifies that:
#   * Our claude.cmd wrapper resolves real claude on PATH (skipping itself)
#   * Real claude reads --settings <file> and honors the hooks JSON we inject
#   * SessionStart hook actually fires on claude startup and routes back to
#     the live perch app's IPC layer
#
# Strategy: spawn a new perch pane whose shell command IS `claude` (interactive
# mode). The wrapper finds real claude, injects --settings, real claude boots
# its TUI and fires SessionStart, our `perch hooks claude session-start` hook
# runs inside the same pane (PERCH_PIPE inherited), and the host's PerchIpc
# server logs the resulting `status` dispatch. We then read errors.log and
# count status events whose timestamp falls AFTER the spawn moment.
#
# Cost: $0. We never type a prompt -- only claude startup fires.
#
# Skipped if real claude is not on PATH (e.g. CI machines without an npm
# install).

[CmdletBinding()]
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools",
    [int]   $WaitSeconds = 15,
    [switch]$KeepVisible
)

$ErrorActionPreference = 'Continue'
$PerchExe = Join-Path $ToolsDir 'perch.exe'
$LogPath = "$env:APPDATA\perch\errors.log"

if (-not (Test-Path $ExePath))  { throw "Perch.exe not found at $ExePath" }
if (-not (Test-Path $PerchExe))  { throw "perch.exe missing at $PerchExe" }

# --- Resolve real claude, skipping any perch-staged wrapper -------------------
$realClaude = $null
foreach ($dir in ($env:PATH -split ';')) {
    if ([string]::IsNullOrWhiteSpace($dir)) { continue }
    $resolved = $null
    try { $resolved = (Resolve-Path $dir.Trim() -EA SilentlyContinue).Path } catch { }
    if (-not $resolved) { continue }
    if ($resolved -like "*$ToolsDir*") { continue }
    foreach ($ext in '.cmd','.exe','.bat') {
        $candidate = Join-Path $resolved "claude$ext"
        if (Test-Path $candidate) { $realClaude = $candidate; break }
    }
    if ($realClaude) { break }
}
if (-not $realClaude) {
    Write-Host "Real claude not on PATH -- skipping (test is opt-in)." -ForegroundColor Yellow
    exit 0
}
Write-Host "Real claude:   $realClaude"
Write-Host "Perch exe:      $ExePath"
Write-Host "Perch CLI:      $PerchExe"

# --- Reset state -------------------------------------------------------------
Get-Process -Name Perch -EA SilentlyContinue |
    Where-Object { $_.Path -like '*\bin\Debug\*' } |
    Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Milliseconds 400
Remove-Item "$env:APPDATA\perch\sessions.json" -Force -EA SilentlyContinue
Remove-Item $LogPath -Force -EA SilentlyContinue

# --- Launch with test IPC enabled -------------------------------------------
$env:PERCH_ENABLE_TEST_IPC = '1'
$p = Start-Process -PassThru -FilePath $ExePath
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline) {
    $p.Refresh()
    if ($p.HasExited) { throw "Perch exited early $($p.ExitCode)" }
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 200
}
if ($p.MainWindowHandle -eq [IntPtr]::Zero) { throw "main window never appeared" }
Write-Host "Launched perch pid=$($p.Id)"

if (-not $KeepVisible) {
    if (-not ('Perch.W2' -as [type])) {
        Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace Perch { public static class W2 {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
} }
'@
    }
    [Perch.W2]::ShowWindow($p.MainWindowHandle, 6) | Out-Null   # SW_MINIMIZE
}

# Wait for sessions.json + origin pane IPC server to spin up.
$sessionsPath = "$env:APPDATA\perch\sessions.json"
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline) {
    if (Test-Path $sessionsPath) {
        $raw = Get-Content $sessionsPath -Raw -EA SilentlyContinue
        if ($raw -and $raw -match 'ActiveSessionId') { break }
    }
    Start-Sleep -Milliseconds 200
}
$sessions = Get-Content $sessionsPath -Raw | ConvertFrom-Json
$active = $sessions.Sessions | Where-Object { $_.Id -eq $sessions.ActiveSessionId } | Select-Object -First 1
if (-not $active) { throw "no active session in sessions.json" }
$originPaneId = $active.Root.Id -replace '-', ''
$originPipe = "\\.\pipe\perch\$originPaneId"
Write-Host "Origin pipe:   $originPipe"

# Wait for the per-pane IPC pipe -- `perch open` from outside connects to this.
$deadline = (Get-Date).AddSeconds(5)
while ((Get-Date) -lt $deadline) {
    if (Test-Path $originPipe) { break }
    Start-Sleep -Milliseconds 150
}

# --- Drive `claude` inside the default pane ---------------------------------
# The default pane's shell already has PERCH_PIPE in its env (the shell
# startup wrapper injects it for pwsh/cmd/wsl). Sending "claude\r" into the
# pane's PTY is the same thing the user does manually: pwsh resolves
# `claude` via PATH, our tools/claude.cmd wrapper wins (PATH-prepended at
# app launch), the wrapper finds real claude and execs it with --settings.
#
# We use `perch send` over the origin pane's IPC pipe to push bytes verbatim
# into the PTY -- no keystroke synthesis, no foreground dependency.
$kickoff = Get-Date
Write-Host "Kickoff at $($kickoff.ToString('HH:mm:ss.fff'))"

$psi = [System.Diagnostics.ProcessStartInfo]::new($PerchExe)
$psi.Arguments = "send pane-1 claude`r"
$psi.UseShellExecute = $false
$psi.RedirectStandardError = $true
$psi.EnvironmentVariables['PERCH_PIPE'] = $originPipe
$proc = [System.Diagnostics.Process]::Start($psi)
if (-not $proc.WaitForExit(5000)) { $proc.Kill(); throw "perch send hung" }
$sendErr = $proc.StandardError.ReadToEnd()
if ($proc.ExitCode -ne 0) { throw "perch send exited $($proc.ExitCode): $sendErr" }
Write-Host "  -> 'claude\r' written into pane-1's PTY"

# claude takes a few seconds to boot the TUI and fire SessionStart.
Write-Host "Waiting ${WaitSeconds}s for SessionStart hook to fire and round-trip..."
Start-Sleep -Seconds $WaitSeconds

# --- Inspect log ------------------------------------------------------------
$lines = if (Test-Path $LogPath) { Get-Content $LogPath } else { @() }

# Only count events that arrived AFTER kickoff (origin pane chatter may
# have produced earlier ones).
$kickoffStamp = $kickoff.ToString('HH:mm:ss.fff')
function After-Kickoff([string]$line) {
    if ($line -notmatch '^\[(\d\d:\d\d:\d\d\.\d\d\d)\]') { return $false }
    return ($matches[1] -ge $kickoffStamp)
}

$afterKickoff = $lines | Where-Object { After-Kickoff $_ }
$ipcLines = $afterKickoff | Where-Object { $_ -match 'PerchIpc\.recv|ERROR' }

Write-Host ""
Write-Host "=== Log lines after kickoff (PerchIpc + errors) ==="
if ($ipcLines.Count -eq 0) { Write-Host "  (none)" }
else { $ipcLines | ForEach-Object { Write-Host "  $_" } }

$statusEvents = $ipcLines | Where-Object { $_ -match 'type=status' }
$statusCount = $statusEvents.Count

# --- Cleanup ----------------------------------------------------------------
Stop-Process -Id $p.Id -Force -EA SilentlyContinue

Write-Host ""
Write-Host "=== Result ==="
Write-Host "  status verbs after kickoff: $statusCount"
if ($statusCount -ge 1) {
    Write-Host "PASS: real claude fired at least one hook and the host received it." -ForegroundColor Green
    exit 0
}
Write-Host "FAIL: no hook arrived from real claude." -ForegroundColor Red
Write-Host "  Possible causes:"
Write-Host "  - claude not yet authenticated (run 'claude' manually once first)"
Write-Host "  - claude version doesn't honor --settings <file-path>"
Write-Host "  - PATH inside the spawned pane doesn't include real claude"
Write-Host "  - hooks JSON path not readable by claude"
exit 1
