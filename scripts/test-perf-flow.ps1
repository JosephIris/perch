# Regression test for the multi-pane lag bug (2026-05-29).
#
# THE BUG: PTY output was shovelled into the WebView2 page fire-and-forget.
# Under a fast producer the page's xterm write buffer grew without bound;
# two such panes share ONE renderer thread, so the backlog starved keystroke
# handling and the whole UI froze (renderer process at 1.5 GB, keystrokes not
# landing at all).
#
# THE FIX: ConPty applies backpressure — it stops reading once the page is
# ~256 KB behind on rendering (the page acks each drained xterm write), so the
# renderer never falls arbitrarily behind and stays responsive.
#
# HOW THIS TEST PROVES IT (and proves it actually detects the bug):
#   We flood TWO panes hard, then measure two things while they stream:
#     1. peak unacked backlog per pane  (how far the renderer fell behind)
#     2. render-ping round-trip latency (a faithful proxy for keystroke lag —
#        the ping is serviced on the SAME main-thread queue as keystrokes)
#   We run the identical scenario twice:
#     - FIXED  : flow control on  -> backlog stays bounded, pings stay fast
#     - BROKEN : CMUX_DISABLE_FLOW_CONTROL=1 -> backlog explodes to many MB
#   The test passes only if the fixed run meets the responsiveness bars AND the
#   broken run demonstrably falls far behind. If someone deletes the gate, the
#   fixed run regresses to the broken numbers and this fails.
#
# Keystroke-free, like the other harnesses: driven entirely via the control
# IPC pipe. The window is parked OFF-SCREEN (not minimized) so the renderer is
# not throttled by Chromium's hidden-window backgrounding — minimizing would
# invalidate the measurement.

[CmdletBinding()]
param(
    [string]$ExePath,
    [string]$ToolsDir,
    [int]$FloodChunks   = 350,   # ~60 KB per chunk -> ~21 MB of output per pane
    [int]$PingCount     = 40,
    [int]$PingIntervalMs = 150
)

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot  = Resolve-Path (Join-Path $scriptDir '..')
if (-not $ExePath)  { $ExePath  = Join-Path $repoRoot 'src\CmuxWin\bin\Debug\net8.0-windows\win10-x64\CmuxWin.exe' }
if (-not $ToolsDir) { $ToolsDir = Join-Path $repoRoot 'src\CmuxWin\bin\Debug\net8.0-windows\win10-x64\tools' }

$ErrorActionPreference = 'Continue'
if (-not (Test-Path $ExePath)) { throw "CmuxWin.exe not found at $ExePath. Build first." }

$SessionsPath = Join-Path $env:APPDATA 'cmux-win\sessions.json'
$SettingsPath = Join-Path $env:APPDATA 'cmux-win\settings.json'
$ErrorsPath   = Join-Path $env:APPDATA 'cmux-win\errors.log'

if (-not ('Cmux.Win' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace Cmux {
  public static class Win {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
  }
}
'@
}

function Stop-CmuxWin {
    Get-Process -Name CmuxWin -EA SilentlyContinue |
        Where-Object { $_.Path -like '*\bin\Debug\*' } |
        Stop-Process -Force -EA SilentlyContinue
    Start-Sleep -Milliseconds 400
}

function Reset-CmuxState {
    Remove-Item $SessionsPath -Force -EA SilentlyContinue
    Remove-Item $SettingsPath -Force -EA SilentlyContinue
    Remove-Item $ErrorsPath   -Force -EA SilentlyContinue
}

function Start-CmuxForTest {
    param([bool]$FlowControl)
    $env:CMUX_ENABLE_TEST_IPC = '1'
    if ($FlowControl) { Remove-Item Env:\CMUX_DISABLE_FLOW_CONTROL -EA SilentlyContinue }
    else              { $env:CMUX_DISABLE_FLOW_CONTROL = '1' }
    $p = Start-Process -PassThru -FilePath $ExePath
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $p.Refresh()
        if ($p.HasExited) { throw "CmuxWin exited early with $($p.ExitCode)" }
        if ($p.MainWindowHandle -ne 0) {
            # Leave the window where it opens. We do NOT minimize or park it at
            # the -32000 sentinel (that reads as occluded and Chromium stops
            # rendering -> no lazy PTY spawn, no valid measurement). The test
            # launches the app with anti-throttling browser flags (see
            # InitWebViewAsync), so the renderer keeps running at full speed
            # even if the user's windows cover it.
            return $p
        }
        Start-Sleep -Milliseconds 200
    }
    throw "CmuxWin main window never appeared"
}

function Wait-ForControlPipe {
    param([int]$TimeoutSec = 8)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path '\\.\pipe\cmux\control') { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

function Wait-ForPanePipe {
    param([string]$PaneId, [int]$TimeoutSec = 15)
    $bare = $PaneId -replace '-', ''
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path "\\.\pipe\cmux\$bare") { return $true }
        Start-Sleep -Milliseconds 150
    }
    return $false
}

function Send-Control {
    param([Parameter(Mandatory)][hashtable]$Payload)
    $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'cmux\control', [System.IO.Pipes.PipeDirection]::Out)
    try {
        $client.Connect(2000)
        $json = ($Payload | ConvertTo-Json -Compress) + "`n"
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
        $client.Write($bytes, 0, $bytes.Length)
        $client.Flush()
    } finally { $client.Dispose() }
}

# Collect every leaf pane id in the active session from sessions.json.
function Get-AllPaneIds {
    if (-not (Test-Path $SessionsPath)) { return @() }
    try {
        $store = Get-Content $SessionsPath -Raw | ConvertFrom-Json
        $active = $store.Sessions | Where-Object { $_.Id -eq $store.ActiveSessionId }
        if (-not $active) { $active = $store.Sessions[0] }
        $ids = New-Object System.Collections.Generic.List[string]
        $walk = {
            param($node)
            if ($node.Children -and $node.Children.Count -gt 0) {
                foreach ($c in $node.Children) { & $walk $c }
            } else { $ids.Add($node.Id) }
        }
        & $walk $active.Root
        return $ids.ToArray()
    } catch { return @() }
}

# The flood command typed into a pane's PowerShell: write ~60 KB chunks of
# 120-byte CRLF-terminated lines, $FloodChunks times. Fast enough to outrun the
# renderer. `$`-vars are escaped so OUR PowerShell doesn't expand them.
function Get-FloodCommand {
    return ("`$l='X'*118;`$nl=[char]13+[char]10;`$s=(`$l+`$nl)*500;" +
            "for(`$i=0;`$i -lt $FloodChunks;`$i++){[Console]::Out.Write(`$s)}`r")
}

function Get-Pongs {
    if (-not (Test-Path $ErrorsPath)) { return @() }
    $vals = New-Object System.Collections.Generic.List[double]
    foreach ($line in Get-Content $ErrorsPath) {
        if ($line -match 'RENDER_PONG id=\d+ ms=([\d\.]+)') { $vals.Add([double]$matches[1]) }
    }
    return $vals.ToArray()
}

function Get-MaxBacklog {
    if (-not (Test-Path $ErrorsPath)) { return 0 }
    $max = 0L
    foreach ($line in Get-Content $ErrorsPath) {
        if ($line -match 'FLOW pane=\S+ max=(\d+)') { $v = [long]$matches[1]; if ($v -gt $max) { $max = $v } }
    }
    return $max
}

function Percentile {
    param([double[]]$Values, [double]$P)
    if (-not $Values -or $Values.Count -eq 0) { return [double]::NaN }
    $sorted = $Values | Sort-Object
    $idx = [int][math]::Ceiling($P * $sorted.Count) - 1
    if ($idx -lt 0) { $idx = 0 }
    if ($idx -ge $sorted.Count) { $idx = $sorted.Count - 1 }
    return $sorted[$idx]
}

# ---- One full scenario: launch, flood two panes, ping under load ----------
function Invoke-FloodScenario {
    param([bool]$FlowControl)
    $label = if ($FlowControl) { 'FIXED  (flow control ON )' } else { 'BROKEN (flow control OFF)' }
    Write-Host ""
    Write-Host "==== Scenario: $label ===="
    $p = $null
    try {
        Stop-CmuxWin
        Reset-CmuxState
        $p = Start-CmuxForTest -FlowControl $FlowControl
        if (-not (Wait-ForControlPipe)) { throw "control pipe never appeared" }

        # Pane 1 = the session's root leaf. Wait for its PTY, then flood it.
        $deadline = (Get-Date).AddSeconds(12); $pane1 = $null
        while ((Get-Date) -lt $deadline -and -not $pane1) {
            $ids = @(Get-AllPaneIds)   # @() so a single id isn't unwrapped to a string (then [0] = first char)
            if ($ids.Count -ge 1) { $pane1 = $ids[0] } else { Start-Sleep -Milliseconds 150 }
        }
        if (-not $pane1) { throw "no pane id in sessions.json" }
        if (-not (Wait-ForPanePipe -PaneId $pane1)) { throw "pane1 PTY never spawned" }

        $flood = Get-FloodCommand
        Send-Control @{ verb = 'pane.simulate-input'; text = $flood }   # -> active = pane1

        # Split -> pane2 becomes active. Wait for its PTY, then flood it too.
        Send-Control @{ verb = 'pane.split-active'; dir = 'right' }
        $deadline = (Get-Date).AddSeconds(12); $pane2 = $null
        while ((Get-Date) -lt $deadline -and -not $pane2) {
            $ids = @(Get-AllPaneIds)
            $other = $ids | Where-Object { $_ -ne $pane1 } | Select-Object -First 1
            if ($other) { $pane2 = $other } else { Start-Sleep -Milliseconds 150 }
        }
        if (-not $pane2) { throw "pane2 never appeared after split" }
        if (-not (Wait-ForPanePipe -PaneId $pane2)) { throw "pane2 PTY never spawned" }
        Send-Control @{ verb = 'pane.simulate-input'; text = $flood }   # -> active = pane2

        # Let both floods ramp, then fire render pings under sustained load.
        Start-Sleep -Milliseconds 700
        for ($i = 1; $i -le $PingCount; $i++) {
            try { Send-Control @{ verb = 'render.ping'; id = $i } } catch { }
            Start-Sleep -Milliseconds $PingIntervalMs
        }
        Start-Sleep -Milliseconds 800        # let trailing pongs land
        Send-Control @{ verb = 'pty.flowstats' }
        Start-Sleep -Milliseconds 500        # let FLOW lines flush to log

        $pongs   = Get-Pongs
        $backlog = Get-MaxBacklog
        $answered = $pongs.Count
        $p50 = Percentile -Values $pongs -P 0.50
        $p95 = Percentile -Values $pongs -P 0.95
        $maxMs = if ($answered -gt 0) { ($pongs | Measure-Object -Maximum).Maximum } else { [double]::NaN }

        Write-Host ("  pings sent/answered : {0}/{1}" -f $PingCount, $answered)
        Write-Host ("  ping latency ms     : p50={0:N0}  p95={1:N0}  max={2:N0}" -f $p50, $p95, $maxMs)
        Write-Host ("  peak renderer backlog: {0:N1} MB" -f ($backlog / 1MB))

        return [pscustomobject]@{
            FlowControl = $FlowControl
            Sent        = $PingCount
            Answered    = $answered
            P50         = $p50
            P95         = $p95
            MaxMs       = $maxMs
            BacklogBytes = $backlog
        }
    } finally {
        if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -EA SilentlyContinue }
        Start-Sleep -Milliseconds 400
    }
}

# ==========================================================================
Write-Host "cmux multi-pane flow-control regression test"
Write-Host ("flood=~{0} MB/pane x2   pings={1}@{2}ms" -f [int]($FloodChunks * 60 / 1024), $PingCount, $PingIntervalMs)

$fixed  = Invoke-FloodScenario -FlowControl $true
$broken = Invoke-FloodScenario -FlowControl $false

# ---- Verdict --------------------------------------------------------------
# Thresholds. Generous so a slow CI box still passes when the fix is present.
$BacklogBoundBytes = 1MB        # fixed run must keep renderer within ~1 MB
$BacklogBlowupBytes = 4MB       # broken run must fall >=4 MB behind (it dumps ~20 MB)
$P95BudgetMs        = 500       # fixed run: keystroke-proxy latency under load
$MinAnswered        = [int]($fixed.Sent * 0.9)

$gate1 = $fixed.BacklogBytes -le $BacklogBoundBytes        # gate kept backlog bounded
$gate2 = ($fixed.Answered -ge $MinAnswered) -and ($fixed.P95 -le $P95BudgetMs)  # stayed responsive
$gate3 = $broken.BacklogBytes -ge $BacklogBlowupBytes      # without fix, renderer falls far behind
$gate4 = $broken.BacklogBytes -ge ($fixed.BacklogBytes * 4) # fix bounds it by a wide margin

Write-Host ""
Write-Host "==== Verdict ===="
Write-Host ("  [{0}] fixed backlog bounded   : {1:N1} MB <= {2:N1} MB" -f $(if($gate1){'PASS'}else{'FAIL'}), ($fixed.BacklogBytes/1MB), ($BacklogBoundBytes/1MB))
Write-Host ("  [{0}] fixed stayed responsive : {1}/{2} pongs, p95={3:N0}ms <= {4}ms" -f $(if($gate2){'PASS'}else{'FAIL'}), $fixed.Answered, $fixed.Sent, $fixed.P95, $P95BudgetMs)
Write-Host ("  [{0}] broken falls far behind : {1:N1} MB >= {2:N1} MB" -f $(if($gate3){'PASS'}else{'FAIL'}), ($broken.BacklogBytes/1MB), ($BacklogBlowupBytes/1MB))
Write-Host ("  [{0}] fix bounds vs broken    : broken {1:N1} MB >= 4x fixed {2:N1} MB" -f $(if($gate4){'PASS'}else{'FAIL'}), ($broken.BacklogBytes/1MB), ($fixed.BacklogBytes/1MB))
Write-Host ""
Write-Host ("  broken responsiveness (context): {0}/{1} pongs, p95={2:N0}ms, max={3:N0}ms" -f $broken.Answered, $broken.Sent, $broken.P95, $broken.MaxMs)

if ($gate1 -and $gate2 -and $gate3 -and $gate4) {
    Write-Host ""
    Write-Host "ALL PASS - flow control bounds the backlog and keeps the renderer responsive under 2-pane flood." -ForegroundColor Green
    exit 0
}
Write-Host ""
Write-Host "FAIL" -ForegroundColor Red
exit 1
