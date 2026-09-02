<#
Capture the team room and the new-bot dialog in the REAL app, for the design
loop and for a truthful "does it render" check.

Same isolation rules as test-team.ps1 (own data dir, fake claude first on PATH,
kills only its own PID) and the one capture method that tells the truth here:
drive CDP, force an opaque page background, then Page.captureScreenshot.

Writes design-loop/team-room-live.png and design-loop/newbot-live.png.
#>
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools",
    [string]$OutDir   = "$PSScriptRoot\..\design-loop",
    [int]$CdpPort = 9337
)
$ErrorActionPreference = 'Stop'

$PerchExe = Join-Path $ToolsDir 'perch.exe'
$DataDir  = Join-Path $env:TEMP ("perch-teamshot-{0}" -f $PID)
$LogPath  = Join-Path $DataDir 'perch\errors.log'
$RepoDir  = Join-Path $DataDir 'repo'
$FakeDir  = Join-Path $DataDir 'fake-claude'

if (-not (Test-Path $ExePath))  { throw "Perch.exe not found: $ExePath (build first)" }
if (-not (Test-Path $PerchExe)) { throw "perch.exe not found: $PerchExe (build first)" }

if (-not ('Perch.WinPos' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace Perch { public static class WinPos {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(
    IntPtr h, IntPtr after, int X, int Y, int cx, int cy, uint flags);
  public const uint NOZORDER = 0x0004; public const uint NOACTIVATE = 0x0010;
} }
'@
}

function Send-Verb {
    param([string]$Verb, [hashtable]$Flags = @{})
    $a = @('test', $Verb)
    foreach ($k in $Flags.Keys) { $a += "--$k"; $a += [string]$Flags[$k] }
    & $PerchExe @a *> $null
    return ($LASTEXITCODE -eq 0)
}
function Log-Count { param([string]$Pat)
    if (-not (Test-Path $LogPath)) { return 0 }
    return @(Select-String -Path $LogPath -Pattern $Pat -SimpleMatch -EA SilentlyContinue).Count
}
function Wait-Until { param([scriptblock]$Cond, [int]$TimeoutSec = 15)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try { if (& $Cond) { return $true } } catch {}
        Start-Sleep -Milliseconds 200
    }
    return $false
}
function Cdp-Eval { param([string]$Expr)
    $out = & node (Join-Path $PSScriptRoot 'cdp-eval.mjs') $Expr 8000 $CdpPort 2>&1
    return ($out | Out-String).Trim()
}
function Shot { param([string]$File)
    $b64 = & node (Join-Path $PSScriptRoot 'cdp-shot.mjs') $CdpPort
    [IO.File]::WriteAllBytes($File, [Convert]::FromBase64String(($b64 | Out-String).Trim().Trim('"')))
    Write-Host "  wrote $File"
}

$proc = $null
$exit = 0
try {
    if (Test-Path '\\.\pipe\perch\control') { throw "control pipe already exists - another test-IPC Perch is running." }

    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'perch'), $RepoDir, $FakeDir, (Join-Path $DataDir 'claude\projects') | Out-Null
    Push-Location $RepoDir
    try {
        & git init --quiet 2>$null
        Set-Content -Path (Join-Path $RepoDir 'README.md') -Value 'scratch' -Encoding utf8
        & git add -A 2>$null; & git -c user.email=t@t -c user.name=t commit -qm init 2>$null
    } finally { Pop-Location }

    $fake = @"
@echo off
setlocal
set SID=
:loop
if "%~1"=="" goto done
if "%~1"=="--session-id" set SID=%~2
if "%~1"=="-p" goto headless
shift
goto loop
:headless
echo not-json
exit /b 0
:done
echo {"session_id":"%SID%","source":"startup"} | "$PerchExe" hooks claude session-start
echo {"notification_type":"idle_prompt"} | "$PerchExe" hooks claude notification
cmd /k
"@
    Set-Content -Path (Join-Path $FakeDir 'claude.cmd') -Value $fake -Encoding ascii

    $env:PERCH_ENABLE_TEST_IPC = '1'
    $env:PERCH_DATA_DIR        = $DataDir
    $env:CLAUDE_CONFIG_DIR     = Join-Path $DataDir 'claude'
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$CdpPort"
    $oldPath = $env:PATH
    $env:PATH = "$FakeDir;$oldPath"

    Write-Host "Launching isolated Perch (data: $DataDir)"
    $proc = Start-Process -PassThru -FilePath $ExePath
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($proc.HasExited) { throw "Perch exited early (code $($proc.ExitCode))" }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($proc.MainWindowHandle -eq [IntPtr]::Zero) { throw "main window never appeared" }
    [Perch.WinPos]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, -3400, -3400, 1400, 900,
        ([Perch.WinPos]::NOZORDER -bor [Perch.WinPos]::NOACTIVATE)) | Out-Null
    if (-not (Wait-Until { (Log-Count 'Pane.spawn') -ge 1 } 25)) { throw "initial pane never spawned" }
    Start-Sleep -Milliseconds 600

    if (-not (Send-Verb 'project.add' @{ path = $RepoDir })) { throw "project.add rejected" }
    $projectsJson = Join-Path $DataDir 'perch\projects.json'
    if (-not (Wait-Until { (Test-Path $projectsJson) -and ((Get-Content $projectsJson -Raw) -match 'repo') } 10)) { throw "no project" }
    $proj = (Get-Content $projectsJson -Raw | ConvertFrom-Json).projects | Where-Object { $_.path -like "*repo*" } | Select-Object -First 1
    $pid2 = [string]$proj.id
    [void](Send-Verb 'ui.mode' @{ mode = 'projects' })

    $teamDir = Join-Path $RepoDir '.perch\team'
    New-Item -ItemType Directory -Force -Path (Join-Path $teamDir 'positions\frontend-dev'), (Join-Path $teamDir 'positions\backend-dev') | Out-Null
    Set-Content -Path (Join-Path $teamDir 'team.json') -Encoding utf8 -Value @'
{"v":1,"positions":[
 {"slug":"frontend-dev","name":"Frontend dev","purpose":"Owns the sidebar, the panes, the dialogs and the CSS tokens under src/web.","referenceRepo":"","model":"","createdAtMs":1,"briefGeneratedAtMs":0,"briefModel":""},
 {"slug":"backend-dev","name":"Backend dev","purpose":"Owns the WPF host, the IPC pipes, the hook handler and the CLI.","referenceRepo":"","model":"","createdAtMs":1,"briefGeneratedAtMs":0,"briefModel":""}],
 "bots":[]}
'@
    Set-Content -Path (Join-Path $teamDir 'positions\frontend-dev\brief.md') -Value "## Role`nYou own src/web." -Encoding utf8
    Set-Content -Path (Join-Path $teamDir 'positions\backend-dev\brief.md') -Value "## Role`nYou own src/Perch." -Encoding utf8

    [void](Send-Verb 'team.bot.create' @{ projectId = $pid2; nickname = 'Ada'; positionSlug = 'frontend-dev'; worktree = 'false' })
    if (-not (Wait-Until { (Log-Count 'type=session') -ge 1 } 30)) { throw "Ada never came up" }
    [void](Send-Verb 'team.bot.create' @{ projectId = $pid2; nickname = 'Bo'; positionSlug = 'backend-dev'; worktree = 'false' })
    if (-not (Wait-Until { (Log-Count 'type=session') -ge 2 } 30)) { throw "Bo never came up" }
    Start-Sleep -Milliseconds 800
    [void](Send-Verb 'team.post' @{ projectId = $pid2; text = 'Introduce yourselves and say what you own.'; to = 'everyone'; clientId = 's1' })
    Start-Sleep -Milliseconds 500
    [void](Send-Verb 'team.post' @{ projectId = $pid2; text = 'The sidebar row is misaligned by 2px on hover.'; clientId = 's2' })
    if (-not (Wait-Until { (Log-Count 'Team.route:') -ge 1 } 40)) { Write-Host "  (router did not log)" }
    Start-Sleep -Milliseconds 800

    [void](Cdp-Eval "(()=>{document.documentElement.style.background='#1f1f1f';return 1})()")
    # A fresh data dir shows the first-launch lightbox; dismiss it or it covers the room.
    [void](Cdp-Eval "(()=>{const b=[...document.querySelectorAll('button')].find(x=>/get started/i.test(x.textContent||''));if(b){b.click();return 1}return 0})()")
    Start-Sleep -Milliseconds 700
    $rows = Cdp-Eval "document.querySelectorAll('.team-row').length"
    Write-Host "  team rows in the sidebar: $rows"
    [void](Cdp-Eval "(()=>{const r=document.querySelector('.team-row');if(r){r.click();return 1}return 0})()")
    if (-not (Wait-Until { (Cdp-Eval "document.querySelectorAll('.team-room').length") -eq '1' } 10)) { throw "the room did not open" }
    Start-Sleep -Milliseconds 2500
    Write-Host "  feed rows: $(Cdp-Eval "document.querySelectorAll('.team-feed > *').length")  roster rows: $(Cdp-Eval "document.querySelectorAll('.roster-bot').length")"
    Shot (Join-Path $OutDir 'team-room-live.png')

    [void](Cdp-Eval "(()=>{const b=document.querySelector('.team-roster__add');if(b){b.click();return 1}return 0})()")
    if (-not (Wait-Until { (Cdp-Eval "document.querySelectorAll('.newbot-card').length") -eq '1' } 10)) { throw "the dialog did not open" }
    Start-Sleep -Milliseconds 600
    Shot (Join-Path $OutDir 'newbot-live.png')
    Write-Host "  --- team log lines ---"
    Select-String -Path $LogPath -Pattern 'team\.|Team\.|TEAM_' -EA SilentlyContinue | Select-Object -Last 12 | ForEach-Object { Write-Host "    $($_.Line)" }
    Write-Host "RESULT: PASS"
}
catch {
    Write-Host "`nRESULT: FAIL -- $($_.Exception.Message)" -ForegroundColor Red
    if (Test-Path $LogPath) { Get-Content $LogPath -Tail 20 -EA SilentlyContinue | ForEach-Object { Write-Host "    $_" } }
    $exit = 1
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -EA SilentlyContinue; Start-Sleep -Milliseconds 400 }
    foreach ($m in (Get-ChildItem -Path $env:TEMP -Filter 'perch-claude-brief-*.txt' -EA SilentlyContinue |
        Where-Object { (Get-Content $_.FullName -Raw -EA SilentlyContinue) -like "$RepoDir*" })) { Remove-Item $m.FullName -Force -EA SilentlyContinue }
    if ($oldPath) { $env:PATH = $oldPath }
    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    Remove-Item Env:PERCH_ENABLE_TEST_IPC, Env:PERCH_DATA_DIR, Env:CLAUDE_CONFIG_DIR,
                Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -EA SilentlyContinue
}
exit $exit
