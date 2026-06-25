# Live end-to-end test: launches Perch, runs `perch notify ...` inside the
# spawned pane, and verifies the host received it. Two checks:
#   1. errors.log contains the dispatch line from PerchIpc.Dispatch.
#   2. Screenshot saved so we can eyeball the sidebar showing the notification.
#
# Adapted from scripts/test-features.ps1 — same HWND-finding pattern.

[CmdletBinding()]
param(
    [string]$ExePath = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$LogPath = "$env:APPDATA\perch\errors.log",
    [string]$ShotPath = 'C:\tmp\perch-ipc-live.png'
)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class W2 {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
}
'@ -ErrorAction SilentlyContinue

function Find-TerminalHwnd($processId) {
    # Scriptblock-as-delegate in PowerShell doesn't close over function-local
    # params reliably, so we stash everything we need on $script: scope and
    # read from there inside the callback.
    $script:findPid = $processId
    $script:topHwnds = New-Object 'System.Collections.Generic.List[IntPtr]'
    $cb1 = [W2+EnumProc] {
        param($h,$lp)
        $opid = 0
        [W2]::GetWindowThreadProcessId($h, [ref]$opid) | Out-Null
        if ($opid -eq $script:findPid -and [W2]::IsWindowVisible($h)) {
            $script:topHwnds.Add($h)
        }
        return $true
    }
    [W2]::EnumWindows($cb1, [IntPtr]::Zero) | Out-Null

    $script:foundTerm = [IntPtr]::Zero
    foreach ($h in $script:topHwnds) {
        $cb2 = [W2+EnumProc] {
            param($child,$lp)
            $sb = New-Object System.Text.StringBuilder 64
            [W2]::GetClassName($child, $sb, 64) | Out-Null
            if ($sb.ToString() -eq 'HwndTerminalClass') { $script:foundTerm = $child }
            return $true
        }
        [W2]::EnumChildWindows($h, $cb2, [IntPtr]::Zero) | Out-Null
        if ($script:foundTerm -ne [IntPtr]::Zero) { break }
    }
    return @{ Top = $script:topHwnds; Term = $script:foundTerm }
}

function Send-Line($termHwnd, $line) {
    foreach ($ch in $line.ToCharArray()) {
        [W2]::PostMessage($termHwnd, 0x0102, [IntPtr][int]$ch, [IntPtr]1) | Out-Null
        Start-Sleep -Milliseconds 5
    }
    [W2]::PostMessage($termHwnd, 0x0102, [IntPtr]13, [IntPtr]1) | Out-Null
}

# --- Setup ---
if (Test-Path $LogPath) { Remove-Item $LogPath -Force }
Get-Process -Name Perch -EA SilentlyContinue | Where-Object { $_.Path -like '*Debug*' } | Stop-Process -Force
Start-Sleep -Milliseconds 500

# --- Launch ---
$p = Start-Process -PassThru -FilePath $ExePath
Start-Sleep -Seconds 4
if ($p.HasExited) { throw "Perch exited code $($p.ExitCode)" }
Write-Host "Launched pid=$($p.Id)"

# --- Find terminal HWND ---
$hwnds = Find-TerminalHwnd $p.Id
if ($hwnds.Term -eq [IntPtr]::Zero) {
    Stop-Process -Id $p.Id -EA SilentlyContinue
    throw "No HwndTerminalClass found under pid $($p.Id)"
}
Write-Host "Terminal HWND: $($hwnds.Term)"

# Bring main window to front so any visible toast/sidebar shows.
foreach ($t in $hwnds.Top) {
    [W2]::ShowWindow($t, 9) | Out-Null
    [W2]::SetForegroundWindow($t) | Out-Null
}
Start-Sleep -Milliseconds 300

# --- Drive the test: invoke perch.exe directly against the live pane's pipe ---
#
# WM_CHAR injection into PowerShell crashes PSReadLine on non-translatable
# keys (anything outside 0..255 fails the ConsoleKey ctor), so we don't use
# the shell. The thing we actually want to verify is that the host's IPC
# server is listening on the pane's pipe and dispatches the message — that
# only needs the pipe path, which we can derive from sessions.json.
$sessionsJson = Get-Content "$env:APPDATA\perch\sessions.json" -Raw | ConvertFrom-Json
$paneGuid = $sessionsJson.Sessions[0].Root.Id
$paneId = $paneGuid -replace '-', ''
$pipePath = "\\.\pipe\perch\$paneId"
Write-Host "Pane pipe: $pipePath"

$marker = "ipc-test-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
$cliExe = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools\perch.exe"
$psi = [System.Diagnostics.ProcessStartInfo]::new($cliExe)
$psi.ArgumentList.Add('notify')
$psi.ArgumentList.Add('--level'); $psi.ArgumentList.Add('success')
$psi.ArgumentList.Add($marker)
$psi.UseShellExecute = $false
$psi.RedirectStandardError = $true
$psi.EnvironmentVariables['PERCH_PIPE'] = $pipePath
$cliProc = [System.Diagnostics.Process]::Start($psi)
if (-not $cliProc.WaitForExit(3000)) { $cliProc.Kill(); throw 'perch.exe hung' }
$cliErr = $cliProc.StandardError.ReadToEnd()
if ($cliProc.ExitCode -ne 0) { throw "perch.exe exited $($cliProc.ExitCode): $cliErr" }
Write-Host "perch.exe sent notify (marker=$marker)"
Start-Sleep -Milliseconds 500  # let the dispatch land on the UI thread + log

# --- Screenshot for eyeballing the sidebar ---
& "$PSScriptRoot\capture-window.ps1" -ProcessId $p.Id -OutPath $ShotPath -TopMostFirst $false | Out-Null
Write-Host "Screenshot: $ShotPath"

# --- Verify the dispatch fired ---
$logLines = if (Test-Path $LogPath) { Get-Content $LogPath } else { @() }
$dispatched = $logLines | Where-Object { $_ -match 'PerchIpc\.recv.*type=notify' }
$errors = $logLines | Where-Object { $_ -match '^\[.*\] ERROR' }

Write-Host ""
Write-Host "=== Log lines (filtered) ==="
$logLines | Where-Object { $_ -match 'PerchIpc|ERROR' } | ForEach-Object { Write-Host "  $_" }

# --- Cleanup ---
Stop-Process -Id $p.Id -EA SilentlyContinue

# --- Assertions ---
if (-not $dispatched) {
    throw "FAIL: no 'PerchIpc.recv type=notify' line in $LogPath"
}
if ($errors) {
    Write-Warning "Errors logged during run:"
    $errors | ForEach-Object { Write-Warning "  $_" }
}
Write-Host ""
Write-Host "PASS: dispatch line found -> $($dispatched -join '; ')"
