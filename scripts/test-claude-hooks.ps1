# Phase 4 live test: Claude Code wrapper + hook event handler.
#
# We don't need the real Claude CLI for this. Instead we stage a tiny
# fake-claude.cmd that echoes its argv to stdout — that's enough to verify:
#   * The perch wrapper finds it on PATH (skipping its own dir)
#   * Inside perch, the wrapper injected --settings <hooks-json>
#   * Outside perch, the wrapper is a transparent passthrough (no --settings)
#
# Separately we exercise `perch hooks claude <event>` directly against a live
# pane — confirming the full hook → IPC → Session → sidebar pipeline.

[CmdletBinding()]
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools",
    [string]$LogPath  = "$env:APPDATA\perch\errors.log",
    [string]$ShotPath = 'C:\tmp\perch-claude-hooks.png'
)

$ErrorActionPreference = 'Stop'

# ---- 1. Fake-claude staging ----
$fakeDir = Join-Path $env:TEMP "perch-fake-claude-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Path $fakeDir | Out-Null
$fakeClaude = Join-Path $fakeDir 'claude.cmd'
@'
@echo off
echo FAKE-CLAUDE-START
:loop
if "%~1"=="" goto end
echo ARG: %1
shift
goto loop
:end
echo FAKE-CLAUDE-END
'@ | Set-Content -Path $fakeClaude -Encoding ASCII

$perchExe = Join-Path $ToolsDir 'perch.exe'
if (-not (Test-Path $perchExe)) { throw "perch.exe not found at $perchExe" }

# ---- 2. Passthrough mode: no PERCH_PIPE → no --settings ----
$env:PERCH_PIPE = $null
$env:PATH = "$ToolsDir;$fakeDir;$env:PATH"
$out1 = & $perchExe wrap-claude --version test-arg 2>&1
$out1Joined = $out1 -join "`n"
Write-Host "=== Passthrough run output ==="
Write-Host $out1Joined
if ($out1Joined -notmatch 'FAKE-CLAUDE-START') {
    throw "passthrough: fake claude was not invoked. Got: $out1Joined"
}
if ($out1Joined -match '--settings') {
    throw "passthrough: --settings should NOT be injected outside perch. Got: $out1Joined"
}
if ($out1Joined -notmatch 'ARG: --version' -or $out1Joined -notmatch 'ARG: test-arg') {
    throw "passthrough: original args not forwarded. Got: $out1Joined"
}
Write-Host "  PASS: passthrough forwards args without injecting --settings"

# ---- 3. Perch-pane mode: PERCH_PIPE set → --settings <path> prepended ----
# The wrapper now writes the JSON to a temp file (path passed as the arg) so
# it survives cmd.exe parsing when real claude is an npm-installed .cmd shim.
$env:PERCH_PIPE  = '\\.\pipe\perch\fake-test'
$env:PERCH_PANE_ID = 'phase4-test'
$out2 = & $perchExe wrap-claude --version 2>&1
$out2Joined = $out2 -join "`n"
Write-Host ""
Write-Host "=== In-perch run output ==="
Write-Host $out2Joined

if ($out2Joined -notmatch 'ARG: --settings') {
    throw "in-perch: wrapper did not inject --settings. Got: $out2Joined"
}
# Extract the path that follows --settings (next ARG: line).
$lines2 = $out2 | Where-Object { $_ -match '^ARG: ' } | ForEach-Object { ($_ -replace '^ARG: ', '') }
$settingsIdx = [Array]::IndexOf($lines2, '--settings')
if ($settingsIdx -lt 0 -or $settingsIdx -ge $lines2.Length - 1) {
    throw "in-perch: couldn't find the --settings file-path argument"
}
$hooksFile = $lines2[$settingsIdx + 1]
if (-not (Test-Path $hooksFile)) {
    throw "in-perch: --settings target '$hooksFile' does not exist"
}
$hooksContent = Get-Content $hooksFile -Raw
Write-Host "  hooks JSON ($hooksFile):"
Write-Host "    $($hooksContent.Substring(0, [Math]::Min(180, $hooksContent.Length)))..."

# PreToolUse is intentionally omitted by the wrapper (would fire many times
# per second during agentic work — see ClaudeWrapper.cs comment). Check only
# the events we actually register.
foreach ($evt in 'SessionStart','Stop','Notification','UserPromptSubmit','SessionEnd','SubagentStop') {
    if ($hooksContent -notmatch $evt) { throw "hooks JSON missing event: $evt" }
}
if ($hooksContent -notmatch 'hooks claude session-start') {
    throw "hooks JSON not pointing callback at our CLI"
}
Write-Host "  PASS: wrapper wrote a settings file with all expected events"

# ---- 4. End-to-end hook dispatch against a live app ----
$env:PERCH_PIPE = $null
if (Test-Path $LogPath) { Remove-Item $LogPath -Force }
$sessionsPath = "$env:APPDATA\perch\sessions.json"
if (Test-Path $sessionsPath) { Remove-Item $sessionsPath -Force }
Get-Process -Name Perch -EA SilentlyContinue | Where-Object { $_.Path -like '*Debug*' } | Stop-Process -Force
Start-Sleep -Milliseconds 500

$p = Start-Process -PassThru -FilePath $ExePath
Start-Sleep -Seconds 4
if ($p.HasExited) { throw "Perch exited $($p.ExitCode)" }
Write-Host ""
Write-Host "Launched pid=$($p.Id)"

$sessions = Get-Content $sessionsPath -Raw | ConvertFrom-Json
$active = $sessions.Sessions | Where-Object { $_.Id -eq $sessions.ActiveSessionId } | Select-Object -First 1
$paneId = $active.Root.Id -replace '-', ''
$pipe = "\\.\pipe\perch\$paneId"

function Send-HookEvent {
    param([string]$EventName, [string]$StdinJson)
    # Windows PowerShell 5.1 (.NET Framework) has no ArgumentList collection
    # on ProcessStartInfo — only the legacy Arguments string. Event names
    # are simple ASCII (session-start, prompt-submit, ...) so quoting is moot.
    $psi = [System.Diagnostics.ProcessStartInfo]::new($perchExe)
    $psi.Arguments = "hooks claude $EventName"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables['PERCH_PIPE'] = $pipe
    $proc = [System.Diagnostics.Process]::Start($psi)
    if ($StdinJson) {
        $proc.StandardInput.Write($StdinJson)
    }
    $proc.StandardInput.Close()
    if (-not $proc.WaitForExit(3000)) { $proc.Kill(); throw "hooks claude $EventName hung" }
    $err = $proc.StandardError.ReadToEnd()
    if ($proc.ExitCode -ne 0) { throw "hooks claude $EventName exited $($proc.ExitCode): $err" }
}

Send-HookEvent -EventName 'session-start' -StdinJson '{}'
Start-Sleep -Milliseconds 200
Send-HookEvent -EventName 'prompt-submit' -StdinJson '{"prompt":"refactor the auth flow"}'
Start-Sleep -Milliseconds 200
Send-HookEvent -EventName 'pre-tool-use' -StdinJson '{"tool_name":"Edit"}'
Start-Sleep -Milliseconds 200
Send-HookEvent -EventName 'notification' -StdinJson '{"message":"Permission required for Bash"}'
Start-Sleep -Milliseconds 200
Send-HookEvent -EventName 'stop' -StdinJson '{}'
Start-Sleep -Milliseconds 400

& "$PSScriptRoot\capture-window.ps1" -ProcessId $p.Id -OutPath $ShotPath -TopMostFirst $true | Out-Null
Write-Host "Screenshot: $ShotPath"

$lines = if (Test-Path $LogPath) { Get-Content $LogPath } else { @() }
Write-Host ""
Write-Host "=== Log (filtered) ==="
$lines | Where-Object { $_ -match 'PerchIpc|ERROR' } | ForEach-Object { Write-Host "  $_" }

Stop-Process -Id $p.Id -EA SilentlyContinue
Remove-Item -Recurse -Force $fakeDir -EA SilentlyContinue

# Each hook event should have produced its mapped IPC verbs. Stop fires
# status; notification fires both notify AND status; etc. Total >= 6.
$statusCount = ($lines | Where-Object { $_ -match 'PerchIpc\.recv.*type=status' }).Count
$notifyCount = ($lines | Where-Object { $_ -match 'PerchIpc\.recv.*type=notify' }).Count
if ($statusCount -lt 4) { throw "FAIL: expected >=4 status dispatches from hooks, got $statusCount" }
if ($notifyCount -lt 1) { throw "FAIL: expected >=1 notify dispatch from hooks, got $notifyCount" }

Write-Host ""
Write-Host "PASS: phase 4 complete."
Write-Host "  - wrapper passthrough works outside perch"
Write-Host "  - wrapper injects --settings JSON inside perch"
Write-Host "  - hooks claude <event> routes to IPC end-to-end (status=$statusCount notify=$notifyCount)"
