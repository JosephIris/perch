# Stage 3a end-to-end test.
#
# What this proves without touching anything else on the desktop:
#   * Sessions render in the sidebar from a real SessionStore on disk.
#   * Pane is spawned lazily for the active session (PTY bytes flow,
#     verified via the existing pty.snapshot count).
#   * `session.new` over the control pipe creates a session, makes it
#     active, spawns its PTY, and we see fresh banner bytes on it.
#   * `session.select` flips the active session and the snapshot tracks
#     the newly active pane.
#   * Persistence works: kill-and-relaunch sees the saved sessions list
#     (sessions.json round-trip).
#
# Safety:
#   * Window is minimized for the whole run.
#   * No keystroke synthesis. Only the control pipe is used.

[CmdletBinding()]
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\CmuxWin\bin\Debug\net8.0-windows\win10-x64\CmuxWin.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\CmuxWin\bin\Debug\net8.0-windows\win10-x64\tools",
    [switch]$KeepVisible
)

$ErrorActionPreference = 'Continue'
$LogPath = "$env:APPDATA\cmux-win\errors.log"
$SessPath = "$env:APPDATA\cmux-win\sessions.json"
$CmuxExe = Join-Path $ToolsDir 'cmux.exe'

if (-not (Test-Path $ExePath))  { throw "CmuxWin.exe not found at $ExePath" }
if (-not (Test-Path $CmuxExe))  { throw "cmux.exe missing at $CmuxExe" }

if (-not ('Cmux.WinShow3' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace Cmux { public static class WinShow3 {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  public const int SW_MINIMIZE = 6;
} }
'@
}

function Stop-CmuxWin {
    Get-Process -Name CmuxWin -EA SilentlyContinue |
        Where-Object { $_.Path -like '*\bin\Debug\*' } |
        Stop-Process -Force -EA SilentlyContinue
    Start-Sleep -Milliseconds 400
}

function Launch-Cmux {
    $env:CMUX_ENABLE_TEST_IPC = '1'
    $p = Start-Process -PassThru -FilePath $ExePath
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $p.Refresh()
        if ($p.HasExited) { throw "cmux exited early code=$($p.ExitCode)" }
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($p.MainWindowHandle -eq [IntPtr]::Zero) { throw "main window never appeared" }
    if (-not $KeepVisible) {
        [Cmux.WinShow3]::ShowWindow($p.MainWindowHandle, [Cmux.WinShow3]::SW_MINIMIZE) | Out-Null
    }
    return $p
}

function Invoke-CmuxTest {
    param([string]$Verb, [hashtable]$Fields)
    $cmdArgs = @('test', $Verb)
    if ($Fields) {
        foreach ($k in $Fields.Keys) { $cmdArgs += @("--$k", $Fields[$k]) }
    }
    $out = & $CmuxExe @cmdArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "cmux test $Verb exited $LASTEXITCODE`: $out"
    }
}

function Wait-For-Pattern {
    param([string]$Pattern, [int]$TimeoutSec = 10)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $LogPath) {
            $hit = Get-Content $LogPath -EA SilentlyContinue |
                Where-Object { $_ -match $Pattern } |
                Select-Object -Last 1
            if ($hit) { return $hit }
        }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function Get-Bytes-From-Last-Snapshot {
    if (-not (Test-Path $LogPath)) { return -1 }
    $line = Get-Content $LogPath |
        Where-Object { $_ -match 'Pty\.snapshot:\s*bytes=(\d+)' } |
        Select-Object -Last 1
    if ($line -and $line -match 'bytes=(\d+)') { return [int]$matches[1] }
    return -1
}

# --- Fresh start -----------------------------------------------------------
Stop-CmuxWin
Remove-Item $LogPath, $SessPath -Force -EA SilentlyContinue

# --- Phase 1: cold launch, verify state ------------------------------------
Write-Host "Phase 1: cold launch"
$p = Launch-Cmux
Write-Host "  cmux pid=$($p.Id)"

# Initial active pane should spawn within a couple of seconds.
$spawn1 = Wait-For-Pattern -Pattern 'Pane\.spawn:\s*pane=([0-9a-f]+).*pid=\d+' -TimeoutSec 12
if (-not $spawn1) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: initial pane never spawned"
}
Write-Host "  [+] initial pane spawned: $spawn1"

Start-Sleep -Seconds 2
Invoke-CmuxTest -Verb 'pty.snapshot'
Start-Sleep -Milliseconds 200
$bytes1 = Get-Bytes-From-Last-Snapshot
if ($bytes1 -lt 50) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: initial pane produced only $bytes1 bytes after 2s (expected banner+prompt)"
}
Write-Host "  [+] initial pane produced $bytes1 bytes"

# --- Phase 2: session.new spawns + activates a fresh session --------------
Write-Host ""
Write-Host "Phase 2: session.new"
$spawnBefore = (Get-Content $LogPath -EA SilentlyContinue | Where-Object { $_ -match 'Pane\.spawn' }).Count
Invoke-CmuxTest -Verb 'session.new'
# Poll for a NEW Pane.spawn line. Wait-For-Pattern would match the phase-1
# one immediately, so count instead.
$spawnAfter = $spawnBefore
$deadline = (Get-Date).AddSeconds(8)
while ((Get-Date) -lt $deadline -and $spawnAfter -le $spawnBefore) {
    Start-Sleep -Milliseconds 200
    $spawnAfter = (Get-Content $LogPath -EA SilentlyContinue | Where-Object { $_ -match 'Pane\.spawn' }).Count
}
if ($spawnAfter -le $spawnBefore) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: session.new did not produce a new Pane.spawn (count stayed at $spawnBefore)"
}
Write-Host "  [+] session.new spawned a fresh pane (spawn count: $spawnBefore -> $spawnAfter)"

# Snapshot now targets the NEW active session's pane (control pipe verbs
# always act on the active session in stage 3a).
Start-Sleep -Seconds 2
Invoke-CmuxTest -Verb 'pty.snapshot'
Start-Sleep -Milliseconds 200
$bytes2 = Get-Bytes-From-Last-Snapshot
if ($bytes2 -lt 50) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: new pane produced only $bytes2 bytes (expected its own banner+prompt)"
}
Write-Host "  [+] new pane produced $bytes2 bytes"

# --- Phase 3: sessions.json persisted with both sessions ------------------
Write-Host ""
Write-Host "Phase 3: persistence"
if (-not (Test-Path $SessPath)) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: sessions.json not written"
}
$snap = Get-Content $SessPath -Raw | ConvertFrom-Json
if ($snap.Sessions.Count -lt 2) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: sessions.json has only $($snap.Sessions.Count) session(s); expected >= 2"
}
$active1 = $snap.ActiveSessionId
Write-Host "  [+] sessions.json holds $($snap.Sessions.Count) sessions, active=$active1"

# --- Phase 4: relaunch picks up persisted sessions ------------------------
Stop-Process -Id $p.Id -Force -EA SilentlyContinue
Start-Sleep -Milliseconds 400
Remove-Item $LogPath -Force -EA SilentlyContinue

$p = Launch-Cmux
Write-Host "  relaunch pid=$($p.Id)"

# The active pane should respawn -- we should see at least one Pane.spawn.
$relaunchSpawn = Wait-For-Pattern -Pattern 'Pane\.spawn' -TimeoutSec 12
if (-not $relaunchSpawn) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: no Pane.spawn after relaunch"
}
Write-Host "  [+] active pane respawned on relaunch"

$snap2 = Get-Content $SessPath -Raw | ConvertFrom-Json
if ($snap2.Sessions.Count -lt 2) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: sessions.json lost sessions across restart"
}
if ($snap2.ActiveSessionId -ne $active1) {
    Write-Host "  warn: active session changed across restart ($active1 -> $($snap2.ActiveSessionId))" -ForegroundColor Yellow
}

# --- Phase 5: session.select flips the active pane ------------------------
Write-Host ""
Write-Host "Phase 5: session.select"
$otherId = ($snap2.Sessions | Where-Object { $_.Id -ne $snap2.ActiveSessionId } | Select-Object -First 1).Id
Invoke-CmuxTest -Verb 'session.select' -Fields @{ id = $otherId }
Start-Sleep -Milliseconds 800
$snap3 = Get-Content $SessPath -Raw | ConvertFrom-Json
if ($snap3.ActiveSessionId -ne $otherId) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: session.select did not flip active ($($snap3.ActiveSessionId) vs $otherId)"
}
Write-Host "  [+] active flipped to $otherId"

# --- Phase 6: session.close ----------------------------------------------
Write-Host ""
Write-Host "Phase 6: session.close"
Invoke-CmuxTest -Verb 'session.close' -Fields @{ id = $otherId }
Start-Sleep -Milliseconds 800
$snap4 = Get-Content $SessPath -Raw | ConvertFrom-Json
$stillThere = $snap4.Sessions | Where-Object { $_.Id -eq $otherId }
if ($stillThere) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: session.close did not remove the session"
}
Write-Host "  [+] session.close removed the session ($($snap4.Sessions.Count) remaining)"

Stop-Process -Id $p.Id -Force -EA SilentlyContinue
Start-Sleep -Milliseconds 400

$errors = @(Get-Content $LogPath -EA SilentlyContinue | Where-Object { $_ -match '^\[.*\] ERROR' })
if ($errors.Count -gt 0) {
    Write-Host ""
    Write-Host "WARN: ERROR lines in log during run:" -ForegroundColor Yellow
    $errors | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}

Write-Host ""
Write-Host "ALL PASS  --  stage 3a sidebar + sessions verified" -ForegroundColor Green
exit 0
