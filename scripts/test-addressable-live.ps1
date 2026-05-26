# Phase 3 live test: addressable panes.
#
# Drives `cmux open`, `cmux send`, `cmux focus` against a live app and
# screenshots the result so we can eyeball that:
#   * `open` created a new session ("test-target")
#   * `send` injected text into that session's pane (visible echo in terminal)
#   * `focus` brought it active (status bar shows the new session/pane)

[CmdletBinding()]
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\CmuxWin\bin\Debug\net8.0-windows\win10-x64\CmuxWin.exe",
    [string]$CliPath  = "$PSScriptRoot\..\src\CmuxWin\bin\Debug\net8.0-windows\win10-x64\tools\cmux.exe",
    [string]$LogPath  = "$env:APPDATA\cmux-win\errors.log",
    [string]$ShotPath = 'C:\tmp\cmux-addressable-live.png'
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

# Origin pipe — the main session's pane.
$sessions = Get-Content "$env:APPDATA\cmux-win\sessions.json" -Raw | ConvertFrom-Json
$mainPaneId = $sessions.Sessions[0].Root.Id -replace '-', ''
$mainPipe = "\\.\pipe\cmux\$mainPaneId"
Write-Host "Origin pipe: $mainPipe"

# 1) open — create a target session.
Invoke-Cli -PipePath $mainPipe -CliArgs @('open', '--name', 'test-target')
Start-Sleep -Milliseconds 1500

# Re-read sessions.json to discover the new pane id (so we can address it
# directly for send verification — this is what the agent would do too).
$sessions = Get-Content "$env:APPDATA\cmux-win\sessions.json" -Raw | ConvertFrom-Json
$target = $sessions.Sessions | Where-Object { $_.Title -eq 'test-target' } | Select-Object -First 1
if (-not $target) { throw "open: 'test-target' session not created" }
$targetPaneId = $target.Root.Id -replace '-', ''
$targetPipe = "\\.\pipe\cmux\$targetPaneId"
Write-Host "Target pipe: $targetPipe"

# Give the target shell a moment to boot fully before we send into it.
Start-Sleep -Milliseconds 2000

# 2) send — inject a marker line into the target pane. Newline included so
#    the shell actually runs whatever's there. Use a literal echo so the
#    terminal shows the marker in its scrollback.
$marker = "addr-test-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
Invoke-Cli -PipePath $mainPipe -CliArgs @('send', 'test-target:pane-1', "echo $marker`n")
Start-Sleep -Milliseconds 600

# 3) focus — bring test-target into view, then screenshot.
Invoke-Cli -PipePath $mainPipe -CliArgs @('focus', 'test-target:pane-1')
Start-Sleep -Milliseconds 800

& "$PSScriptRoot\capture-window.ps1" -ProcessId $p.Id -OutPath $ShotPath -TopMostFirst $true | Out-Null
Write-Host "Screenshot: $ShotPath  (look for: status bar shows test-target/pane-1; terminal shows '$marker')"

$lines = if (Test-Path $LogPath) { Get-Content $LogPath } else { @() }
Write-Host ""
Write-Host "=== Log (filtered) ==="
$lines | Where-Object { $_ -match 'CmuxIpc|ERROR' } | ForEach-Object { Write-Host "  $_" }

Stop-Process -Id $p.Id -EA SilentlyContinue

$gotOpen  = $lines | Where-Object { $_ -match 'CmuxIpc\.recv.*type=open' }
$gotSend  = $lines | Where-Object { $_ -match 'CmuxIpc\.recv.*type=send' }
$gotFocus = $lines | Where-Object { $_ -match 'CmuxIpc\.recv.*type=focus' }
$errors   = $lines | Where-Object { $_ -match '^\[.*\] ERROR' }

if (-not $gotOpen)  { throw "FAIL: open not dispatched" }
if (-not $gotSend)  { throw "FAIL: send not dispatched" }
if (-not $gotFocus) { throw "FAIL: focus not dispatched" }
if ($errors) { Write-Warning "Errors during run:"; $errors | ForEach-Object { Write-Warning "  $_" } }

Write-Host ""
Write-Host "PASS: open/send/focus all dispatched. Marker on screen: $marker"
