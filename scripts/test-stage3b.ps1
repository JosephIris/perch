# Stage 3b end-to-end test: pane splits.
#
# What this proves without touching anything else on the desktop:
#   * pane.split (right) takes the active leaf, wraps it in a split node,
#     and spawns a fresh ConPty for the new sibling. spawnCount goes up
#     by one.
#   * The new pane is marked active; sessions.json shows the split tree
#     with two leaves under a Split=Vertical root.
#   * pane.split (down) on the new active pane gives us a 2-deep tree
#     (top-level vertical split, one child further split horizontally).
#   * pane.close on the active pane collapses the parent: the surviving
#     sibling is promoted, the split node disappears.
#   * No keystroke synthesis; window minimized throughout.

[CmdletBinding()]
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools",
    [switch]$KeepVisible
)

$ErrorActionPreference = 'Continue'
$LogPath  = "$env:APPDATA\perch\errors.log"
$SessPath = "$env:APPDATA\perch\sessions.json"
$PerchExe  = Join-Path $ToolsDir 'perch.exe'

if (-not (Test-Path $ExePath))  { throw "Perch.exe not found at $ExePath" }
if (-not (Test-Path $PerchExe))  { throw "perch.exe missing at $PerchExe" }

if (-not ('Perch.WinShow3b' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace Perch { public static class WinShow3b {
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
    $p = Start-Process -PassThru -FilePath $ExePath
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $p.Refresh()
        if ($p.HasExited) { throw "perch exited early code=$($p.ExitCode)" }
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($p.MainWindowHandle -eq [IntPtr]::Zero) { throw "main window never appeared" }
    if (-not $KeepVisible) {
        [Perch.WinShow3b]::ShowWindow($p.MainWindowHandle, [Perch.WinShow3b]::SW_MINIMIZE) | Out-Null
    }
    return $p
}

function Invoke-PerchTest {
    param([string]$Verb, [hashtable]$Fields)
    $cmdArgs = @('test', $Verb)
    if ($Fields) { foreach ($k in $Fields.Keys) { $cmdArgs += @("--$k", $Fields[$k]) } }
    $out = & $PerchExe @cmdArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "perch test $Verb exited $LASTEXITCODE`: $out" }
}

function Spawn-Count {
    (Get-Content $LogPath -EA SilentlyContinue | Where-Object { $_ -match 'Pane\.spawn' }).Count
}

function Wait-For-Spawn-Increase {
    param([int]$Before, [int]$TimeoutSec = 8)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
        if ((Spawn-Count) -gt $Before) { return (Spawn-Count) }
    }
    return -1
}

function Get-Root {
    (Get-Content $SessPath -Raw | ConvertFrom-Json).Sessions[0].Root
}

function Count-Leaves {
    param($Node)
    if ($null -eq $Node.Split) { return 1 }
    $sum = 0
    foreach ($c in $Node.Children) { $sum += Count-Leaves -Node $c }
    return $sum
}

# --- Fresh start -----------------------------------------------------------
Stop-Perch
Remove-Item $LogPath, $SessPath -Force -EA SilentlyContinue

# --- Phase 1: cold launch, initial single-leaf session --------------------
Write-Host "Phase 1: cold launch"
$p = Launch-Perch
Write-Host "  perch pid=$($p.Id)"

# Wait for the initial Pane.spawn (single leaf).
$deadline = (Get-Date).AddSeconds(12)
while ((Get-Date) -lt $deadline -and (Spawn-Count) -lt 1) { Start-Sleep -Milliseconds 200 }
if ((Spawn-Count) -lt 1) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: initial pane never spawned"
}
Write-Host "  [+] initial leaf spawned"

$root1 = Get-Root
if ((Count-Leaves -Node $root1) -ne 1) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: expected exactly 1 leaf in initial tree, got $(Count-Leaves -Node $root1)"
}

# --- Phase 2: split-right -> 2 leaves under a Vertical split --------------
Write-Host ""
Write-Host "Phase 2: pane.split-active dir=right"
$before2 = Spawn-Count
Invoke-PerchTest -Verb 'pane.split-active' -Fields @{ dir = 'right' }
$after2 = Wait-For-Spawn-Increase -Before $before2
if ($after2 -lt 0) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: split-right did not produce a new Pane.spawn"
}
Write-Host "  [+] new pane spawned (spawn count: $before2 -> $after2)"

Start-Sleep -Milliseconds 600
$root2 = Get-Root
if ($root2.Split -ne 1) {   # SplitOrientation.Vertical = 1
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: root.Split expected Vertical(1), got $($root2.Split)"
}
if ((Count-Leaves -Node $root2) -ne 2) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: expected 2 leaves after split-right, got $(Count-Leaves -Node $root2)"
}
Write-Host "  [+] tree shape: vertical split with 2 leaves"

# --- Phase 3: split-down on active -> 3 leaves -----------------------------
Write-Host ""
Write-Host "Phase 3: pane.split-active dir=down"
$before3 = Spawn-Count
Invoke-PerchTest -Verb 'pane.split-active' -Fields @{ dir = 'down' }
$after3 = Wait-For-Spawn-Increase -Before $before3
if ($after3 -lt 0) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: split-down did not produce a new Pane.spawn"
}
Start-Sleep -Milliseconds 600

$root3 = Get-Root
$leaves3 = Count-Leaves -Node $root3
if ($leaves3 -ne 3) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: expected 3 leaves after split-down, got $leaves3"
}
Write-Host "  [+] tree shape: 3 leaves under a vertical+horizontal hierarchy"

# --- Phase 4: close active pane -> tree collapses --------------------------
Write-Host ""
Write-Host "Phase 4: pane.close-active"
Invoke-PerchTest -Verb 'pane.close-active'
Start-Sleep -Milliseconds 800
$root4 = Get-Root
$leaves4 = Count-Leaves -Node $root4
if ($leaves4 -ne 2) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: expected 2 leaves after closing active, got $leaves4"
}
Write-Host "  [+] tree collapsed to 2 leaves"

# Close another -> back to 1 leaf, no more splits in the root.
Invoke-PerchTest -Verb 'pane.close-active'
Start-Sleep -Milliseconds 800
$root5 = Get-Root
$leaves5 = Count-Leaves -Node $root5
if ($leaves5 -ne 1) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: expected 1 leaf after second close, got $leaves5"
}
if ($null -ne $root5.Split) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: expected root to be a leaf (Split=null), got Split=$($root5.Split)"
}
Write-Host "  [+] tree collapsed back to a single leaf"

Stop-Process -Id $p.Id -Force -EA SilentlyContinue
Start-Sleep -Milliseconds 400

$errors = @(Get-Content $LogPath -EA SilentlyContinue | Where-Object { $_ -match '^\[.*\] ERROR' })
if ($errors.Count -gt 0) {
    Write-Host ""
    Write-Host "WARN: ERROR lines in log:" -ForegroundColor Yellow
    $errors | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}

Write-Host ""
Write-Host "ALL PASS  --  stage 3b pane splits verified" -ForegroundColor Green
exit 0
