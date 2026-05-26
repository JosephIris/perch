# Phase 2 live test: pushes status + meta via cmux.exe into the running app
# and screenshots the sidebar to verify the chips render.

[CmdletBinding()]
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\CmuxWin\bin\Debug\net8.0-windows\win10-x64\CmuxWin.exe",
    [string]$CliPath  = "$PSScriptRoot\..\src\CmuxWin\bin\Debug\net8.0-windows\win10-x64\tools\cmux.exe",
    [string]$LogPath  = "$env:APPDATA\cmux-win\errors.log",
    [string]$ShotPath = 'C:\tmp\cmux-state-live.png'
)

$ErrorActionPreference = 'Stop'

function Invoke-Cli {
    param([string]$PipePath, [string[]]$CliArgs)
    $psi = [System.Diagnostics.ProcessStartInfo]::new($CliPath)
    foreach ($a in $CliArgs) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables['CMUX_PIPE'] = $PipePath
    $proc = [System.Diagnostics.Process]::Start($psi)
    if (-not $proc.WaitForExit(3000)) { $proc.Kill(); throw "cmux $($CliArgs -join ' ') hung" }
    $err = $proc.StandardError.ReadToEnd()
    if ($proc.ExitCode -ne 0) { throw "cmux $($CliArgs -join ' ') exited $($proc.ExitCode): $err" }
}

if (Test-Path $LogPath) { Remove-Item $LogPath -Force }
Get-Process -Name CmuxWin -EA SilentlyContinue | Where-Object { $_.Path -like '*Debug*' } | Stop-Process -Force
Start-Sleep -Milliseconds 500

$p = Start-Process -PassThru -FilePath $ExePath
Start-Sleep -Seconds 5
if ($p.HasExited) { throw "CmuxWin exited $($p.ExitCode)" }
Write-Host "Launched pid=$($p.Id)"

# Resolve pane pipe from sessions.json (same trick as test-ipc-live).
$sessions = Get-Content "$env:APPDATA\cmux-win\sessions.json" -Raw | ConvertFrom-Json
$paneId = $sessions.Sessions[0].Root.Id -replace '-', ''
$pipe = "\\.\pipe\cmux\$paneId"
Write-Host "Pipe: $pipe"

# Three pushes — a complete agent narrative in miniature.
Invoke-Cli -PipePath $pipe -CliArgs @('status', 'working', 'running', 'tests')
Invoke-Cli -PipePath $pipe -CliArgs @('meta', '--branch', 'feature/agent-hooks', '--port', '3000', '--port', '5173')
Invoke-Cli -PipePath $pipe -CliArgs @('notify', '--level', 'success', 'tests passing')
Start-Sleep -Milliseconds 800

& "$PSScriptRoot\capture-window.ps1" -ProcessId $p.Id -OutPath $ShotPath -TopMostFirst $true | Out-Null
Write-Host "Screenshot: $ShotPath"

$lines = if (Test-Path $LogPath) { Get-Content $LogPath } else { @() }
Write-Host ""
Write-Host "=== Log (filtered) ==="
$lines | Where-Object { $_ -match 'CmuxIpc|ERROR' } | ForEach-Object { Write-Host "  $_" }

Stop-Process -Id $p.Id -EA SilentlyContinue

$gotStatus = $lines | Where-Object { $_ -match 'CmuxIpc\.recv.*type=status' }
$gotMeta   = $lines | Where-Object { $_ -match 'CmuxIpc\.recv.*type=meta' }
$gotNotify = $lines | Where-Object { $_ -match 'CmuxIpc\.recv.*type=notify' }
$errors    = $lines | Where-Object { $_ -match '^\[.*\] ERROR' }

if (-not $gotStatus) { throw "FAIL: status not dispatched" }
if (-not $gotMeta)   { throw "FAIL: meta not dispatched" }
if (-not $gotNotify) { throw "FAIL: notify not dispatched" }
if ($errors) { Write-Warning "Errors during run:"; $errors | ForEach-Object { Write-Warning "  $_" } }

Write-Host ""
Write-Host "PASS: all three message types dispatched."
