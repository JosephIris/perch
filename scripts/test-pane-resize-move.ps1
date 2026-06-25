# End-to-end test for pane RESIZE (split weights) and MOVE (rearrange).
#
# Drives the host tree surgery through the control pipe and asserts the
# resulting tree shape + weights straight from the persisted sessions.json.
# This covers the bug-prone C# tree mutation (OnPaneResizeSplit / OnPaneMove /
# InsertBesideImpl / SwapNodes / CloseAndCollapse) without needing to simulate
# mouse drags in the WebView2.
#
# ISOLATION: runs against a throwaway PERCH_DATA_DIR so it never reads or
# writes the user's real session store, and only ever kills Perch.exe whose
# path is under bin\Debug (its own instance) — never the installed prod app in
# %APPDATA%\perch.

[CmdletBinding()]
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools",
    [string]$DataDir  = "$env:TEMP\perch-test-resize-move",
    [switch]$KeepVisible
)

$ErrorActionPreference = 'Continue'
$PerchExe = Join-Path $ToolsDir 'perch.exe'
$LogPath  = Join-Path $DataDir 'perch\errors.log'
$SessPath = Join-Path $DataDir 'perch\sessions.json'

if (-not (Test-Path $ExePath))  { throw "Perch.exe not found at $ExePath" }
if (-not (Test-Path $PerchExe)) { throw "perch.exe missing at $PerchExe" }

if (-not ('Perch.WinShowRM' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace Perch { public static class WinShowRM {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  public const int SW_MINIMIZE = 6;
} }
'@
}

function Stop-MyPerch {
    # ONLY our own bin\Debug instance — never the installed prod app.
    Get-Process -Name Perch -EA SilentlyContinue |
        Where-Object { $_.Path -like '*\bin\Debug\*' } |
        Stop-Process -Force -EA SilentlyContinue
    Start-Sleep -Milliseconds 400
}

function Launch-Perch {
    $env:PERCH_ENABLE_TEST_IPC = '1'
    $env:PERCH_DATA_DIR        = $DataDir
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
        [Perch.WinShowRM]::ShowWindow($p.MainWindowHandle, [Perch.WinShowRM]::SW_MINIMIZE) | Out-Null
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

function Get-Root { (Get-Content $SessPath -Raw | ConvertFrom-Json).Sessions[0].Root }

function Count-Leaves {
    param($Node)
    if ($null -eq $Node.Split) { return 1 }
    $sum = 0; foreach ($c in $Node.Children) { $sum += Count-Leaves -Node $c }
    return $sum
}

function Wait-Until {
    param([scriptblock]$Cond, [int]$TimeoutSec = 8)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try { if (& $Cond) { return $true } } catch { }
        Start-Sleep -Milliseconds 150
    }
    return $false
}

function Fail { param($p, $msg) Stop-Process -Id $p.Id -Force -EA SilentlyContinue; throw "FAIL: $msg" }

# --- Fresh, isolated start --------------------------------------------------
Stop-MyPerch
Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

Write-Host "Phase 1: cold launch (isolated data dir: $DataDir)"
$p = Launch-Perch
Write-Host "  perch pid=$($p.Id)"
# Wait for the initial Pane.spawn — that only fires after the page is ready and
# the host has set the active pane, so split-active in Phase 2 won't no-op on a
# null active id (a warmup race).
function Spawn-Count { (Get-Content $LogPath -EA SilentlyContinue | Where-Object { $_ -match 'Pane\.spawn' }).Count }
if (-not (Wait-Until { (Spawn-Count) -ge 1 } 20)) { Fail $p "initial pane never spawned" }
if (-not (Wait-Until { (Test-Path $SessPath) -and (Count-Leaves (Get-Root)) -ge 1 } 5)) {
    Fail $p "initial single-leaf session never appeared"
}
Write-Host "  [+] initial leaf spawned"

# --- Phase 2: split-right -> 2 leaves under a Vertical split -----------------
Write-Host "`nPhase 2: split-active right -> vertical split, 2 leaves"
Invoke-PerchTest -Verb 'pane.split-active' -Fields @{ dir = 'right' }
if (-not (Wait-Until { (Count-Leaves (Get-Root)) -eq 2 })) { Fail $p "split did not yield 2 leaves" }
$root = Get-Root
if ($root.Split -ne 1) { Fail $p "root.Split expected Vertical(1), got $($root.Split)" }
$splitId = $root.Id
$L0 = $root.Children[0].Id
$L1 = $root.Children[1].Id
Write-Host "  [+] vertical split id=$splitId  L0=$L0  L1=$L1"
# Default weights must both be 1.0 (even — no behavior change from before).
if ($root.Children[0].Weight -ne 1 -or $root.Children[1].Weight -ne 1) {
    Fail $p "default weights expected 1/1, got $($root.Children[0].Weight)/$($root.Children[1].Weight)"
}
Write-Host "  [+] default weights are 1.0 / 1.0"

# --- Phase 3: resize the split (weights 2 : 1) ------------------------------
Write-Host "`nPhase 3: resize-split weights 2,1"
Invoke-PerchTest -Verb 'pane.resize-split' -Fields @{ splitId = $splitId; weights = '2,1' }
if (-not (Wait-Until { $r = Get-Root; $r.Children[0].Weight -eq 2 -and $r.Children[1].Weight -eq 1 })) {
    $r = Get-Root
    Fail $p "weights after resize expected 2/1, got $($r.Children[0].Weight)/$($r.Children[1].Weight)"
}
Write-Host "  [+] children weights are now 2.0 / 1.0"

# --- Phase 4: move center -> swap the two panes (weights travel) ------------
Write-Host "`nPhase 4: move center (swap L0 <-> L1)"
Invoke-PerchTest -Verb 'pane.move' -Fields @{ src = $L0; target = $L1; edge = 'center' }
if (-not (Wait-Until { (Get-Root).Children[0].Id -eq $L1 })) { Fail $p "center move did not swap order" }
$root = Get-Root
if ($root.Children[0].Id -ne $L1 -or $root.Children[1].Id -ne $L0) {
    Fail $p "swap order wrong: [$($root.Children[0].Id),$($root.Children[1].Id)]"
}
# The swapped nodes carry their own weights with them: slot0 is now L1(was 1),
# slot1 is now L0(was 2).
if ($root.Children[0].Weight -ne 1 -or $root.Children[1].Weight -ne 2) {
    Fail $p "weights didn't travel with swap: $($root.Children[0].Weight)/$($root.Children[1].Weight)"
}
if ((Count-Leaves $root) -ne 2) { Fail $p "swap changed leaf count" }
Write-Host "  [+] order swapped and weights traveled (L1@1, L0@2)"

# --- Phase 5: move to an edge -> collapse + re-split, weights reset ----------
# Tree is [L1, L0] vertical. Move L1 to the BOTTOM of L0:
#   detach L1 -> root collapses to L0 leaf -> insert L1 below L0
#   => Horizontal split [L0, L1], both weights reset to 1.
Write-Host "`nPhase 5: move L1 to bottom of L0 (collapse + horizontal re-split)"
Invoke-PerchTest -Verb 'pane.move' -Fields @{ src = $L1; target = $L0; edge = 'bottom' }
if (-not (Wait-Until { $r = Get-Root; $r.Split -eq 0 -and (Count-Leaves $r) -eq 2 })) {
    $r = Get-Root
    Fail $p "expected horizontal split with 2 leaves, got Split=$($r.Split) leaves=$(Count-Leaves $r)"
}
$root = Get-Root
if ($root.Children[0].Id -ne $L0 -or $root.Children[1].Id -ne $L1) {
    Fail $p "edge-move order wrong: [$($root.Children[0].Id),$($root.Children[1].Id)] (expected L0,L1)"
}
if ($root.Children[0].Weight -ne 1 -or $root.Children[1].Weight -ne 1) {
    Fail $p "edge-move should reset weights to 1/1, got $($root.Children[0].Weight)/$($root.Children[1].Weight)"
}
Write-Host "  [+] horizontal split [L0, L1], weights reset to 1.0 / 1.0"

# --- Phase 6: keyboard move-dir (reorder within the split) ------------------
# Tree is horizontal [L0, L1]. move-dir is the Ctrl+Shift+arrows path.
Write-Host "`nPhase 6: keyboard move-dir"
# UP swaps L1 above L0 -> [L1, L0].
Invoke-PerchTest -Verb 'pane.move-dir' -Fields @{ paneId = $L1; dir = 'up' }
if (-not (Wait-Until { (Get-Root).Children[0].Id -eq $L1 })) { Fail $p "move-dir up did not reorder" }
Write-Host "  [+] up -> [L1, L0]"
# A perpendicular direction (left, on a horizontal split) is a no-op.
Invoke-PerchTest -Verb 'pane.move-dir' -Fields @{ paneId = $L1; dir = 'left' }
Start-Sleep -Milliseconds 400
if ((Get-Root).Children[0].Id -ne $L1) { Fail $p "perpendicular move-dir should be a no-op" }
Write-Host "  [+] left (perpendicular) is a no-op"
# DOWN swaps back -> [L0, L1].
Invoke-PerchTest -Verb 'pane.move-dir' -Fields @{ paneId = $L1; dir = 'down' }
if (-not (Wait-Until { (Get-Root).Children[0].Id -eq $L0 })) { Fail $p "move-dir down did not reorder back" }
Write-Host "  [+] down -> [L0, L1]"

# --- Wrap up ----------------------------------------------------------------
Stop-Process -Id $p.Id -Force -EA SilentlyContinue
Start-Sleep -Milliseconds 400

$errors = @(Get-Content $LogPath -EA SilentlyContinue | Where-Object { $_ -match '^\[.*\] ERROR' })
if ($errors.Count -gt 0) {
    Write-Host "`nWARN: ERROR lines in log:" -ForegroundColor Yellow
    $errors | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}

Write-Host "`nALL PASS  --  pane resize + move tree surgery verified" -ForegroundColor Green
exit 0
