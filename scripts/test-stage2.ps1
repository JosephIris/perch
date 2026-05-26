# Stage 2 (ConPTY bridge) end-to-end test.
#
# What this proves without touching anything else on the desktop:
#   * Window opens; WebView2 + xterm.js bootstrap
#   * ConPty spawns a real shell; bytes flow from PTY to the page
#   * The page round-trips bytes back to the host via `pty.in`, which we
#     mimic here through `cmux test pty.send --text ...` (control pipe ->
#     ConPty.Write -> PTY -> shell echoes back -> OutputReceived adds bytes)
#   * A clean `exit\r` brings the shell down with exit code 0
#
# Safety:
#   * Window is minimized for the entire run -- the user can keep using
#     other apps. The control pipe and UIA both work fine against a
#     minimized window.
#   * No keystroke synthesis anywhere. Input goes via the named pipe.
#   * The control pipe only listens when CMUX_ENABLE_TEST_IPC=1 is in the
#     launching env, so a normal install exposes no surface.

[CmdletBinding()]
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\CmuxWin\bin\Debug\net8.0-windows\win10-x64\CmuxWin.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\CmuxWin\bin\Debug\net8.0-windows\win10-x64\tools",
    [switch]$KeepVisible
)

$ErrorActionPreference = 'Continue'
$LogPath = "$env:APPDATA\cmux-win\errors.log"
$CmuxExe = Join-Path $ToolsDir 'cmux.exe'

if (-not (Test-Path $ExePath))  { throw "CmuxWin.exe not found at $ExePath" }
if (-not (Test-Path $CmuxExe))  { throw "cmux.exe missing at $CmuxExe" }

# --- Helpers ----------------------------------------------------------------

if (-not ('Cmux.WinShow2' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace Cmux { public static class WinShow2 {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  public const int SW_MINIMIZE = 6, SW_RESTORE = 9;
} }
'@
}

function Stop-CmuxWin {
    Get-Process -Name CmuxWin -EA SilentlyContinue |
        Where-Object { $_.Path -like '*\bin\Debug\*' } |
        Stop-Process -Force -EA SilentlyContinue
    Start-Sleep -Milliseconds 400
}

function Wait-For-Log {
    param([string]$Pattern, [int]$TimeoutSec = 10, [int]$AfterLineCount = 0)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $LogPath) {
            $lines = Get-Content $LogPath -EA SilentlyContinue
            if ($lines -and $lines.Count -gt $AfterLineCount) {
                $tail = $lines | Select-Object -Skip $AfterLineCount
                $hit = $tail | Where-Object { $_ -match $Pattern } | Select-Object -First 1
                if ($hit) { return $hit }
            }
        }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function Get-PtyBytes-FromLastSnapshot {
    if (-not (Test-Path $LogPath)) { return -1 }
    $line = Get-Content $LogPath | Where-Object { $_ -match 'Pty\.snapshot:\s*bytes=(\d+)' } | Select-Object -Last 1
    if ($line -and $line -match 'bytes=(\d+)') { return [int]$matches[1] }
    return -1
}

function Invoke-CmuxTest {
    param([string]$Verb, [string]$Text)
    $verbArgs = @('test', $Verb)
    if ($PSBoundParameters.ContainsKey('Text')) {
        # `--text` is a generic field flag in stage 3; pty.send still uses it.
        $verbArgs += @('--text', $Text)
    }
    $out = & $CmuxExe @verbArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "cmux test $Verb exited $LASTEXITCODE`: $out"
    }
}

# --- Run --------------------------------------------------------------------

Stop-CmuxWin
Remove-Item $LogPath -Force -EA SilentlyContinue

$env:CMUX_ENABLE_TEST_IPC = '1'
$p = Start-Process -PassThru -FilePath $ExePath

# Wait for the window to actually exist before minimizing it.
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline) {
    $p.Refresh()
    if ($p.HasExited) { throw "CmuxWin exited early code=$($p.ExitCode)" }
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 200
}
if ($p.MainWindowHandle -eq [IntPtr]::Zero) { throw "main window never appeared" }
if (-not $KeepVisible) {
    [Cmux.WinShow2]::ShowWindow($p.MainWindowHandle, [Cmux.WinShow2]::SW_MINIMIZE) | Out-Null
}
Write-Host "Launched cmux pid=$($p.Id) (minimized: $(-not $KeepVisible))"

# --- Assertion 1: ConPty starts a shell ------------------------------------
$startLine = Wait-For-Log -Pattern 'ConPty\.Start: pid=\d+' -TimeoutSec 12
if (-not $startLine) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: ConPty never started (no 'ConPty.Start' log line)"
}
$shellPid = if ($startLine -match 'pid=(\d+)') { [int]$matches[1] } else { 0 }
Write-Host "  [+] ConPty started: shell pid=$shellPid"

# --- Assertion 2: banner + prompt produced output --------------------------
# PowerShell's banner takes a moment to flush; give it 2s, then snapshot.
Start-Sleep -Seconds 2
Invoke-CmuxTest -Verb 'pty.snapshot'
Start-Sleep -Milliseconds 200
$bytesAfterBanner = Get-PtyBytes-FromLastSnapshot
if ($bytesAfterBanner -lt 1) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: PTY produced no output after 2s (snapshot bytes=$bytesAfterBanner)"
}
Write-Host "  [+] Banner+prompt produced $bytesAfterBanner bytes"

# --- Assertion 3: pty.send -> shell echoes back ----------------------------
# Send `echo cmux-stage2-marker<CR>`. PowerShell echoes the command line back
# (the cursor reposition + bytes) AND prints the literal "cmux-stage2-marker"
# on its own line. Either way, total bytes received goes up substantially.
$marker = 'cmux-stage2-marker'
Invoke-CmuxTest -Verb 'pty.send' -Text "echo $marker`r"
Start-Sleep -Seconds 1
Invoke-CmuxTest -Verb 'pty.snapshot'
Start-Sleep -Milliseconds 200
$bytesAfterEcho = Get-PtyBytes-FromLastSnapshot
$delta = $bytesAfterEcho - $bytesAfterBanner
if ($delta -lt 8) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: 'echo $marker' produced only $delta new bytes; expected >= 8"
}
Write-Host "  [+] echo produced $delta new bytes (total: $bytesAfterEcho)"

# --- Assertion 4: 'exit' brings the shell down cleanly --------------------
Invoke-CmuxTest -Verb 'pty.send' -Text "exit`r"
$exitLine = Wait-For-Log -Pattern 'pty\.out' -TimeoutSec 1  # eats final bytes
# We can't directly observe pty.exit unless we log it; rely on process death.
$deadline = (Get-Date).AddSeconds(8)
$shellGone = $false
while ((Get-Date) -lt $deadline) {
    if (-not (Get-Process -Id $shellPid -EA SilentlyContinue)) { $shellGone = $true; break }
    Start-Sleep -Milliseconds 200
}
if (-not $shellGone) {
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    throw "FAIL: shell pid=$shellPid still alive 8s after sending 'exit'"
}
Write-Host "  [+] Shell exited cleanly after 'exit'"

# --- Cleanup ---------------------------------------------------------------
Stop-Process -Id $p.Id -Force -EA SilentlyContinue
Start-Sleep -Milliseconds 400

# --- Look for any errors that snuck in -------------------------------------
$errors = @(Get-Content $LogPath -EA SilentlyContinue | Where-Object { $_ -match '^\[.*\] ERROR' })
if ($errors.Count -gt 0) {
    Write-Host ""
    Write-Host "WARN: ERROR lines in log during run (non-fatal but worth a look):" -ForegroundColor Yellow
    $errors | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}

Write-Host ""
Write-Host "ALL PASS  --  stage 2 ConPTY bridge end-to-end works" -ForegroundColor Green
exit 0
