# Verify the three regressions reported on 2026-05-26:
#   1. Claude Code hooks didn't fire after install
#   2. The per-pane X button rendered outside the app window
#   3. App crashed on normal use
#
# IMPORTANT — keystroke-free.
# An earlier version of this script drove the app with [SendKeys]::SendWait.
# That sends keys to whatever has the foreground; the moment perch lost focus
# (which happens on every split/redraw) the Ctrl+Shift+W shortcuts spilled
# into the user's Chrome and tore down browser windows. We no longer touch
# the input layer. Instead the app exposes a control pipe (ControlIpcServer)
# which the test harness drives via `perch.exe test <verb>`. The pipe is only
# active when PERCH_ENABLE_TEST_IPC=1 in the launching process. You can keep
# using the machine normally while this runs.

[CmdletBinding()]
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools",
    [int]   $StressIterations = 10,
    [switch]$KeepVisible
)

$ErrorActionPreference = 'Continue'

if (-not (Test-Path $ExePath))  { throw "Perch.exe not found at $ExePath. Build first." }
if (-not (Test-Path $ToolsDir)) { throw "tools/ missing at $ToolsDir." }
$PerchExe = Join-Path $ToolsDir 'perch.exe'
if (-not (Test-Path $PerchExe)) { throw "perch.exe missing at $PerchExe." }

Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes

if (-not ('Perch.WinShow' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace Perch {
  public static class WinShow {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    public const int SW_MINIMIZE = 6, SW_RESTORE = 9;
  }
}
'@
}

function Hide-Perchdow {
    param([System.Diagnostics.Process]$P)
    if ($KeepVisible) { return }
    if ($P.MainWindowHandle -ne [IntPtr]::Zero) {
        [Perch.WinShow]::ShowWindow($P.MainWindowHandle, [Perch.WinShow]::SW_MINIMIZE) | Out-Null
    }
}

# --- helpers --------------------------------------------------------------

function Stop-Perch {
    Get-Process -Name Perch -EA SilentlyContinue |
        Where-Object { $_.Path -like '*\bin\Debug\*' } |
        Stop-Process -Force -EA SilentlyContinue
    Start-Sleep -Milliseconds 400
}

function Reset-PerchState {
    Remove-Item "$env:APPDATA\perch\sessions.json" -Force -EA SilentlyContinue
    Remove-Item "$env:APPDATA\perch\errors.log"    -Force -EA SilentlyContinue
}

function Start-PerchForTest {
    # PERCH_ENABLE_TEST_IPC must be in the LAUNCHED process's env, not just
    # ours — Start-Process inherits the current shell's env by default.
    $env:PERCH_ENABLE_TEST_IPC = '1'
    $p = Start-Process -PassThru -FilePath $ExePath
    # Wait for the main window to appear; we don't care if it's focused.
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
    param([int]$TimeoutSec = 5)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path '\\.\pipe\perch\control') { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

function Invoke-PerchTest {
    param([string]$Verb)
    # Stderr → stdout join so PowerShell surfaces error text. Exit 0 = success.
    $stderr = & $PerchExe test $Verb 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "perch test $Verb exited $LASTEXITCODE`: $stderr"
    }
}

function Get-PerchAutomationRoot {
    param([Parameter(Mandatory)][int]$ProcId)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcId)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $cond)
}

function Find-ClosePaneButtons {
    param([System.Windows.Automation.AutomationElement]$Window)
    $btn = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $name = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Close pane')
    $cond = New-Object System.Windows.Automation.AndCondition($btn, $name)
    return $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

# ==========================================================================
# Test 1: hooks pipeline (delegates to existing comprehensive test)
# ==========================================================================
Write-Host ""
Write-Host "==== Test 1: Claude Code hooks pipeline ===="
$hooksOk = $false
try {
    Stop-Perch; Reset-PerchState
    & "$PSScriptRoot\test-claude-hooks.ps1" -ExePath $ExePath -ToolsDir $ToolsDir
    $hooksOk = ($LASTEXITCODE -eq 0 -or $null -eq $LASTEXITCODE)
} catch {
    Write-Host "  Hook test threw: $_" -ForegroundColor Red
    $hooksOk = $false
}

# ==========================================================================
# Test 2: per-pane X button actually closes its pane
# ==========================================================================
Write-Host ""
Write-Host "==== Test 2: X button closes the pane it belongs to ===="
$xClickOk = $false
$xDetail = ''
$p = $null
try {
    Stop-Perch; Reset-PerchState
    $p = Start-PerchForTest
    if (-not (Wait-ForControlPipe)) { throw "control pipe never appeared (PERCH_ENABLE_TEST_IPC not honored?)" }
    Hide-Perchdow -P $p

    # Drive 2 splits via the control pipe → 3 panes total. Even minimized,
    # WPF processes the dispatcher actions and updates the visual tree (UIA
    # still sees the buttons), so we don't need to restore the window.
    Invoke-PerchTest 'split-right'; Start-Sleep -Milliseconds 200
    Invoke-PerchTest 'split-right'; Start-Sleep -Milliseconds 200

    $win = Get-PerchAutomationRoot -ProcId $p.Id
    if (-not $win) { throw "perch window not visible to UIA" }
    $before = Find-ClosePaneButtons -Window $win
    Write-Host "  $($before.Count) 'Close pane' buttons before UIA Invoke"
    if ($before.Count -lt 2) {
        throw "expected at least 2 close buttons after splits, got $($before.Count)"
    }

    # UIA Invoke is the cleanest non-keyboard click — fires the exact same
    # Click handler as a real mouse click on the X.
    $pattern = $before[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 800

    $after = Find-ClosePaneButtons -Window $win
    Write-Host "  $($after.Count) 'Close pane' buttons after UIA Invoke"

    $xClickOk = ($after.Count -eq ($before.Count - 1))
    $xDetail = "before=$($before.Count) after=$($after.Count)"
} catch {
    $xDetail = "exception: $_"
    Write-Host "  $xDetail" -ForegroundColor Red
} finally {
    if ($p) { Stop-Process -Id $p.Id -Force -EA SilentlyContinue }
    Start-Sleep -Milliseconds 300
}

# ==========================================================================
# Test 3: stress for the native AV crash
#
# Drive split/close cycles entirely through the control pipe. Each iteration
# splits twice then closes twice — pane count oscillates 1→2→3→2→1 so the
# final close never targets the last pane (closing the last pane would shut
# down the app and look like a crash to the harness).
# ==========================================================================
Write-Host ""
Write-Host "==== Test 3: $StressIterations split/close cycles ===="
$stressOk = $false
$stressDetail = ''
$startTime = Get-Date
$p = $null
try {
    Stop-Perch; Reset-PerchState
    $p = Start-PerchForTest
    if (-not (Wait-ForControlPipe)) { throw "control pipe never appeared" }
    Hide-Perchdow -P $p

    $iter = 0
    while ($iter -lt $StressIterations) {
        $p.Refresh()
        if ($p.HasExited) { break }
        Invoke-PerchTest 'split-right';       Start-Sleep -Milliseconds 80
        Invoke-PerchTest 'split-down';        Start-Sleep -Milliseconds 80
        Invoke-PerchTest 'close-active-pane'; Start-Sleep -Milliseconds 80
        Invoke-PerchTest 'close-active-pane'; Start-Sleep -Milliseconds 80
        $iter++
    }

    $p.Refresh()
    $crashed = $p.HasExited
    $exitCode = if ($crashed) { $p.ExitCode } else { $null }
    if (-not $crashed) { Stop-Process -Id $p.Id -Force -EA SilentlyContinue }
    Start-Sleep -Milliseconds 600

    $wer = @(Get-WinEvent -FilterHashtable @{
        LogName='Application'
        ProviderName='.NET Runtime','Application Error'
        StartTime=$startTime
    } -EA SilentlyContinue | Where-Object { $_.Message -match 'Perch' })

    $stressOk = (-not $crashed) -and ($wer.Count -eq 0)
    $stressDetail = "iter=$iter crashed=$crashed exitCode=$exitCode wer=$($wer.Count)"
    if ($wer.Count -gt 0) {
        Write-Host "  --- WER events during stress ---"
        foreach ($e in $wer) {
            $msg = ($e.Message -replace "`r?`n",' | ' -replace ' +',' ')
            $msg = $msg.Substring(0, [Math]::Min(360, $msg.Length))
            Write-Host "    [$($e.TimeCreated)] $($e.LevelDisplayName): $msg"
        }
    }
} catch {
    $stressDetail = "exception: $_"
    Write-Host "  $stressDetail" -ForegroundColor Red
} finally {
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -EA SilentlyContinue }
}

# ==========================================================================
# Summary
# ==========================================================================
Write-Host ""
Write-Host "==== Summary ===="
Write-Host "  Test 1  hooks pipeline      : $(if($hooksOk){'PASS'}else{'FAIL'})"
Write-Host "  Test 2  X-button closes pane: $(if($xClickOk){'PASS'}else{'FAIL'})  ($xDetail)"
Write-Host "  Test 3  stress / no crash   : $(if($stressOk){'PASS'}else{'FAIL'})  ($stressDetail)"

if ($hooksOk -and $xClickOk -and $stressOk) {
    Write-Host ""
    Write-Host "ALL PASS" -ForegroundColor Green
    exit 0
}
Write-Host ""
Write-Host "FAIL" -ForegroundColor Red
exit 1
