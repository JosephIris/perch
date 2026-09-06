<#
.SYNOPSIS
Proves resume-on-open on a live, ISOLATED Perch, with a mock claude (no tokens).

What replaced what: there used to be a launch dialog — "24 agent sessions
across 24 projects can be reopened", one Resume button. It described something
the app never did. Accepting it reopened nothing; it ARMED every pane, and each
came back only when you clicked its tab. So the dialog asked one global question
whose real answer was per-tab and deferred.

Now: launch arms silently, the sidebar marks each tab that will pick up where it
left off, opening one resumes it, and right-click → "Open as a fresh shell" is
the per-tab no. This proves all four.

Runs against C:\tmp\perch-resume-test — your real Perch is untouched.
#>
param(
    [string]$OutDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64",
    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
$ExePath   = Join-Path $OutDir 'Perch.exe'
$PerchExe  = Join-Path $OutDir 'tools\perch.exe'
$ClaudeCmd = Join-Path $OutDir 'tools\claude.cmd'
$ClaudeBak = Join-Path $OutDir 'tools\claude.cmd.resumebak'
$DataDir   = 'C:\tmp\perch-resume-test'
$LogPath   = Join-Path $DataDir 'perch\errors.log'
$Eval      = Join-Path $PSScriptRoot 'cdp-eval.mjs'

if (-not (Test-Path $ExePath)) { throw "Perch.exe not found at $ExePath (build first)" }

function Send-Verb {
    param([string]$Verb, [hashtable]$Flags = @{})
    $a = @('test', $Verb)
    foreach ($k in $Flags.Keys) { $a += "--$k"; $a += [string]$Flags[$k] }
    & $PerchExe @a *> $null
    return ($LASTEXITCODE -eq 0)
}
function Log-Has { param([string]$Pat)
    if (-not (Test-Path $LogPath)) { return $false }
    return $null -ne (Select-String -Path $LogPath -Pattern $Pat -EA SilentlyContinue | Select-Object -First 1)
}
function Log-Last { param([string]$Pat)
    $m = Select-String -Path $LogPath -Pattern $Pat -EA SilentlyContinue | Select-Object -Last 1
    if ($m) { return $m.Line } else { return '' }
}
function Wait-Until { param([scriptblock]$Cond, [int]$TimeoutSec = 25)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try { if (& $Cond) { return $true } } catch {}
        Start-Sleep -Milliseconds 500
    }
    return $false
}
function Page { param([string]$Expr) return (& node $Eval $Expr 8000 9333) }
function State {
    [void](Send-Verb 'state.dump')
    Start-Sleep -Milliseconds 700
    $line = Log-Last 'STATE_DUMP'
    if (-not $line) { throw "state.dump produced nothing" }
    return (($line -split 'STATE_DUMP', 2)[1] | ConvertFrom-Json)
}
$script:Fails = 0
function Check { param([string]$Name, [bool]$Ok, [string]$Detail = '')
    if ($Ok) { Write-Host "  PASS  $Name" -ForegroundColor Green }
    else { $script:Fails++; Write-Host "  FAIL  $Name  $Detail" -ForegroundColor Red }
}

# A mock claude: fires the SessionStart hook, writes a stub transcript (the
# resume pre-flight requires one on disk), and handles --resume. No tokens.
$mock = @'
@echo off
setlocal
if "%~1"=="--resume" (
  echo [mock-claude] RESUMED %~2
  echo {"session_id":"%~2","source":"resume"} | "%~dp0perch.exe" hooks claude session-start
) else (
  if not exist "%CLAUDE_CONFIG_DIR%\projects\test" mkdir "%CLAUDE_CONFIG_DIR%\projects\test"
  echo {"type":"summary"} > "%CLAUDE_CONFIG_DIR%\projects\test\%PERCH_TEST_SID%.jsonl"
  echo [mock-claude] STARTED %PERCH_TEST_SID%
  echo {"session_id":"%PERCH_TEST_SID%","source":"startup"} | "%~dp0perch.exe" hooks claude session-start
)
cmd /k
endlocal
'@

Get-Process -Name Perch -EA SilentlyContinue |
    Where-Object { $_.Path -like '*\bin\Debug\*' } | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Milliseconds 400
Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $DataDir 'perch'), (Join-Path $DataDir 'claude\projects\test') | Out-Null

if (-not (Test-Path $ClaudeBak)) { Copy-Item $ClaudeCmd $ClaudeBak -Force }
Set-Content -Path $ClaudeCmd -Value $mock -Encoding ascii

$SID = "mock-$(Get-Random)"
$env:PERCH_DATA_DIR = $DataDir
$env:PERCH_ENABLE_TEST_IPC = '1'
$env:PERCH_TEST_SID = $SID
$env:CLAUDE_CONFIG_DIR = Join-Path $DataDir 'claude'
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = '--remote-debugging-port=9333'
Remove-Item Env:PERCH_PIPE, Env:PERCH_PANE_ID -EA SilentlyContinue

$app = $null
try {
    # ---- Run 1: start an agent so there is a conversation to come back to ----
    Write-Host "`nRun 1: start a mock agent" -ForegroundColor Cyan
    $app = Start-Process -PassThru -FilePath $ExePath
    if (-not (Wait-Until { Send-Verb 'state.dump' } 40)) { throw "control pipe never answered" }
    Start-Sleep -Seconds 4
    [void](Send-Verb 'pty.send' @{ text = "claude`r" })
    Check "the agent's session id was captured" (Wait-Until { Log-Has "type=session" } 30) ''
    # A second tab, left active. On relaunch THAT one opens itself and the agent
    # tab is the one nobody has touched - and an untouched tab is the only kind
    # that can wear the mark, because a running pane has nothing left to pick up.
    [void](Send-Verb 'session.new')
    Start-Sleep -Seconds 2
    Stop-Process -Id $app.Id -Force -EA SilentlyContinue
    Start-Sleep -Seconds 2
    Remove-Item $LogPath -Force -EA SilentlyContinue

    # ---- Run 2: no dialog, a marked tab, and a resume on open ----------------
    Write-Host "`nRun 2: relaunch" -ForegroundColor Cyan
    $app = Start-Process -PassThru -FilePath $ExePath
    if (-not (Wait-Until { Send-Verb 'state.dump' } 40)) { throw "control pipe never answered" }

    Check "launch armed the pane, silently" `
        (Wait-Until { Log-Has 'Resume\.gate: enabled=True armed=[1-9]' } 20) "$(Log-Last 'Resume.gate')"

    # 1. No dialog. The old build put a confirm card on screen at this point.
    Start-Sleep -Seconds 3
    $dialog = (Page 'document.querySelectorAll(".confirm-card").length').Trim()
    Check "no launch dialog is in the way" ($dialog -eq '0') "confirm-card count=$dialog"

    # 2. The tab wears the mark instead.
    $marked = Wait-Until {
        (Page '[...document.querySelectorAll(".session-item__meta-item")].some(e => e.textContent.includes("resumes"))').Trim() -eq 'true'
    } 25
    Check "the tab is marked as picking up where it left off" $marked ''

    # 3. Opening it is what resumes it. Nothing has run --resume yet.
    Check "nothing resumed before the tab was opened" (-not (Log-Has "claude --resume")) `
        "$(Log-Last 'claude --resume')"
    $agentTab = (State).sessions |
        Where-Object { $_.panes -and $_.panes[0].claudeSessionId -eq $SID } | Select-Object -First 1
    Check "the agent tab is in the sidebar" ($null -ne $agentTab) ''
    if ($agentTab) { [void](Send-Verb 'session.select' @{ id = $agentTab.id }) }
    Check "opening the tab ran claude --resume" `
        (Wait-Until { Log-Has "claude --resume $([regex]::Escape($SID))" } 30) ''

    # 4. Once it is running the mark is gone: there is nothing left to pick up.
    $cleared = Wait-Until {
        (Page '[...document.querySelectorAll(".session-item__meta-item")].some(e => e.textContent.includes("resumes"))').Trim() -eq 'false'
    } 25
    Check "the mark clears once the tab is running" $cleared ''

    # ---- The per-tab no ------------------------------------------------------
    Write-Host "`n'Open as a fresh shell' is the per-tab no" -ForegroundColor Cyan
    Stop-Process -Id $app.Id -Force -EA SilentlyContinue
    Start-Sleep -Seconds 2
    Remove-Item $LogPath -Force -EA SilentlyContinue
    $app = Start-Process -PassThru -FilePath $ExePath
    if (-not (Wait-Until { Send-Verb 'state.dump' } 40)) { throw "control pipe never answered" }
    [void](Wait-Until { Log-Has 'Resume\.gate' } 20)

    $armed = (State).sessions |
        Where-Object { $_.panes -and $_.panes[0].claudeSessionId -eq $SID } | Select-Object -First 1
    Check "the agent tab is back and armed" ($null -ne $armed) ''
    if ($armed) {
        [void](Send-Verb 'session.openFresh' @{ id = $armed.id })
        Check "openFresh disarmed the tab" (Wait-Until { Log-Has 'Resume\.fresh' } 15) ''
        Write-Host "     $(Log-Last 'Resume.fresh')" -ForegroundColor DarkGray
        Start-Sleep -Seconds 6
        Check "it opened as a plain shell (no --resume)" (-not (Log-Has "claude --resume")) `
            "$(Log-Last 'claude --resume')"
    }
}
finally {
    if (-not $KeepOpen -and $app) { Stop-Process -Id $app.Id -Force -EA SilentlyContinue }
    if (Test-Path $ClaudeBak) { Move-Item $ClaudeBak $ClaudeCmd -Force -EA SilentlyContinue }
    Remove-Item Env:PERCH_DATA_DIR, Env:PERCH_ENABLE_TEST_IPC, Env:PERCH_TEST_SID, Env:CLAUDE_CONFIG_DIR -EA SilentlyContinue
}

Write-Host ""
if ($script:Fails -eq 0) { Write-Host "All resume-on-open checks passed." -ForegroundColor Green; exit 0 }
Write-Host "$($script:Fails) check(s) failed." -ForegroundColor Red; exit 1
