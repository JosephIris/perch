<#
.SYNOPSIS
Launch an ISOLATED Perch instance for hands-on testing — separate sessions,
settings, WebView2 profile and log under C:\tmp\perch-test, so it never touches
your real/prod Perch (no shared state, no WebView2 lock conflict).

State PERSISTS between runs of this script, so you can test the full
close-the-app → reopen → "Resume previous sessions?" flow: just run it again
(without -Fresh) after closing the window.

.PARAMETER Fresh
Wipe the test data dir first (start from a clean seeded "main" session).

.PARAMETER Mock
Swap in a mock `claude` so you can exercise capture/resume WITHOUT a real
Claude Code session (no tokens, instant). The mock fires the SessionStart hook
and handles `--resume`. The real shim is restored when you close the window
(this script waits for exit in -Mock mode). Omit to use your REAL claude.

.EXAMPLE
  ./scripts/run-test-instance.ps1                 # real claude, persistent state
  ./scripts/run-test-instance.ps1 -Fresh          # clean slate
  ./scripts/run-test-instance.ps1 -Mock -Fresh    # token-free resume demo
#>
[CmdletBinding()]
param(
    [switch]$Fresh,
    [switch]$Mock,
    [string]$OutDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64"
)

$ErrorActionPreference = 'Stop'
$ExePath   = Join-Path $OutDir 'Perch.exe'
$ClaudeCmd = Join-Path $OutDir 'tools\claude.cmd'
$ClaudeBak = Join-Path $OutDir 'tools\claude.cmd.realbak'
$DataDir   = 'C:\tmp\perch-test'

if (-not (Test-Path $ExePath)) {
    throw "Perch.exe not found at $ExePath. Build first: dotnet build src/Perch/Perch.csproj -c Debug"
}

# Stop a PRIOR test instance (same isolated data dir would otherwise hit a
# WebView2 single-writer lock). Narrow to bin\Debug builds — never touches an
# installed/prod Perch.
Get-Process -Name Perch -EA SilentlyContinue |
    Where-Object { $_.Path -like '*\bin\Debug\*' } |
    Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Milliseconds 400

if ($Fresh) {
    Remove-Item (Join-Path $DataDir 'perch') -Recurse -Force -EA SilentlyContinue
    Write-Host "Wiped test data dir." -ForegroundColor Yellow
}
New-Item -ItemType Directory -Force (Join-Path $DataDir 'perch') | Out-Null

# Isolation: redirect ALL per-user data (sessions/settings/WebView2/log) here.
$env:PERCH_DATA_DIR = $DataDir

$mockText = @'
@echo off
rem MOCK claude (test instance) — simulates Claude Code's SessionStart hook so
rem the host captures a session id, writes a stub transcript so the resume
rem pre-flight passes, and handles `claude --resume <id>`.
setlocal
if "%~1"=="--resume" (
  echo [mock-claude] RESUMED %~2
  echo {"session_id":"%~2","source":"resume"} | "%~dp0perch.exe" hooks claude session-start
) else (
  if not exist "%CLAUDE_CONFIG_DIR%\projects\test" mkdir "%CLAUDE_CONFIG_DIR%\projects\test"
  echo {"type":"summary"} > "%CLAUDE_CONFIG_DIR%\projects\test\%PERCH_TEST_SID%.jsonl"
  echo [mock-claude] STARTED session %PERCH_TEST_SID% -- reopen the app to resume it
  echo {"session_id":"%PERCH_TEST_SID%","source":"startup"} | "%~dp0perch.exe" hooks claude session-start
)
rem keep the "agent" alive so it looks like a running session until you exit
cmd /k
endlocal
'@

if ($Mock) {
    if (-not (Test-Path $ClaudeBak)) { Copy-Item $ClaudeCmd $ClaudeBak -Force }
    Set-Content -Path $ClaudeCmd -Value $mockText -Encoding ascii
    # Mock needs a fixed session id + an isolated Claude config root (so its
    # stub transcripts and the host's resume pre-flight agree, without touching
    # your real ~/.claude).
    $env:PERCH_TEST_SID = "mock-$(Get-Random)"
    $env:CLAUDE_CONFIG_DIR = Join-Path $DataDir 'claude'
    New-Item -ItemType Directory -Force (Join-Path $DataDir 'claude\projects\test') | Out-Null
    Write-Host "Mock claude staged (real shim restored on exit)." -ForegroundColor Cyan
}

Write-Host ""
Write-Host "Launching ISOLATED test Perch  (data: $DataDir)" -ForegroundColor Green
Write-Host "  Prod Perch (%APPDATA%\perch) is untouched."
Write-Host ""
Write-Host "Try:" -ForegroundColor Green
Write-Host "  - Run '$(if($Mock){'claude'}else{'claude'})' in a pane to start an agent session."
Write-Host "  - Close a session (X on its sidebar row) -> it drops into 'Recently closed'."
Write-Host "  - Click a 'Recently closed' row -> confirm -> watch the restore lightbox."
Write-Host "  - Close the whole window, then re-run this script -> 'Resume previous sessions?' prompt."
Write-Host ""

$p = Start-Process -PassThru -FilePath $ExePath

if ($Mock) {
    Write-Host "Waiting for you to close the window (then restoring the real claude shim)..." -ForegroundColor DarkGray
    try { Wait-Process -Id $p.Id } catch {}
    finally {
        if (Test-Path $ClaudeBak) { Move-Item $ClaudeBak $ClaudeCmd -Force -EA SilentlyContinue }
        Remove-Item Env:PERCH_DATA_DIR, Env:PERCH_TEST_SID, Env:CLAUDE_CONFIG_DIR -EA SilentlyContinue
        Write-Host "Real claude shim restored." -ForegroundColor Cyan
    }
} else {
    Write-Host "Launched (pid=$($p.Id)). Real claude in use; nothing to clean up." -ForegroundColor DarkGray
}
