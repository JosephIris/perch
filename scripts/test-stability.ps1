# Autonomous stability / monkey test.
#
# Drives the app through a long randomized sequence of NORMAL-USAGE
# operations -- create/switch/close sessions, split/close panes, type into
# panes, open URL (WebView2) panes, open/close settings, resize the window
# -- entirely through the control pipe (PERCH_ENABLE_TEST_IPC=1). The goal
# is to surface crashes that creep out of lifecycle races (ConPty teardown,
# WebView2 child create/dispose, close-to-empty then re-create, etc.).
#
# Crash detection, three independent signals:
#   1. process HasExited     -- the app never self-exits in these flows
#                              (closing the last session just empties the
#                              workspace), so any exit is a crash.
#   2. Windows Error Reporting -- .NET Runtime / Application Error events
#                              mentioning Perch since launch.
#   3. control-pipe hang     -- if an op stops getting through, the UI
#                              thread is wedged.
#
# Every operation is logged to a transcript so a crash is reproducible.
# The run is seeded (default seed 1) so the exact sequence repeats; pass
# -Seed to vary it. On crash the script prints the last ~30 ops + the WER
# message + the tail of errors.log.
#
# The window is parked OFF-SCREEN (not minimized) so layout / render /
# resize paths all run for real, but nothing churns on the visible desktop
# (honors the "keep test churn off screen" rule while still exercising the
# resize codepath, which minimizing would freeze).

[CmdletBinding()]
param(
    [string]$ExePath,
    [string]$ToolsDir,
    [int]$Iterations = 150,
    [int]$Seed = 1,
    [int]$MaxSessions = 6,
    [int]$MinDelayMs = 40,    # inter-op jitter floor
    [int]$MaxDelayMs = 130,   # inter-op jitter ceiling
    [switch]$Burst,           # near-zero delays to provoke lifecycle races
    [switch]$Visible          # debug: keep the window on-screen
)

# Burst mode: hammer ops back-to-back so split/close/dispose race against
# PTY spawn and WebView2 child teardown before the dispatcher settles.
if ($Burst) { $MinDelayMs = 2; $MaxDelayMs = 12 }

$ErrorActionPreference = 'Continue'

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot  = Resolve-Path (Join-Path $scriptDir '..')
if (-not $ExePath)  { $ExePath  = Join-Path $repoRoot 'src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe' }
if (-not $ToolsDir) { $ToolsDir = Join-Path $repoRoot 'src\Perch\bin\Debug\net8.0-windows\win10-x64\tools' }
$PerchExe = Join-Path $ToolsDir 'perch.exe'

if (-not (Test-Path $ExePath))  { throw "Perch.exe not found at $ExePath. Build first." }
if (-not (Test-Path $PerchExe))  { throw "perch.exe not found at $PerchExe. Build first." }

$SessionsPath = Join-Path $env:APPDATA 'perch\sessions.json'
$SettingsPath = Join-Path $env:APPDATA 'perch\settings.json'
$ErrorsPath   = Join-Path $env:APPDATA 'perch\errors.log'

# --- Win32 for off-screen parking + resize -------------------------------
if (-not ('Perch.Win' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace Perch {
  public static class Win {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(
      IntPtr hWnd, IntPtr after, int X, int Y, int cx, int cy, uint flags);
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
  }
}
'@
}

function Move-Window {
    param([System.Diagnostics.Process]$P, [int]$X, [int]$Y, [int]$W, [int]$H)
    if ($P.MainWindowHandle -eq [IntPtr]::Zero) { return }
    [Perch.Win]::SetWindowPos($P.MainWindowHandle, [IntPtr]::Zero, $X, $Y, $W, $H,
        ([Perch.Win]::SWP_NOZORDER -bor [Perch.Win]::SWP_NOACTIVATE)) | Out-Null
}

function Stop-Perch {
    Get-Process -Name Perch -EA SilentlyContinue |
        Where-Object { $_.Path -like '*\bin\Debug\*' } |
        Stop-Process -Force -EA SilentlyContinue
    Start-Sleep -Milliseconds 400
}

function Reset-PerchState {
    Remove-Item $SessionsPath -Force -EA SilentlyContinue
    Remove-Item $SettingsPath -Force -EA SilentlyContinue
    Remove-Item $ErrorsPath   -Force -EA SilentlyContinue
}

function Start-PerchForTest {
    $env:PERCH_ENABLE_TEST_IPC = '1'
    $p = Start-Process -PassThru -FilePath $ExePath
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $p.Refresh()
        if ($p.HasExited) { throw "Perch exited early with $($p.ExitCode)" }
        if ($p.MainWindowHandle -ne 0) { return $p }
        Start-Sleep -Milliseconds 200
    }
    throw "Perch main window never appeared"
}

function Wait-ForControlPipe {
    param([int]$TimeoutSec = 8)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path '\\.\pipe\perch\control') { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

# Returns $true if the verb got through, $false if the pipe didn't accept
# it (treated as a hang signal by the caller).
function Send-Verb {
    param([string]$Verb, [hashtable]$Flags = @{})
    $argList = @('test', $Verb)
    foreach ($k in $Flags.Keys) { $argList += "--$k"; $argList += [string]$Flags[$k] }
    & $PerchExe @argList *> $null
    return ($LASTEXITCODE -eq 0)
}

function Get-Store {
    if (-not (Test-Path $SessionsPath)) { return $null }
    try { return (Get-Content $SessionsPath -Raw | ConvertFrom-Json) } catch { return $null }
}

function Get-SessionIds {
    $store = Get-Store
    if (-not $store -or -not $store.Sessions) { return @() }
    return @($store.Sessions | ForEach-Object { $_.Id })
}

function Get-WerSince {
    param([datetime]$Start)
    return @(Get-WinEvent -FilterHashtable @{
        LogName='Application'
        ProviderName='.NET Runtime','Application Error','Windows Error Reporting'
        StartTime=$Start
    } -EA SilentlyContinue | Where-Object { $_.Message -match 'Perch' })
}

# --- Weighted operation table --------------------------------------------
# Each op is a scriptblock returning a short label string (for the
# transcript) and performing the action. They close over $proc / $rng.
$rng = [System.Random]::new($Seed)

function Pick-Weighted {
    param([array]$Table)  # array of @{ w=int; op=string }
    # Sum with a loop, not Measure-Object -Property: hashtable keys are not
    # surfaced as object properties, so Measure-Object can't see `w`.
    $total = 0
    foreach ($e in $Table) { $total += [int]$e.w }
    $roll = $rng.Next(0, $total)
    $acc = 0
    foreach ($e in $Table) { $acc += [int]$e.w; if ($roll -lt $acc) { return $e.op } }
    return $Table[-1].op
}

$opTable = @(
    @{ w = 18; op = 'split-right' }
    @{ w = 18; op = 'split-down' }
    @{ w = 16; op = 'close-pane' }
    @{ w = 10; op = 'new-session' }
    @{ w = 10; op = 'select-session' }
    @{ w = 6;  op = 'close-session' }
    @{ w = 12; op = 'type' }
    @{ w = 6;  op = 'url-pane' }
    @{ w = 4;  op = 'settings' }
    @{ w = 6;  op = 'resize' }
)

$typeSamples = @(
    "echo stability`r",
    "dir`r",
    "1..50 | ForEach-Object { `"line `$_`" }`r",
    "Get-Date`r",
    "cls`r",
    "`r",
    "echo done`r"
)

# ==========================================================================
Write-Host ""
Write-Host "==== Stability monkey: $Iterations ops, seed $Seed ===="
Stop-Perch; Reset-PerchState
$startTime = Get-Date
$proc = Start-PerchForTest
if (-not (Wait-ForControlPipe)) { throw "control pipe never appeared" }
if ($Visible) {
    Move-Window -P $proc -X 80 -Y 80 -W 1280 -H 820
} else {
    Move-Window -P $proc -X -3400 -Y -3400 -W 1280 -H 820
}
Start-Sleep -Seconds 2   # let the first pane spawn

$transcript = New-Object System.Collections.ArrayList
$opCounts = @{}
$crash = $null
$hangOp = $null
$i = 0

while ($i -lt $Iterations) {
    $i++
    $proc.Refresh()
    if ($proc.HasExited) { $crash = "process exited (code $($proc.ExitCode)) after op $($i-1)"; break }

    # @(...) guards against PowerShell unrolling a single-element array
    # back to a scalar string on return (which made indexing yield a char).
    $sids = @(Get-SessionIds)
    $op = Pick-Weighted -Table $opTable

    # Guard: if no sessions exist, the only sensible op is to make one.
    if ($sids.Count -eq 0) { $op = 'new-session' }
    # Guard: cap session sprawl so we test churn, not resource exhaustion.
    if ($op -eq 'new-session' -and $sids.Count -ge $MaxSessions) { $op = 'select-session' }

    $label = $op
    $ok = $true
    switch ($op) {
        'split-right'    { $ok = Send-Verb 'split-right' }
        'split-down'     { $ok = Send-Verb 'split-down' }
        'close-pane'     { $ok = Send-Verb 'close-active-pane' }
        'new-session'    {
            # Vary the shell so we exercise mixed cmd / powershell / pwsh
            # ConPTY backends in one app instance (the original cursor bug
            # surfaced with a cmd + pwsh pair).
            $shells = @('', 'cmd.exe', 'powershell.exe')
            $shell = $shells[$rng.Next(0, $shells.Count)]
            if ($shell) {
                $label = "new-session $shell"
                $ok = Send-Verb 'session.new' @{ shell = $shell }
            } else {
                $ok = Send-Verb 'session.new'
            }
        }
        'select-session' {
            if ($sids.Count -gt 0) {
                $id = $sids[$rng.Next(0, $sids.Count)]
                $label = "select-session $($id.Substring(0,8))"
                $ok = Send-Verb 'session.select' @{ id = $id }
            }
        }
        'close-session'  {
            if ($sids.Count -gt 0) {
                $id = $sids[$rng.Next(0, $sids.Count)]
                $label = "close-session $($id.Substring(0,8))"
                $ok = Send-Verb 'session.close' @{ id = $id }
            }
        }
        'type'           {
            $txt = $typeSamples[$rng.Next(0, $typeSamples.Count)]
            $label = "type '$($txt.TrimEnd("`r"))'"
            $ok = Send-Verb 'pty.send' @{ text = $txt }
        }
        'url-pane'       {
            $dir = if ($rng.Next(0,2) -eq 0) { 'right' } else { 'down' }
            $label = "url-pane $dir"
            $ok = Send-Verb 'pane.split-active' @{ dir = $dir; url = 'about:blank' }
        }
        'settings'       {
            $ok = (Send-Verb 'ui.open-settings')
            Start-Sleep -Milliseconds 120
            # Save with a random font size to drive the persistence path too.
            $null = Send-Verb 'settings.save' @{ fontSize = (12 + $rng.Next(0,8)) }
            $label = 'settings open+save'
        }
        'resize'         {
            $w = 760 + $rng.Next(0, 700)
            $h = 520 + $rng.Next(0, 420)
            $x = if ($Visible) { 80 } else { -3400 }
            $y = if ($Visible) { 80 } else { -3400 }
            Move-Window -P $proc -X $x -Y $y -W $w -H $h
            $label = "resize ${w}x${h}"
        }
    }

    [void]$transcript.Add(("{0,4}  {1}" -f $i, $label))
    if ($opCounts.ContainsKey($op)) { $opCounts[$op]++ } else { $opCounts[$op] = 1 }

    if (-not $ok) {
        # A verb that didn't get through after the pipe was up = likely a
        # wedged UI thread. Confirm with a lightweight follow-up.
        Start-Sleep -Milliseconds 300
        if (-not (Send-Verb 'state.dump')) { $hangOp = $label; break }
    }

    # Small jitter so we interleave at realistic-ish speed and give the
    # dispatcher time to process (and the page to relayout / spawn PTYs).
    Start-Sleep -Milliseconds ($rng.Next($MinDelayMs, $MaxDelayMs + 1))

    # Periodic mid-run crash probe (every 25 ops) so we catch a crash near
    # the op that caused it, not just at the end.
    if (($i % 25) -eq 0) {
        $proc.Refresh()
        if ($proc.HasExited) { $crash = "process exited (code $($proc.ExitCode)) at op $i"; break }
        Write-Host ("  ... {0}/{1} ops, sessions={2}" -f $i, $Iterations, @(Get-SessionIds).Count)
    }
}

# --- Final crash assessment ----------------------------------------------
Start-Sleep -Milliseconds 600
$proc.Refresh()
if (-not $crash -and $proc.HasExited) { $crash = "process exited (code $($proc.ExitCode)) post-run" }
$wer = Get-WerSince -Start $startTime
$alive = -not $proc.HasExited

if ($alive) { Stop-Process -Id $proc.Id -Force -EA SilentlyContinue }

# Fourth signal: handler exceptions are caught + logged (Log.Error) rather
# than crashing the app, so a per-op failure would otherwise hide behind a
# "survived" verdict. errors.log was reset at launch, so any ERROR line is
# from this run. These are bugs even when non-fatal.
$errLines = @()
if (Test-Path $ErrorsPath) {
    $errLines = @(Select-String -Path $ErrorsPath -Pattern 'ERROR' -SimpleMatch -EA SilentlyContinue)
}

# ==========================================================================
# Report
# ==========================================================================
Write-Host ""
Write-Host "==== Result ===="
Write-Host ("  ops executed : {0}/{1}" -f $i, $Iterations)
Write-Host  "  op mix       :"
foreach ($k in ($opCounts.Keys | Sort-Object)) {
    Write-Host ("                 {0,-16} {1}" -f $k, $opCounts[$k])
}
Write-Host ("  survived     : {0}" -f (-not ($crash -or $hangOp -or $wer.Count -gt 0)))
Write-Host ("  process exit : {0}" -f $(if ($crash) { $crash } else { 'no (clean)' }))
Write-Host ("  pipe hang    : {0}" -f $(if ($hangOp) { "after '$hangOp'" } else { 'no' }))
Write-Host ("  WER events   : {0}" -f $wer.Count)
Write-Host ("  logged errors: {0}" -f $errLines.Count)

$failed = $crash -or $hangOp -or ($wer.Count -gt 0) -or ($errLines.Count -gt 0)
if ($failed) {
    Write-Host ""
    Write-Host "  --- last 30 operations (repro w/ -Seed $Seed) ---" -ForegroundColor Yellow
    $tail = $transcript | Select-Object -Last 30
    foreach ($t in $tail) { Write-Host "    $t" }

    if ($wer.Count -gt 0) {
        Write-Host ""
        Write-Host "  --- WER events ---" -ForegroundColor Yellow
        foreach ($e in $wer) {
            $msg = ($e.Message -replace "`r?`n", ' | ' -replace ' +', ' ')
            $msg = $msg.Substring(0, [Math]::Min(500, $msg.Length))
            Write-Host "    [$($e.TimeCreated)] $($e.LevelDisplayName): $msg"
        }
    }

    if ($errLines.Count -gt 0) {
        Write-Host ""
        Write-Host ("  --- {0} logged ERROR line(s) ---" -f $errLines.Count) -ForegroundColor Yellow
        $errLines | Select-Object -Last 15 | ForEach-Object { Write-Host "    $($_.Line)" }
    } elseif (Test-Path $ErrorsPath) {
        Write-Host ""
        Write-Host "  --- errors.log tail ---" -ForegroundColor Yellow
        Get-Content $ErrorsPath -Tail 25 | ForEach-Object { Write-Host "    $_" }
    }

    Write-Host ""
    Write-Host "STABILITY: FAIL" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "STABILITY: PASS -- $i ops, no crash" -ForegroundColor Green
exit 0
