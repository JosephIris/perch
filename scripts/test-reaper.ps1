<#
.SYNOPSIS
Proves the idle reaper on a live, ISOLATED Perch.

Why a live test and not just IdleReaperTests: the unit tests pin the POLICY,
but what actually leaked was a process tree the app was still holding a handle
on — four processes per session, of which the pseudo-console only ever takes
the two attached to it. Only a running app with real ConPTYs can show that the
sleep path reaches the other two, and that it stops short of a dev server.

Two tabs, each with a child that OUTLIVES the console (started with no console
of its own, exactly like the MCP plugin server and the backgrounded dev server
in the incident):

  Tab A — agent, one SILENT portless child.  Must be slept; child reclaimed.
  Tab B — agent, one child holding a loopback listener.  Must NOT be slept
          while it is serving; and closing it by hand must still leave the
          server running, which is PaneJob's entire reason for existing.

Runs against C:\tmp\perch-reaper-test — your real Perch is untouched.
#>
param(
    [string]$OutDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64",
    [int]$Port = 5391,
    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
$ExePath  = Join-Path $OutDir 'Perch.exe'
$PerchExe = Join-Path $OutDir 'tools\perch.exe'
$DataDir  = 'C:\tmp\perch-reaper-test'
$LogPath  = Join-Path $DataDir 'perch\errors.log'
$MarkDir  = Join-Path $DataDir 'marks'

if (-not (Test-Path $ExePath)) { throw "Perch.exe not found at $ExePath (build first)" }

function Send-Verb {
    param([string]$Verb, [hashtable]$Flags = @{})
    $a = @('test', $Verb)
    foreach ($k in $Flags.Keys) { $a += "--$k"; $a += [string]$Flags[$k] }
    & $PerchExe @a *> $null
    return ($LASTEXITCODE -eq 0)
}
function Log-Last { param([string]$Pat)
    $m = Select-String -Path $LogPath -Pattern $Pat -SimpleMatch -EA SilentlyContinue | Select-Object -Last 1
    if ($m) { return $m.Line } else { return '' }
}
function Wait-Until { param([scriptblock]$Cond, [int]$TimeoutSec = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try { if (& $Cond) { return $true } } catch {}
        Start-Sleep -Milliseconds 400
    }
    return $false
}
function State {
    [void](Send-Verb 'state.dump')
    Start-Sleep -Milliseconds 700
    $line = Log-Last 'STATE_DUMP'
    if (-not $line) { throw "state.dump produced nothing" }
    return (($line -split 'STATE_DUMP', 2)[1] | ConvertFrom-Json)
}
function Tab { param($St, [string]$Id) return ($St.sessions | Where-Object { $_.id -eq $Id }) }
function Alive { param([int]$ProcId) return $null -ne (Get-Process -Id $ProcId -EA SilentlyContinue) }
function Send-Line { param([string]$PaneId, [string]$Text)
    [void](Send-Verb 'pty.send' @{ paneId = $PaneId; text = "$Text`r" })
    Start-Sleep -Milliseconds 900
}
# Launch a child that has NO console of its own, so ClosePseudoConsole can't
# claim it — the property that made the leak survive a pane close.
function Detached { param([string]$Body, [string]$PidFile)
    return "`$c = Start-Process -PassThru -WindowStyle Hidden powershell -ArgumentList '-NoProfile','-Command','$Body'; `$c.Id | Set-Content '$PidFile'"
}
$script:Fails = 0
function Check { param([string]$Name, [bool]$Ok, [string]$Detail = '')
    if ($Ok) { Write-Host "  PASS  $Name" -ForegroundColor Green }
    else { $script:Fails++; Write-Host "  FAIL  $Name  $Detail" -ForegroundColor Red }
}

Get-Process -Name Perch -EA SilentlyContinue |
    Where-Object { $_.Path -like '*\bin\Debug\*' } | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Milliseconds 400
Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $DataDir 'perch'), $MarkDir | Out-Null

$env:PERCH_DATA_DIR = $DataDir
$env:PERCH_ENABLE_TEST_IPC = '1'
# This session's own Perch env would otherwise be inherited by the panes.
Remove-Item Env:PERCH_PIPE, Env:PERCH_PANE_ID -EA SilentlyContinue

Write-Host "`nLaunching isolated Perch (data: $DataDir)" -ForegroundColor Cyan
$app = Start-Process -PassThru -FilePath $ExePath
$strays = @()
try {
    # Don't Test-Path the pipe: PowerShell can't reliably stat a named pipe, and
    # a false negative reads as "the app never started". Ask the app instead.
    if (-not (Wait-Until { Send-Verb 'state.dump' } 40)) { throw "control pipe never answered" }
    Start-Sleep -Seconds 4   # the seeded session spawns its shell lazily

    # ---- Tab A: an agent with a silent, portless leftover ------------------
    Write-Host "`nSetting up tab A (agent + a silent leftover)" -ForegroundColor Cyan
    $st = State
    $tabA  = $st.sessions[0]
    $paneA = $tabA.panes[0]
    $silentFile = Join-Path $MarkDir 'silent.pid'
    Send-Line $paneA.id "perch agent claude"
    Send-Line $paneA.id (Detached 'Start-Sleep 900' $silentFile)
    if (-not (Wait-Until { Test-Path $silentFile } 20)) { throw "tab A never reported its child's pid" }
    $silentPid = [int](Get-Content $silentFile).Trim()
    $strays += $silentPid
    Check "tab A's silent leftover is up (pid=$silentPid)" (Alive $silentPid) ''

    # ---- Tab B: an agent serving a port ------------------------------------
    Write-Host "`nSetting up tab B (agent + a dev server on $Port)" -ForegroundColor Cyan
    [void](Send-Verb 'session.new')
    Start-Sleep -Seconds 3
    $st = State
    $tabB = $st.sessions | Where-Object { $_.id -ne $tabA.id } | Select-Object -First 1
    if (-not $tabB) { throw "session.new made no second tab" }
    $paneB = $tabB.panes[0]
    $serverFile = Join-Path $MarkDir 'server.pid'
    $listen = "`$l=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback,$Port);`$l.Start();Start-Sleep 900"
    Send-Line $paneB.id "perch agent claude"
    Send-Line $paneB.id (Detached $listen $serverFile)
    if (-not (Wait-Until { Test-Path $serverFile } 20)) { throw "tab B never reported its child's pid" }
    $serverPid = [int](Get-Content $serverFile).Trim()
    $strays += $serverPid
    Check "tab B's dev server is up (pid=$serverPid)" (Alive $serverPid) ''

    # The sweep can only spare what Perch has attributed. LocalController
    # scans every 30s, so give it a scan.
    $sawPort = Wait-Until {
        $s = State; $b = Tab $s $tabB.id
        $b -and $b.panes[0].ports -and ($b.panes[0].ports -contains $Port)
    } 70
    Check "Perch attributed port $Port to tab B" $sawPort "the sweep can't spare what it can't see"

    $st = State
    Check "both tabs report an agent" `
        (((Tab $st $tabA.id).panes[0].agentType -eq 'claude') -and ((Tab $st $tabB.id).panes[0].agentType -eq 'claude')) ''

    # ---- 1. Sweep ----------------------------------------------------------
    # Tab B is the active one (session.new selected it), so this pass tests two
    # rules at once: the active tab is never taken, and neither is a server.
    Write-Host "`n1. One sweep: tab A sleeps, tab B is spared" -ForegroundColor Cyan
    [void](Send-Verb 'reap.now' @{ idleHours = '0.0001' })
    $sleptA = Wait-Until { (Tab (State) $tabA.id).dormant } 25
    Check "the idle agent tab was slept" $sleptA "REAP_DONE=$(Log-Last 'REAP_DONE')"
    Check "the tab you are in was not" (-not (Tab (State) $tabB.id).dormant) ''
    Check "it logged why" ((Log-Last 'Reap.idle') -ne '') ''
    Write-Host "     $(Log-Last 'Reap.idle')" -ForegroundColor DarkGray

    # ---- 2. The leftover the console does not take -------------------------
    Write-Host "`n2. The leftover the pseudo-console cannot reach" -ForegroundColor Cyan
    $silentGone = Wait-Until { -not (Alive $silentPid) } 30
    Check "the silent leftover was reclaimed (pid=$silentPid)" $silentGone `
        "this is the MCP-server case: ~45% of a leaked session's memory"
    Write-Host "     $(Log-Last 'Reap.job')" -ForegroundColor DarkGray
    Check "tab B's dev server is still up" (Alive $serverPid) ''

    # ---- 3. Sleeping is not closing ----------------------------------------
    Write-Host "`n3. Sleeping is reversible" -ForegroundColor Cyan
    $st = State
    $a = Tab $st $tabA.id
    Check "the slept tab is still in the sidebar" ($null -ne $a) "a slept tab must never be lost"
    Check "it kept its pane" ($a.panes.Count -ge 1) ''

    # ---- 4. Closing a pane still leaves its server running -----------------
    # PaneJob's deliberate design, and the line the job sweep must not cross.
    Write-Host "`n4. Closing tab B still leaves its server running" -ForegroundColor Cyan
    [void](Send-Verb 'session.close' @{ id = $tabB.id })
    Start-Sleep -Seconds 10   # polite exit (~3.5s) + the sweep's 2s grace
    Check "the dev server outlived its pane (pid=$serverPid)" (Alive $serverPid) `
        "a server outliving its pane is what the Local panel exists to show"
}
finally {
    foreach ($sp in $strays) { Stop-Process -Id $sp -Force -EA SilentlyContinue }
    if (-not $KeepOpen) { Stop-Process -Id $app.Id -Force -EA SilentlyContinue }
    Remove-Item Env:PERCH_DATA_DIR, Env:PERCH_ENABLE_TEST_IPC -EA SilentlyContinue
}

Write-Host ""
if ($script:Fails -eq 0) { Write-Host "All reaper checks passed." -ForegroundColor Green; exit 0 }
Write-Host "$($script:Fails) check(s) failed." -ForegroundColor Red; exit 1
