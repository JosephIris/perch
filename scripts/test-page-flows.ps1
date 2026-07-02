<#
.SYNOPSIS
Prove the page-only flows the control-pipe smoke (test-smoke.ps1) can't reach:
new-pane chooser, URL pane layout, settings round-trip + "Check now",
commits.request reply, and the launch resume prompt (via mock claude).

Drives the REAL page over CDP (WebView2 --remote-debugging-port via env var):
real keystrokes and real button clicks, so the page itself authors every wire
message the typed host boundary consumes. Isolated under C:\tmp\perch-prove;
kills only the PIDs it starts; restores the real claude shim on exit.

Companion to scripts/cdp-drive.mjs (the node CDP driver). Requires node 22+
(built-in WebSocket). Exit 0 = PASS.

.EXAMPLE
  ./scripts/test-page-flows.ps1
#>
[CmdletBinding()]
param(
    [string]$OutDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64",
    [string]$Driver = "$PSScriptRoot\cdp-drive.mjs"
)

$ErrorActionPreference = 'Stop'
$ExePath   = Join-Path $OutDir 'Perch.exe'
$ClaudeCmd = Join-Path $OutDir 'tools\claude.cmd'
$ClaudeBak = Join-Path $OutDir 'tools\claude.cmd.provebak'
$DataDir   = 'C:\tmp\perch-prove'
$LogPath   = Join-Path $DataDir 'perch\errors.log'

Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $DataDir 'perch') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $DataDir 'claude\projects\test') | Out-Null
# Pre-seed: skip the onboarding lightbox so it doesn't sit over phase A.
Set-Content (Join-Path $DataDir 'perch\settings.json') '{"OnboardingSeen": true}' -Encoding utf8

# Mock claude (same shape as run-test-instance.ps1): fires the session-start
# hook, writes a stub transcript, handles --resume.
$mockText = @'
@echo off
setlocal
if "%~1"=="--resume" (
  echo [mock-claude] RESUMED %~2
  echo {"session_id":"%~2","source":"resume"} | "%~dp0perch.exe" hooks claude session-start
) else (
  if not exist "%CLAUDE_CONFIG_DIR%\projects\test" mkdir "%CLAUDE_CONFIG_DIR%\projects\test"
  echo {"type":"summary"} > "%CLAUDE_CONFIG_DIR%\projects\test\%PERCH_TEST_SID%.jsonl"
  echo [mock-claude] STARTED session %PERCH_TEST_SID%
  echo {"session_id":"%PERCH_TEST_SID%","source":"startup"} | "%~dp0perch.exe" hooks claude session-start
)
cmd /k
endlocal
'@
if (-not (Test-Path $ClaudeBak)) { Copy-Item $ClaudeCmd $ClaudeBak -Force }
Set-Content -Path $ClaudeCmd -Value $mockText -Encoding ascii

$env:PERCH_DATA_DIR = $DataDir
$env:PERCH_ENABLE_TEST_IPC = '1'
$env:PERCH_TEST_SID = "mock-$(Get-Random)"
$env:CLAUDE_CONFIG_DIR = Join-Path $DataDir 'claude'
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = '--remote-debugging-port=9333'

function Assert-Log([string]$Pattern, [string]$What) {
    if (-not (Select-String -Path $LogPath -Pattern $Pattern -Quiet)) { throw "FAIL: $What (missing '$Pattern' in log)" }
}

$p = $null
try {
    # ---- Phase A: chooser, URL pane, update check, commits, mock session ----
    $p = Start-Process -PassThru -FilePath $ExePath
    Write-Host "phase A: pid=$($p.Id)" -ForegroundColor Green
    node $Driver phaseA $DataDir *> "$DataDir\phaseA.out"
    $code = $LASTEXITCODE
    Get-Content "$DataDir\phaseA.out" | ForEach-Object { Write-Host "  $_" }
    if ($code -ne 0) { throw "FAIL: phase A driver exited $code" }
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    Start-Sleep -Milliseconds 800

    Assert-Log 'Pane\.chooser\.choose .* choice=same' 'chooser choice reached the host'
    Assert-Log 'UrlPane\.create' 'urlpane.layout reached the host'

    # ---- Phase B: relaunch -> resume prompt -> real Resume click ----
    $p = Start-Process -PassThru -FilePath $ExePath
    Write-Host "phase B: pid=$($p.Id)" -ForegroundColor Green
    node $Driver phaseB $DataDir *> "$DataDir\phaseB.out"
    $code = $LASTEXITCODE
    Get-Content "$DataDir\phaseB.out" | ForEach-Object { Write-Host "  $_" }
    if ($code -ne 0) { throw "FAIL: phase B driver exited $code" }
    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    Start-Sleep -Milliseconds 500

    Assert-Log 'claude --resume mock-' 'resume spawn used the saved session id'

    $errors = Get-Content $LogPath | Where-Object { $_ -match 'ERROR' }
    if ($errors) { $errors | ForEach-Object { Write-Warning $_ }; throw "FAIL: unexpected errors in log" }

    Write-Host ""
    Write-Host "PASS: chooser, URL pane, update check, commits round-trip, resume prompt — all proven against the real page." -ForegroundColor Green
}
finally {
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -EA SilentlyContinue }
    if (Test-Path $ClaudeBak) { Move-Item $ClaudeBak $ClaudeCmd -Force -EA SilentlyContinue }
    Remove-Item Env:PERCH_DATA_DIR, Env:PERCH_ENABLE_TEST_IPC, Env:PERCH_TEST_SID, Env:CLAUDE_CONFIG_DIR, Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -EA SilentlyContinue
}
