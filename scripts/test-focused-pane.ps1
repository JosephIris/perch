# Focused-pane-wins test.
#
# Two sessions are created (main + a `perch open`-spawned one). After the
# open call, the newly-spawned session becomes active. We then push state
# from BOTH panes' pipes:
#   * Push A from the BACKGROUND pane (no longer active) → should be IGNORED
#   * Push B from the ACTIVE pane → should LAND on the sidebar
#
# We verify by reading the log (both pushes get a PerchIpc.recv line because
# the gate happens on the UI side, not at dispatch) and by screenshot
# (active session's row should only show push B's state, not A's).

[CmdletBinding()]
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$CliPath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools\perch.exe",
    [string]$LogPath  = "$env:APPDATA\perch\errors.log",
    [string]$ShotPath = 'C:\tmp\perch-focused-pane.png'
)

$ErrorActionPreference = 'Stop'

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

# Fresh state.
if (Test-Path $LogPath) { Remove-Item $LogPath -Force }
$sp = "$env:APPDATA\perch\sessions.json"
if (Test-Path $sp) { Remove-Item $sp -Force }
Get-Process -Name Perch -EA SilentlyContinue | Where-Object { $_.Path -like '*Debug*' } | Stop-Process -Force
Start-Sleep -Milliseconds 500

$p = Start-Process -PassThru -FilePath $ExePath
Start-Sleep -Seconds 5
if ($p.HasExited) { throw "Perch exited $($p.ExitCode)" }

# Main pane is currently active.
$sessions = Get-Content $sp -Raw | ConvertFrom-Json
$mainPaneId = ($sessions.Sessions | Where-Object { $_.Title -eq 'main' }).Root.Id -replace '-', ''
$mainPipe = "\\.\pipe\perch\$mainPaneId"
Write-Host "main pipe: $mainPipe"

# Spawn a second session and let it become active.
Invoke-Cli -PipePath $mainPipe -CliArgs @('open', '--name', 'secondary')
Start-Sleep -Milliseconds 1500
$sessions = Get-Content $sp -Raw | ConvertFrom-Json
$secondary = $sessions.Sessions | Where-Object { $_.Title -eq 'secondary' } | Select-Object -First 1
$secondaryPaneId = $secondary.Root.Id -replace '-', ''
$secondaryPipe = "\\.\pipe\perch\$secondaryPaneId"
Write-Host "secondary pipe: $secondaryPipe  (this one is now active)"
Start-Sleep -Milliseconds 1500

# Push from BACKGROUND (main) — should be ignored.
Invoke-Cli -PipePath $mainPipe -CliArgs @('status', 'waiting', 'BG-from-main')
Invoke-Cli -PipePath $mainPipe -CliArgs @('notify', '--level', 'error', 'BG-notif-from-main')
Start-Sleep -Milliseconds 400

# Push from ACTIVE (secondary) — should land.
Invoke-Cli -PipePath $secondaryPipe -CliArgs @('status', 'done', 'FG-from-secondary')
Invoke-Cli -PipePath $secondaryPipe -CliArgs @('notify', '--level', 'success', 'FG-notif-from-secondary')
Start-Sleep -Milliseconds 600

& "$PSScriptRoot\capture-window.ps1" -ProcessId $p.Id -OutPath $ShotPath -TopMostFirst $true | Out-Null
Write-Host "Screenshot: $ShotPath"

$lines = if (Test-Path $LogPath) { Get-Content $LogPath } else { @() }
Write-Host ""
Write-Host "=== Log (filtered) ==="
$lines | Where-Object { $_ -match 'PerchIpc|ERROR' } | ForEach-Object { Write-Host "  $_" }
Stop-Process -Id $p.Id -EA SilentlyContinue

# All four pushes should hit the log (gate runs after dispatch).
$bgStatus = $lines | Where-Object { $_ -match "pane=$mainPaneId type=status" }
$bgNotify = $lines | Where-Object { $_ -match "pane=$mainPaneId type=notify" }
$fgStatus = $lines | Where-Object { $_ -match "pane=$secondaryPaneId type=status" }
$fgNotify = $lines | Where-Object { $_ -match "pane=$secondaryPaneId type=notify" }
if (-not $bgStatus -or -not $bgNotify) { throw "background pushes didn't reach dispatch" }
if (-not $fgStatus -or -not $fgNotify) { throw "foreground pushes didn't reach dispatch" }

Write-Host ""
Write-Host "PASS: all four pushes dispatched. Eyeball the screenshot:"
Write-Host "  - 'main' row: should be empty (no waiting pill, no red error dot)"
Write-Host "  - 'secondary' row: should show 'done' pill + green 'FG-notif-from-secondary'"
Write-Host "  - If 'main' shows waiting pill or red dot, the gate isn't dropping background pushes."
