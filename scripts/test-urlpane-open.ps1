<#
End-to-end: does a browser (URL) pane ACTUALLY LOAD?

Motivating bug: browser panes "opened blank pages in some cases". A blank pane
and a working pane were indistinguishable from outside - the pane div, the
header, and the layout IPC are identical either way, and the WebView2 surface
can't be screenshotted (PrintWindow misses GPU-composited child HWNDs; see
CLAUDE.md "Both capture methods lie"). So this asserts on the host log, which
now records the navigation OUTCOME:

    UrlPaneHost.Init.begin   controller created
    UrlPaneHost.nav.ok       navigation COMPLETED SUCCESSFULLY  <- the real signal
    UrlPaneHost.nav.fail     navigation failed (status=...)
    UrlPane.reject           URL refused by WebUrlPolicy, no WebView2 at all

Cases covered:
  1. http:// page               -> created + nav.ok
  2. local file:// .html        -> created + nav.ok    (the regression: the host
                                   used to allow http/https ONLY, so a report
                                   opened from a terminal link produced a pane
                                   with no WebView2 behind it - a blank page)
  3. refused scheme (about:)    -> UrlPane.reject, no controller, and the page
                                   is told so it can paint a reason
  4. missing file:// target     -> nav.fail, not a silent empty document
  5. two panes at once          -> one controller each, no cross-talk

ISOLATION / SAFETY (same rules as test-urlpane-persist.ps1):
  - Unique PERCH_DATA_DIR keyed to this shell's PID.
  - Aborts if a test-IPC control pipe is already up.
  - Kills ONLY the PID this script launches.
  - Window parked off-screen at a real size so layout/render still run.

NOTE: the running app serves wwwroot from the BIN OUTPUT, so a page-side change
needs `dotnet build src/Perch/Perch.csproj` (not just `npm run build`). Build
first.
#>
# NOTE: no [CmdletBinding()] here on purpose. Under Windows PowerShell 5.1 it
# makes the script advanced-function-bound, and $PSScriptRoot then evaluates to
# EMPTY inside param() defaults -- the default paths silently become "\..\src\..."
# and the script dies with "Perch.exe not found (build first)" on a repo that
# built fine. Verified: same file, only difference is that attribute.
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools",
    # Port for the throwaway loopback HTTP server case 1 points at. The test is
    # deliberately HERMETIC -- it used to hit https://example.com and failed with
    # HostNameNotResolved on any machine or CI runner without DNS egress, which
    # is a false alarm, not a product bug.
    [int]$Port = 47821,
    # Remote-debugging port for the DOM assertion in case 3.
    [int]$CdpPort = 9334
)
$ErrorActionPreference = 'Stop'

$PerchExe = Join-Path $ToolsDir 'perch.exe'
$DataDir  = Join-Path $env:TEMP ("perch-urlopen-{0}" -f $PID)
$LogPath  = Join-Path $DataDir 'perch\errors.log'
$FixDir   = Join-Path $DataDir 'fixtures'

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
function Log-Has { param([string]$Pat) return (Log-Count $Pat) -gt 0 }
function Cdp-Eval { param([string]$Expr)
    # Runs against the app shell's document (not a URL pane's). Returns the
    # JSON-encoded value, or "ERR: ..." which every caller treats as a failure.
    $out = & node (Join-Path $PSScriptRoot 'cdp-eval.mjs') $Expr 8000 $CdpPort 2>&1
    return ($out | Out-String).Trim()
}
function Wait-Until { param([scriptblock]$Cond, [int]$TimeoutSec = 15)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try { if (& $Cond) { return $true } } catch {}
        Start-Sleep -Milliseconds 200
    }
    return $false
}

$fails = @()
function Check { param([string]$Name, [bool]$Ok, [string]$Detail = '')
    if ($Ok) { Write-Host "  [+] $Name" -ForegroundColor Green }
    else {
        Write-Host "  [-] $Name $Detail" -ForegroundColor Red
        $script:fails += $Name
    }
}

$proc = $null
$server = $null
$stopFile = $null
try {
    if (Test-Path '\\.\pipe\perch\control') {
        throw "control pipe already exists - another test-IPC Perch is running. Aborting to avoid driving the wrong instance."
    }

    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'perch') | Out-Null
    New-Item -ItemType Directory -Force -Path $FixDir | Out-Null

    # A real local HTML file, the shape an agent actually writes.
    $goodHtml = Join-Path $FixDir 'report.html'
    Set-Content -Path $goodHtml -Encoding utf8 -Value @'
<!doctype html><meta charset="utf-8"><title>Perch local report</title>
<body style="background:#1f1f1f;color:#eee;font:14px sans-serif">
<h1>local report</h1><p>If this pane is not blank, file:// panes work.</p>
'@
    $missingHtml = Join-Path $FixDir 'does-not-exist.html'

    function To-FileUrl { param([string]$P) return 'file:///' + ($P -replace '\\', '/') }

    # Throwaway loopback web server for case 1. A raw TcpListener, NOT
    # HttpListener: HttpListener needs a netsh urlacl reservation (or admin) to
    # bind a prefix, which would make this script fail for the wrong reason on a
    # normal dev box. It appends a line per served request so the test can also
    # assert the WebView2 really fetched the page, not just that navigation
    # "completed".
    $hitFile  = Join-Path $FixDir 'hits.log'
    $stopFile = Join-Path $FixDir 'server.stop'
    $server = Start-Job -ArgumentList $Port, $hitFile, $stopFile -ScriptBlock {
        param([int]$Port, [string]$HitFile, [string]$StopFile)
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
        $listener.Start()
        $body = '<!doctype html><meta charset="utf-8"><title>perch loopback</title><h1>ok</h1>'
        $head = "HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`n" +
                "Content-Length: $($body.Length)`r`nConnection: close`r`n`r`n"
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($head + $body)
        try {
            # Poll Pending() rather than blocking in AcceptTcpClient: a job parked
            # inside a blocking accept doesn't answer Stop-Job, so the script hung
            # in its finally block with every check already green.
            while (-not (Test-Path $StopFile)) {
                if (-not $listener.Pending()) { Start-Sleep -Milliseconds 50; continue }
                $client = $listener.AcceptTcpClient()
                try {
                    $stream = $client.GetStream()
                    # Drain the request line so the client doesn't see a reset.
                    $buf = New-Object byte[] 4096
                    [void]$stream.Read($buf, 0, $buf.Length)
                    $stream.Write($bytes, 0, $bytes.Length)
                    $stream.Flush()
                    Add-Content -Path $HitFile -Value 'hit'
                } finally { $client.Close() }
            }
        } finally { $listener.Stop() }
    }
    function Served-Count {
        if (-not (Test-Path $hitFile)) { return 0 }
        return @(Get-Content $hitFile -EA SilentlyContinue).Count
    }
    function Port-Open {
        $c = New-Object System.Net.Sockets.TcpClient
        try { $c.Connect('127.0.0.1', $Port); return $c.Connected }
        catch { return $false } finally { $c.Close() }
    }

    $env:PERCH_ENABLE_TEST_IPC = '1'
    $env:PERCH_DATA_DIR        = $DataDir
    # CDP so case 3 can assert on the REAL DOM: the host log proves the URL was
    # refused, but only the DOM proves the pane actually SAYS so instead of
    # sitting there empty -- which is the whole complaint.
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$CdpPort"

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
    [Perch.WinPos]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, -3400, -3400, 1280, 820,
        ([Perch.WinPos]::NOZORDER -bor [Perch.WinPos]::NOACTIVATE)) | Out-Null
    Write-Host "  pid=$($proc.Id)"
    if (-not (Wait-Until { (Log-Count 'Pane.spawn') -ge 1 } 25)) { throw "initial pane never spawned" }
    Start-Sleep -Milliseconds 600

    # --- 1. a web page actually loads ---------------------------------------
    Write-Host "`n[1] http:// pane must create a controller AND complete navigation"
    # Gate on the fixture being READY. Start-Job spins up a whole child
    # PowerShell, and pointing the pane at a port nothing is listening on yet
    # produces a nav.fail that looks exactly like a product bug.
    if (-not (Wait-Until { Port-Open } 25)) {
        throw "loopback server never came up on port $Port (job state: $($server.State); output: $((Receive-Job $server -Keep 2>&1 | Out-String).Trim()))"
    }
    # The readiness probe consumed one accept; don't count it as a page fetch.
    Remove-Item $hitFile -Force -EA SilentlyContinue
    $webUrl = "http://127.0.0.1:$Port/"
    $before = Log-Count 'UrlPaneHost.nav.ok'
    if (-not (Send-Verb 'pane.split-active' @{ dir = 'right'; url = $webUrl })) {
        throw "split-active verb rejected"
    }
    Check "controller created" (Wait-Until { (Log-Count 'UrlPaneHost.Init.begin') -ge 1 } 15)
    Check "navigation completed successfully (not a blank pane)" `
        (Wait-Until { (Log-Count 'UrlPaneHost.nav.ok') -gt $before } 25) `
        "- no nav.ok; the pane loaded nothing"
    Check "the loopback server was actually hit" ((Served-Count) -ge 1) `
        "- WebView2 never requested the page"
    [void](Send-Verb 'pane.close-active')
    Start-Sleep -Milliseconds 500

    # --- 2. THE REGRESSION: a local .html opens in a pane --------------------
    Write-Host "`n[2] local file:// .html pane (the 'agent wrote a report' flow)"
    $ctlBefore = Log-Count 'UrlPaneHost.Init.begin'
    $okBefore  = Log-Count 'UrlPaneHost.nav.ok'
    $rejBefore = Log-Count 'UrlPane.reject'
    if (-not (Send-Verb 'pane.split-active' @{ dir = 'right'; url = (To-FileUrl $goodHtml) })) {
        throw "split-active verb rejected"
    }
    Check "NOT refused by policy (it was, before the fix)" `
        (-not (Wait-Until { (Log-Count 'UrlPane.reject') -gt $rejBefore } 3)) `
        "- UrlPane.reject fired; the pane has no WebView2 behind it"
    Check "controller created" (Wait-Until { (Log-Count 'UrlPaneHost.Init.begin') -gt $ctlBefore } 15)
    Check "local page navigated successfully" `
        (Wait-Until { (Log-Count 'UrlPaneHost.nav.ok') -gt $okBefore } 20)
    [void](Send-Verb 'pane.close-active')
    Start-Sleep -Milliseconds 500

    # --- 3. a refused scheme is refused LOUDLY, not silently ----------------
    Write-Host "`n[3] a scheme a pane can't host must be refused, and reported"
    $ctlBefore = Log-Count 'UrlPaneHost.Init.begin'
    $rejBefore = Log-Count 'UrlPane.reject'
    if (-not (Send-Verb 'pane.split-active' @{ dir = 'down'; url = 'about:blank' })) {
        throw "split-active verb rejected"
    }
    Check "policy refused it" (Wait-Until { (Log-Count 'UrlPane.reject') -gt $rejBefore } 10)
    Start-Sleep -Milliseconds 800
    Check "no WebView2 was created for it" ((Log-Count 'UrlPaneHost.Init.begin') -eq $ctlBefore) `
        "- a controller appeared for a refused URL"
    # THE point of the case: a refused pane must not be an empty rectangle.
    $errText = Cdp-Eval "(document.querySelector('.urlpane-error')||{}).innerText||''"
    Check "the pane TELLS the user why (not a silent blank rectangle)" `
        ($errText -match 'display this address') "- DOM said: $errText"
    # Exactly one report, not one per layout message (the rect keeps arriving).
    Check "the rejection was reported once, not per layout message" `
        ((Log-Count 'UrlPane.reject') -eq ($rejBefore + 1)) `
        "- reject fired $((Log-Count 'UrlPane.reject') - $rejBefore) times"
    [void](Send-Verb 'pane.close-active')
    Start-Sleep -Milliseconds 500

    # --- 4. a file:// that isn't there fails LOUDLY --------------------------
    Write-Host "`n[4] a missing local file must log a navigation failure"
    $failBefore = Log-Count 'UrlPaneHost.nav.fail'
    if (-not (Send-Verb 'pane.split-active' @{ dir = 'right'; url = (To-FileUrl $missingHtml) })) {
        throw "split-active verb rejected"
    }
    Check "navigation failure recorded (not a silent empty document)" `
        (Wait-Until { (Log-Count 'UrlPaneHost.nav.fail') -gt $failBefore } 20)
    [void](Send-Verb 'pane.close-active')
    Start-Sleep -Milliseconds 500

    # --- 5. two panes side by side, one controller each ----------------------
    Write-Host "`n[5] two browser panes at once -> exactly one controller each"
    $ctlBefore = Log-Count 'UrlPaneHost.Init.begin'
    [void](Send-Verb 'pane.split-active' @{ dir = 'right'; url = (To-FileUrl $goodHtml) })
    Start-Sleep -Milliseconds 1200
    [void](Send-Verb 'pane.split-active' @{ dir = 'down';  url = (To-FileUrl $goodHtml) })
    Check "both controllers created" `
        (Wait-Until { (Log-Count 'UrlPaneHost.Init.begin') -ge ($ctlBefore + 2) } 20)
    Start-Sleep -Milliseconds 1500
    $made = (Log-Count 'UrlPaneHost.Init.begin') - $ctlBefore
    # Exactly two: a duplicate here means a create raced (the deferred-create
    # path used to start one per layout message while the env was warming up).
    Check "exactly 2 controllers, no duplicates" ($made -eq 2) "- created $made"

    # --- summary ------------------------------------------------------------
    Write-Host "`n--- host log (URL pane lines) ---" -ForegroundColor DarkGray
    Select-String -Path $LogPath -Pattern 'UrlPane' -EA SilentlyContinue |
        ForEach-Object { Write-Host "  $($_.Line)" -ForegroundColor DarkGray }

    $errs = @(Select-String -Path $LogPath -Pattern '] ERROR' -EA SilentlyContinue)
    if ($errs.Count -gt 0) {
        Write-Host "`n  WARN: ERROR lines in log:" -ForegroundColor Yellow
        $errs | Select-Object -Last 8 | ForEach-Object { Write-Host "    $($_.Line)" -ForegroundColor Yellow }
    }

    if ($fails.Count -gt 0) {
        Write-Host "`nRESULT: FAIL -- $($fails.Count) check(s) failed:" -ForegroundColor Red
        $fails | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        $exit = 1
    } else {
        Write-Host "`nRESULT: PASS -- browser panes create, navigate, and refuse loudly" -ForegroundColor Green
        $exit = 0
    }
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
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -EA SilentlyContinue
        Start-Sleep -Milliseconds 400
    }
    if ($server) {
        # Ask the loop to exit on its own, then reap. -Force so a job that
        # somehow didn't see the stop file still can't wedge the script.
        if ($stopFile) { New-Item -ItemType File -Path $stopFile -Force -EA SilentlyContinue | Out-Null }
        Wait-Job $server -Timeout 3 -EA SilentlyContinue | Out-Null
        Remove-Job $server -Force -EA SilentlyContinue
    }
    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    Remove-Item Env:PERCH_ENABLE_TEST_IPC, Env:PERCH_DATA_DIR,
                Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -EA SilentlyContinue
}
exit $exit
