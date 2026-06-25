# Full Claude-Code-style end-to-end test.
#
# Unlike test-state-live.ps1, the perch CLI is invoked from inside a *real*
# spawned pane shell — so this exercises:
#   * BuildStartupCommandLine env injection (PERCH_PIPE, PERCH_PANE_ID reach the shell)
#   * The tools/ dir being on the inherited PATH (`perch` resolves)
#   * perch.exe connecting from inside the agent's process tree
#   * The host's IPC server, dispatch, and Session field updates
#   * The sidebar template rendering all the updated fields
#
# Trigger: we use `perch open --cmd "pwsh -NoExit -File <agent-sim.ps1>"` from
# the default pane, so the new pane's startup command IS the simulator.

[CmdletBinding()]
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$CliPath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools\perch.exe",
    [string]$LogPath  = "$env:APPDATA\perch\errors.log",
    [string]$ShotPath = 'C:\tmp\perch-claude-sim.png'
)

$ErrorActionPreference = 'Stop'

$agentSim = (Resolve-Path "$PSScriptRoot\agent-sim.ps1").Path

function Invoke-Cli {
    param([string]$PipePath, [string[]]$CliArgs)
    $psi = [System.Diagnostics.ProcessStartInfo]::new($CliPath)
    foreach ($a in $CliArgs) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables['PERCH_PIPE'] = $PipePath
    $proc = [System.Diagnostics.Process]::Start($psi)
    if (-not $proc.WaitForExit(3000)) { $proc.Kill(); throw "perch $($CliArgs -join ' ') hung" }
    $err = $proc.StandardError.ReadToEnd()
    if ($proc.ExitCode -ne 0) { throw "perch $($CliArgs -join ' ') exited $($proc.ExitCode): $err" }
}

if (Test-Path $LogPath) { Remove-Item $LogPath -Force }
Get-Process -Name Perch -EA SilentlyContinue | Where-Object { $_.Path -like '*Debug*' } | Stop-Process -Force
Start-Sleep -Milliseconds 500

# Start from a clean session state: previous test runs may have added extra
# sessions and switched ActiveSessionId. Drop sessions.json entirely so the
# app boots with a fresh "main" session as the active one.
$sessionsPath = "$env:APPDATA\perch\sessions.json"
if (Test-Path $sessionsPath) { Remove-Item $sessionsPath -Force }

$p = Start-Process -PassThru -FilePath $ExePath
Start-Sleep -Seconds 5
if ($p.HasExited) { throw "Perch exited $($p.ExitCode)" }
Write-Host "Launched pid=$($p.Id)"

# Origin: the active session's first leaf — that's the one with a running
# IPC server, since IPC servers only spin up when the session is materialized.
$sessions = Get-Content $sessionsPath -Raw | ConvertFrom-Json
$activeId = $sessions.ActiveSessionId
$active = $sessions.Sessions | Where-Object { $_.Id -eq $activeId } | Select-Object -First 1
if (-not $active) { $active = $sessions.Sessions[0] }
$originPaneId = $active.Root.Id -replace '-', ''
$originPipe = "\\.\pipe\perch\$originPaneId"
Write-Host "Origin: session '$($active.Title)' pane $originPaneId"

# Spawn a brand-new session whose shell IS the simulator. PowerShell -NoExit
# keeps the pane alive after the script completes so we can inspect it.
$shellCmd = "pwsh.exe -NoExit -File $agentSim"
Write-Host "Spawning agent: $shellCmd"
Invoke-Cli -PipePath $originPipe -CliArgs @('open', '--name', 'claude-sim', '--cmd', $shellCmd)

# Agent script sleeps total ~3.2s + boot overhead. 8s is comfortable.
Start-Sleep -Seconds 8

& "$PSScriptRoot\capture-window.ps1" -ProcessId $p.Id -OutPath $ShotPath -TopMostFirst $true | Out-Null
Write-Host "Screenshot: $ShotPath"

$lines = if (Test-Path $LogPath) { Get-Content $LogPath } else { @() }
Write-Host ""
Write-Host "=== Log (all PerchIpc + errors) ==="
$lines | Where-Object { $_ -match 'PerchIpc|ERROR' } | ForEach-Object { Write-Host "  $_" }

Stop-Process -Id $p.Id -EA SilentlyContinue

# Tally by type — every verb the simulator emits should arrive at least once.
$counts = @{
    notify = ($lines | Where-Object { $_ -match 'PerchIpc\.recv.*type=notify' }).Count
    status = ($lines | Where-Object { $_ -match 'PerchIpc\.recv.*type=status' }).Count
    meta   = ($lines | Where-Object { $_ -match 'PerchIpc\.recv.*type=meta'   }).Count
    open   = ($lines | Where-Object { $_ -match 'PerchIpc\.recv.*type=open'   }).Count
}
$errors = $lines | Where-Object { $_ -match '^\[.*\] ERROR' }

Write-Host ""
Write-Host "=== Dispatch counts ==="
$counts.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Host ("  {0,-7} {1}" -f $_.Key, $_.Value) }

if ($counts.open   -lt 1) { throw "FAIL: perch open never dispatched" }
if ($counts.status -lt 3) { throw "FAIL: expected >=3 status dispatches, got $($counts.status)" }
if ($counts.meta   -lt 1) { throw "FAIL: meta never dispatched" }
if ($counts.notify -lt 3) { throw "FAIL: expected >=3 notify dispatches, got $($counts.notify)" }
if ($errors) { Write-Warning "Errors during run:"; $errors | ForEach-Object { Write-Warning "  $_" } }

Write-Host ""
Write-Host "PASS: agent simulation hit every IPC verb from inside a real spawned shell."
Write-Host "Eyeball check the screenshot: claude-sim sidebar row should show:"
Write-Host "  state pill: 'done' (green)"
Write-Host "  branch:     'main'"
Write-Host "  ports:      3000, 5173"
Write-Host "  notification: 'complete' (green dot)"
