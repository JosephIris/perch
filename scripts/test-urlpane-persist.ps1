<#
Regression test: switching away from a tab with a URL (WebView2) pane and back
must NOT reload it — the native WebView2 controller is HIDDEN, not disposed.

Decisive signal (capture methods lie in this app; the log does not): the host
logs "UrlPaneHost.Init.begin" once per WebView2 controller creation. Across a
switch-away-and-back cycle it must stay at exactly 1. Before the fix, hideStage
disposed the controller and it was recreated on return -> count would be >= 2.

ISOLATION / SAFETY:
  - Unique PERCH_DATA_DIR keyed to this shell's PID: no WebView2 single-writer
    lock conflict with a live instance, no shared session store.
  - Aborts if a test-IPC control pipe is already up (would drive the wrong app).
  - Kills ONLY the exact PID this script launches (never Get-Process -Name).
  - Window parked far off-screen so nothing churns on the visible desktop, but
    layout/render/resize still run for real (not minimized).

NOTE: the running app serves wwwroot from the BIN OUTPUT, so a page-side change
needs `dotnet build src/Perch/Perch.csproj` (not just `npm run build`) to reach
this test. Build first.
#>
[CmdletBinding()]
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools"
)
$ErrorActionPreference = 'Stop'

$PerchExe = Join-Path $ToolsDir 'perch.exe'
$DataDir  = Join-Path $env:TEMP ("perch-urlpane-{0}" -f $PID)
$LogPath  = Join-Path $DataDir 'perch\errors.log'

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
function Wait-Until { param([scriptblock]$Cond, [int]$TimeoutSec = 10)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try { if (& $Cond) { return $true } } catch {}
        Start-Sleep -Milliseconds 150
    }
    return $false
}
function Get-Dump {
    # Fire a state.dump and read back the LAST STATE_DUMP json line.
    [void](Send-Verb 'state.dump')
    Start-Sleep -Milliseconds 250
    $line = Get-Content $LogPath -EA SilentlyContinue |
        Select-String -Pattern 'STATE_DUMP' -SimpleMatch | Select-Object -Last 1
    if (-not $line) { return $null }
    $json = ($line.Line -replace '^.*STATE_DUMP', '')
    try { return ($json | ConvertFrom-Json) } catch { return $null }
}
function Active-Id { param($dump) ($dump.sessions | Where-Object { $_.active } | Select-Object -First 1).id }

$proc = $null
try {
    # Pre-flight: if a test-IPC control pipe is ALREADY up, another instance owns
    # it and `perch test` verbs could be routed to the wrong app. Abort.
    if (Test-Path '\\.\pipe\perch\control') {
        throw "control pipe already exists — another test-IPC Perch is running. Aborting to avoid driving the wrong instance."
    }

    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'perch') | Out-Null
    $env:PERCH_ENABLE_TEST_IPC = '1'
    $env:PERCH_DATA_DIR        = $DataDir

    Write-Host "Launching isolated Perch (data: $DataDir)"
    $proc = Start-Process -PassThru -FilePath $ExePath
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($proc.HasExited) { throw "Perch exited early (code $($proc.ExitCode))" }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($proc.MainWindowHandle -eq [IntPtr]::Zero) { throw "main window never appeared" }
    # Park far off-screen at a real size (layout/render still run).
    [Perch.WinPos]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, -3400, -3400, 1280, 820,
        ([Perch.WinPos]::NOZORDER -bor [Perch.WinPos]::NOACTIVATE)) | Out-Null
    Write-Host "  pid=$($proc.Id)"

    if (-not (Wait-Until { (Log-Count 'Pane.spawn') -ge 1 } 20)) { throw "initial pane never spawned" }
    Start-Sleep -Milliseconds 500

    # --- 1. Create a URL (WebView2) pane in session 1 -----------------------
    Write-Host "`n[1] split-active --url -> create WebView2 pane"
    if (-not (Send-Verb 'pane.split-active' @{ dir = 'right'; url = 'https://example.com' })) {
        throw "split-active verb rejected"
    }
    if (-not (Wait-Until { (Log-Count 'UrlPaneHost.Init.begin') -ge 1 } 15)) {
        throw "URL pane WebView2 was never created (no Init.begin)"
    }
    $created = Log-Count 'UrlPaneHost.Init.begin'
    Write-Host "  [+] WebView2 created (Init.begin count = $created)"

    $s1 = Active-Id (Get-Dump)
    if (-not $s1) { throw "could not read active session id" }
    Write-Host "  [+] session 1 active: $($s1.Substring(0,8))"

    # --- 2. Switch AWAY to a new session ------------------------------------
    Write-Host "`n[2] session.new -> switch away (stage hidden, pane HIDDEN not disposed)"
    if (-not (Send-Verb 'session.new')) { throw "session.new rejected" }
    if (-not (Wait-Until {
        $d = Get-Dump; $a = Active-Id $d
        $d.sessions.Count -ge 2 -and $a -and $a -ne $s1
    } 10)) { throw "did not switch to a new active session" }
    Write-Host "  [+] switched to session 2: $((Active-Id (Get-Dump)).Substring(0,8))"
    Start-Sleep -Milliseconds 700

    # --- 3. Switch BACK to session 1 ----------------------------------------
    Write-Host "`n[3] session.select session 1 -> switch back (pane RE-SHOWN, no reload)"
    if (-not (Send-Verb 'session.select' @{ id = $s1 })) { throw "session.select rejected" }
    if (-not (Wait-Until { (Active-Id (Get-Dump)) -eq $s1 } 10)) { throw "did not switch back to session 1" }
    Write-Host "  [+] back on session 1"
    Start-Sleep -Milliseconds 800

    # --- 4. Assert: no recreation across the cycle --------------------------
    $after = Log-Count 'UrlPaneHost.Init.begin'
    Write-Host "`n[4] assert WebView2 was NOT recreated"
    Write-Host "  Init.begin: before switch = $created, after switch-back = $after"
    if ($after -ne $created) {
        throw "RELOAD DETECTED: WebView2 controller was recreated ($created -> $after). The pane reloaded."
    }
    Write-Host "  [+] controller count unchanged -> the page stayed loaded, no reload"

    $errs = @(Select-String -Path $LogPath -Pattern '] ERROR' -EA SilentlyContinue)
    if ($errs.Count -gt 0) {
        Write-Host "`n  WARN: ERROR lines in log:" -ForegroundColor Yellow
        $errs | Select-Object -Last 8 | ForEach-Object { Write-Host "    $($_.Line)" -ForegroundColor Yellow }
    }

    Write-Host "`nRESULT: PASS -- URL pane survived a tab switch without reloading" -ForegroundColor Green
    $exit = 0
}
catch {
    Write-Host "`nRESULT: FAIL -- $($_.Exception.Message)" -ForegroundColor Red
    if (Test-Path $LogPath) {
        Write-Host "`n  --- errors.log tail ---" -ForegroundColor Yellow
        Get-Content $LogPath -Tail 25 -EA SilentlyContinue | ForEach-Object { Write-Host "    $_" }
    }
    $exit = 1
}
finally {
    # Kill ONLY the instance this script launched (explicit PID, never by name).
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -EA SilentlyContinue
        Start-Sleep -Milliseconds 400
    }
    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    Remove-Item Env:PERCH_ENABLE_TEST_IPC, Env:PERCH_DATA_DIR -EA SilentlyContinue
}
exit $exit
