<#
Capture the board pane with real content, for the design loop.

Uses the ONE capture method that tells the truth in this app: drive CDP, force
an opaque page background, then screenshot. PrintWindow crops at non-100% DPI
and misses GPU-composited layers; a plain CDP capture paints the Mica-backed
workspace white. See CLAUDE.md "Both capture methods lie".

Writes design-loop/board-current.png. Isolated instance, own data dir, kills only
its own PID - same rules as the test harnesses.
#>
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools",
    [string]$OutFile  = "$PSScriptRoot\..\design-loop\board-current.png",
    [int]$CdpPort = 9336
)
$ErrorActionPreference = 'Stop'

$PerchExe = Join-Path $ToolsDir 'perch.exe'
$DataDir  = Join-Path $env:TEMP ("perch-shot-{0}" -f $PID)
$LogPath  = Join-Path $DataDir 'perch\errors.log'
$RepoDir  = Join-Path $DataDir 'repo'

if (-not (Test-Path $ExePath)) { throw "Perch.exe not found: $ExePath (build first)" }

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
    $a = @('test', $Verb)
    foreach ($k in $Flags.Keys) { $a += "--$k"; $a += [string]$Flags[$k] }
    & $PerchExe @a *> $null
    return ($LASTEXITCODE -eq 0)
}
function Cdp-Eval { param([string]$Expr)
    $out = & node (Join-Path $PSScriptRoot 'cdp-eval.mjs') $Expr 10000 $CdpPort 2>&1
    return ($out | Out-String).Trim()
}
function Wait-Until { param([scriptblock]$Cond, [int]$TimeoutSec = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try { if (& $Cond) { return $true } } catch {}
        Start-Sleep -Milliseconds 200
    }
    return $false
}

$proc = $null
try {
    if (Test-Path '\\.\pipe\perch\control') { throw "control pipe already up - another test instance is running" }

    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'perch') | Out-Null
    New-Item -ItemType Directory -Force -Path $RepoDir | Out-Null
    Push-Location $RepoDir
    try { & git init --quiet 2>$null } finally { Pop-Location }

    $env:PERCH_ENABLE_TEST_IPC = '1'
    $env:PERCH_DATA_DIR        = $DataDir
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$CdpPort"

    $proc = Start-Process -PassThru -FilePath $ExePath
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($proc.HasExited) { throw "Perch exited early ($($proc.ExitCode))" }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 200
    }
    [Perch.WinPos]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, -3400, -3400, 1500, 940,
        ([Perch.WinPos]::NOZORDER -bor [Perch.WinPos]::NOACTIVATE)) | Out-Null

    if (-not (Wait-Until { (Test-Path $LogPath) -and @(Select-String $LogPath -Pattern 'Pane.spawn' -SimpleMatch -EA SilentlyContinue).Count -ge 1 })) {
        throw "initial pane never spawned"
    }
    Start-Sleep -Milliseconds 800
    # A fresh data dir always shows the onboarding overlay, which would cover
    # the pane we came to look at. Dismiss it the way the button does.
    [void](Cdp-Eval "(()=>{const b=document.querySelector('.onboarding-overlay button');if(b){b.click();return 1}return 0})()")
    Start-Sleep -Milliseconds 500
    [void](Send-Verb 'pane.simulate-input' @{ text = "cd '$RepoDir'`r" })
    Start-Sleep -Milliseconds 1400
    [void](Send-Verb 'board.new-active')
    if (-not (Wait-Until { @(Select-String $LogPath -Pattern 'Board.create' -SimpleMatch -EA SilentlyContinue).Count -ge 1 })) {
        throw "board never created"
    }
    Start-Sleep -Milliseconds 900

    # Write a board with one of every node kind, straight to disk, then ask the
    # pane to re-read it. Exercises the real render path with real content.
    $boardDir = (Get-ChildItem (Join-Path $RepoDir '.perch\boards') -Directory | Select-Object -First 1).FullName
    # A real PNG in assets/, so the image card renders an actual picture rather
    # than the "image missing" state.
    $assetsDir = Join-Path $boardDir 'assets'
    New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null
    Add-Type -AssemblyName PresentationCore, WindowsBase
    $w = 480; $h = 300; $stride = $w * 4
    $px = New-Object byte[] ($stride * $h)
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $o = $y * $stride + $x * 4
            # A mock login form: dark panel, two field outlines, an error bar.
            $b = 32; $g = 32; $r = 32
            if ($y -lt 18) { $b = 46; $g = 46; $r = 46 }
            if (($y -ge 90 -and $y -le 92) -or ($y -ge 130 -and $y -le 132)) { if ($x -gt 60 -and $x -lt 420) { $b = 120; $g = 120; $r = 200 } }
            if ($y -ge 60 -and $y -le 132 -and (($x -ge 60 -and $x -le 62) -or ($x -ge 418 -and $x -le 420))) { $b = 120; $g = 120; $r = 200 }
            if ($y -ge 60 -and $y -le 62 -and $x -gt 60 -and $x -lt 420) { $b = 120; $g = 120; $r = 200 }
            if ($y -ge 170 -and $y -le 182 -and $x -ge 60 -and $x -le 260) { $b = 110; $g = 100; $r = 235 }
            $px[$o] = $b; $px[$o+1] = $g; $px[$o+2] = $r; $px[$o+3] = 255
        }
    }
    $bmp = New-Object System.Windows.Media.Imaging.WriteableBitmap $w, $h, 96, 96,
        ([System.Windows.Media.PixelFormats]::Bgra32), $null
    $bmp.WritePixels((New-Object System.Windows.Int32Rect 0, 0, $w, $h), $px, $stride, 0)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $fs = [IO.File]::Create((Join-Path $assetsDir 'login-broken.png'))
    try { $enc.Save($fs) } finally { $fs.Close() }

    $layout = @'
{"v":1,"title":"login bug","nodes":[
{"id":"n1","kind":"note","text":"Session cookie stays HttpOnly. No token in localStorage, that is what we are undoing.","x":16,"y":16},
{"id":"n2","kind":"path","ref":"src/auth/session.ts","text":"where the cookie is set","x":232,"y":16},
{"id":"n3","kind":"image","ref":"assets/login-broken.png","text":"the failure state","x":16,"y":152,"w":260,"h":250},
{"id":"n4","kind":"url","ref":"refs/oauth-browser-apps.md","source":"https://datatracker.ietf.org/doc/html/rfc8252","fetchedUtc":"2026-07-29","text":"section 6.2","x":292,"y":152}
],"links":[{"from":"n3","to":"n2","label":"shows"}]}
'@
    $md = "# login bug`n`n## Files`n- ``src/auth/session.ts`` - where the cookie is set`n`n<!-- perch:layout`n$layout`n-->`n"
    Set-Content -Path (Join-Path $boardDir 'board.md') -Value $md -Encoding utf8

    [void](Cdp-Eval "(()=>{const p=document.querySelector('.pane--board');window.chrome.webview.postMessage(JSON.stringify({type:'board.request',paneId:p.dataset.paneId}));return 1;})()")
    Start-Sleep -Milliseconds 1200

    # Opaque background first, else the workspace composites as white and the
    # hairlines and light text vanish into it.
    [void](Cdp-Eval "document.documentElement.style.background='#1f1f1f';1")
    Start-Sleep -Milliseconds 300

    $b64 = & node (Join-Path $PSScriptRoot 'cdp-shot.mjs') $CdpPort
    if (-not $b64 -or $b64.StartsWith('ERR')) { throw "capture failed: $b64" }
    [IO.File]::WriteAllBytes($OutFile, [Convert]::FromBase64String($b64.Trim('"')))
    Write-Host "wrote $OutFile" -ForegroundColor Green
    $exit = 0
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if (Test-Path $LogPath) { Get-Content $LogPath -Tail 15 | ForEach-Object { Write-Host "  $_" } }
    $exit = 1
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -EA SilentlyContinue; Start-Sleep -Milliseconds 400 }
    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    Remove-Item Env:PERCH_ENABLE_TEST_IPC, Env:PERCH_DATA_DIR,
                Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -EA SilentlyContinue
}
exit $exit
