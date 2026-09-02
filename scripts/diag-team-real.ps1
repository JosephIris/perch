<#
Diagnostic: launch ONE real-claude bot in an isolated Perch and photograph its
pane (CDP) so whatever Claude Code printed at startup can be read.
Writes design-loop/diag-bot-pane.png and diag-bot-pane-2.png (after an Enter).
#>
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools",
    [int]$CdpPort = 9339
)
$ErrorActionPreference = 'Stop'
$PerchExe = Join-Path $ToolsDir 'perch.exe'
$DataDir  = Join-Path $env:TEMP ("perch-diag-team-{0}" -f $PID)
$LogPath  = Join-Path $DataDir 'perch\errors.log'
$RepoDir  = Join-Path $DataDir 'notes-app'
$OutDir   = "$PSScriptRoot\..\design-loop"

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
function Send-Verb { param([string]$Verb, [hashtable]$Flags = @{})
    $a = @('test', $Verb); foreach ($k in $Flags.Keys) { $a += "--$k"; $a += [string]$Flags[$k] }
    & $PerchExe @a *> $null; return ($LASTEXITCODE -eq 0) }
function Log-Count { param([string]$Pat)
    if (-not (Test-Path $LogPath)) { return 0 }
    return @(Select-String -Path $LogPath -Pattern $Pat -SimpleMatch -EA SilentlyContinue).Count }
function Wait-Until { param([scriptblock]$Cond, [int]$TimeoutSec = 15)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) { try { if (& $Cond) { return $true } } catch {}; Start-Sleep -Milliseconds 300 }
    return $false }
function Cdp-Eval { param([string]$Expr)
    $out = & node (Join-Path $PSScriptRoot 'cdp-eval.mjs') $Expr 8000 $CdpPort 2>&1; return ($out | Out-String).Trim() }
function Shot { param([string]$File)
    $b64 = & node (Join-Path $PSScriptRoot 'cdp-shot.mjs') $CdpPort
    [IO.File]::WriteAllBytes($File, [Convert]::FromBase64String(($b64 | Out-String).Trim().Trim('"')))
    Write-Host "  wrote $File" }

$proc = $null
try {
    if (Test-Path '\\.\pipe\perch\control') { throw "control pipe already exists" }
    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'perch'), (Join-Path $RepoDir 'src\web') | Out-Null
    Set-Content -Path (Join-Path $RepoDir 'README.md') -Value '# notes-app' -Encoding utf8
    Push-Location $RepoDir; try { & git init --quiet 2>$null; & git add -A 2>$null; & git -c user.email=t@t -c user.name=t commit -qm init 2>$null } finally { Pop-Location }

    $env:PERCH_ENABLE_TEST_IPC = '1'; $env:PERCH_DATA_DIR = $DataDir
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$CdpPort"
    $proc = Start-Process -PassThru -FilePath $ExePath
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) { $proc.Refresh(); if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }; Start-Sleep -Milliseconds 200 }
    [Perch.WinPos]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, -3400, -3400, 1400, 900, ([Perch.WinPos]::NOZORDER -bor [Perch.WinPos]::NOACTIVATE)) | Out-Null
    if (-not (Wait-Until { (Log-Count 'Pane.spawn') -ge 1 } 25)) { throw "initial pane never spawned" }
    Start-Sleep -Milliseconds 600
    [void](Send-Verb 'project.add' @{ path = $RepoDir })
    $projectsJson = Join-Path $DataDir 'perch\projects.json'
    if (-not (Wait-Until { (Test-Path $projectsJson) -and ((Get-Content $projectsJson -Raw) -match 'notes-app') } 10)) { throw "no project" }
    $proj = (Get-Content $projectsJson -Raw | ConvertFrom-Json).projects | Where-Object { $_.path -like "*notes-app*" } | Select-Object -First 1
    $pid2 = [string]$proj.id
    $teamDir = Join-Path $RepoDir '.perch\team'
    New-Item -ItemType Directory -Force -Path (Join-Path $teamDir 'positions\frontend-dev') | Out-Null
    Set-Content -Path (Join-Path $teamDir 'team.json') -Encoding utf8 -Value '{"v":1,"positions":[{"slug":"frontend-dev","name":"Frontend dev","purpose":"Owns src/web.","referenceRepo":"","model":"","createdAtMs":1,"briefGeneratedAtMs":0,"briefModel":""}],"bots":[]}'
    Set-Content -Path (Join-Path $teamDir 'positions\frontend-dev\brief.md') -Value "## Role`nYou own src/web." -Encoding utf8

    [void](Send-Verb 'team.bot.create' @{ projectId = $pid2; nickname = 'Ada'; positionSlug = 'frontend-dev'; worktree = 'false' })
    Start-Sleep -Seconds 14
    [void](Cdp-Eval "(()=>{document.documentElement.style.background='#1f1f1f';return 1})()")
    [void](Cdp-Eval "(()=>{const b=[...document.querySelectorAll('button')].find(x=>/get started/i.test(x.textContent||''));if(b){b.click();return 1}return 0})()")
    Start-Sleep -Milliseconds 800
    Shot (Join-Path $OutDir 'diag-bot-pane.png')
    Write-Host "  session hooks so far: $(Log-Count 'type=session')"
    [void](Send-Verb 'pty.send' @{ text = "`r" })
    Start-Sleep -Seconds 12
    Shot (Join-Path $OutDir 'diag-bot-pane-2.png')
    Write-Host "  session hooks after Enter: $(Log-Count 'type=session')"
    Select-String -Path $LogPath -Pattern 'Setup:|type=session|ERROR' -EA SilentlyContinue | Select-Object -Last 6 | ForEach-Object { Write-Host "    $($_.Line)" }
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -EA SilentlyContinue; Start-Sleep -Milliseconds 600 }
    foreach ($m in (Get-ChildItem -Path $env:TEMP -Filter 'perch-claude-brief-*.txt' -EA SilentlyContinue | Where-Object { (Get-Content $_.FullName -Raw -EA SilentlyContinue) -like "$RepoDir*" })) { Remove-Item $m.FullName -Force -EA SilentlyContinue }
    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    Remove-Item Env:PERCH_ENABLE_TEST_IPC, Env:PERCH_DATA_DIR, Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -EA SilentlyContinue
}
