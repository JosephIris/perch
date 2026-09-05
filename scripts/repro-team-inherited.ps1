<#
Reproduce "I pulled a repo that carries a team; the room opens but the roster
is empty". Copies the SHARED half of a real team folder (team.json, positions;
never local/) into a scratch repo, opens the room in an isolated dev Perch,
counts roster rows, captures page errors, and photographs the room.
#>
param(
    [string]$TeamDir  = "C:\Users\josep\dev-projects\product-tools-prod\.perch\team",
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools",
    [int]$CdpPort = 9340
)
$ErrorActionPreference = 'Stop'
$PerchExe = Join-Path $ToolsDir 'perch.exe'
$DataDir  = Join-Path $env:TEMP ("perch-repro-team-{0}" -f $PID)
$LogPath  = Join-Path $DataDir 'perch\errors.log'
$RepoDir  = Join-Path $DataDir 'repo'
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
    New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'perch'), $RepoDir | Out-Null
    Set-Content -Path (Join-Path $RepoDir 'README.md') -Value '# repo' -Encoding utf8
    Push-Location $RepoDir; try { & git init --quiet 2>$null; & git add -A 2>$null; & git -c user.email=t@t -c user.name=t commit -qm init 2>$null } finally { Pop-Location }
    # The shared half only, exactly what a pull brings.
    $dst = Join-Path $RepoDir '.perch\team'
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item (Join-Path $TeamDir 'team.json') $dst
    if (Test-Path (Join-Path $TeamDir 'positions')) { Copy-Item (Join-Path $TeamDir 'positions') $dst -Recurse }
    if (Test-Path (Join-Path $TeamDir 'bots')) { Copy-Item (Join-Path $TeamDir 'bots') $dst -Recurse }
    if (Test-Path (Join-Path $TeamDir 'tasks.json')) { Copy-Item (Join-Path $TeamDir 'tasks.json') $dst }
    Write-Host "  copied: $((Get-ChildItem $dst -Recurse -File | ForEach-Object { $_.FullName.Substring($dst.Length+1) }) -join ', ')"

    $env:PERCH_ENABLE_TEST_IPC = '1'; $env:PERCH_DATA_DIR = $DataDir
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$CdpPort"
    $proc = Start-Process -PassThru -FilePath $ExePath
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) { $proc.Refresh(); if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }; Start-Sleep -Milliseconds 200 }
    [Perch.WinPos]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, -3400, -3400, 1400, 900, ([Perch.WinPos]::NOZORDER -bor [Perch.WinPos]::NOACTIVATE)) | Out-Null
    if (-not (Wait-Until { (Log-Count 'Pane.spawn') -ge 1 } 25)) { throw "initial pane never spawned" }
    Start-Sleep -Milliseconds 600
    [void](Cdp-Eval "(()=>{window.__errs=[];window.addEventListener('error',e=>window.__errs.push(String(e.message)+' @ '+String(e.filename)+':'+e.lineno));window.addEventListener('unhandledrejection',e=>window.__errs.push('rej '+String(e.reason)));return 1})()")
    [void](Send-Verb 'project.add' @{ path = $RepoDir })
    $projectsJson = Join-Path $DataDir 'perch\projects.json'
    if (-not (Wait-Until { (Test-Path $projectsJson) -and ((Get-Content $projectsJson -Raw) -match 'repo') } 10)) { throw "no project" }
    [void](Send-Verb 'ui.mode' @{ mode = 'projects' })
    Start-Sleep -Seconds 2
    [void](Cdp-Eval "(()=>{document.documentElement.style.background='#1f1f1f';return 1})()")
    [void](Cdp-Eval "(()=>{const b=[...document.querySelectorAll('button')].find(x=>/get started/i.test(x.textContent||''));if(b){b.click();return 1}return 0})()")
    Start-Sleep -Milliseconds 800
    Write-Host "  team rows in sidebar: $(Cdp-Eval "document.querySelectorAll('.team-row').length")"
    Write-Host "  state team bots: $(Cdp-Eval "(()=>{try{const s=window.__lastState;return s?JSON.stringify((s.projects||[]).map(p=>({n:p.name,bots:(p.team&&p.team.bots||[]).length}))):'no __lastState'}catch(e){return 'err '+e}})()")"
    [void](Cdp-Eval "(()=>{const r=document.querySelector('.team-row');if(r){r.click();return 1}return 0})()")
    Start-Sleep -Seconds 3
    Write-Host "  room open: $(Cdp-Eval "document.querySelectorAll('.team-room').length")  roster rows: $(Cdp-Eval "document.querySelectorAll('.roster-bot').length")  roster count text: $(Cdp-Eval "(document.querySelector('.team-roster__count')||{}).textContent||''")"
    Write-Host "  empty state visible: $(Cdp-Eval "(()=>{const e=document.querySelector('.team-room__empty');return e?String(!e.hidden):'none'})()")"
    Write-Host "  page errors: $(Cdp-Eval "JSON.stringify(window.__errs||[])")"
    Shot (Join-Path $OutDir 'repro-inherited-room.png')
    Select-String -Path $LogPath -Pattern 'Team\.|team\.|ERROR' -EA SilentlyContinue | Select-Object -Last 8 | ForEach-Object { Write-Host "    $($_.Line)" }
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -EA SilentlyContinue; Start-Sleep -Milliseconds 600 }
    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    Remove-Item Env:PERCH_ENABLE_TEST_IPC, Env:PERCH_DATA_DIR, Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -EA SilentlyContinue
}
