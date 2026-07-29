<#
End-to-end: the board pane (the context staging surface).

Boards are a THIRD leaf kind beside terminals and browser panes, and the things
most likely to break about them are invisible from a screenshot:

  - A board must not spawn a shell. Nothing enforces that with a guard; the
    mechanism is that a board never sends `pane.resize`, exactly how URL panes
    avoid it. So the assertion is on the ABSENCE of a Pane.spawn, which only the
    host log can tell you.
  - A board that can't be read must SAY why. An empty dotted grid looks
    identical to a board nobody has filled in yet - the same trap the URL pane
    fell into. That one needs the real DOM, via scripts/cdp-eval.mjs.
  - board.md must survive a restart, because the pane is a view onto a file.
  - The board folder must be invisible to git. GitProc enumerates untracked
    files with `--exclude-standard`, so `.perch/.gitignore` containing `*` is
    what keeps fetched refs and screenshots out of the agent's LOC chip. This is
    the regression UntrackedBaseline exists to prevent, and it gets its own
    check here.

ISOLATION / SAFETY (same rules as test-urlpane-open.ps1):
  - Unique PERCH_DATA_DIR keyed to this shell's PID.
  - Aborts if a test-IPC control pipe is already up.
  - Kills ONLY the PID this script launches.
  - Window parked off-screen at a real size so layout/render still run.
  - The scratch repo is a throwaway `git init` under TEMP, never your checkout.

NOTE: the running app serves wwwroot from the BIN OUTPUT, so a page-side change
needs `dotnet build src/Perch/Perch.csproj` (not just `npm run build`). Build
first.
#>
# NOTE: no [CmdletBinding()] here on purpose. Under Windows PowerShell 5.1 it
# makes the script advanced-function-bound and $PSScriptRoot then evaluates to
# EMPTY inside param() defaults, so the paths below silently become "\..\src\..."
# and the script dies with "Perch.exe not found (build first)" on a repo that
# built fine.
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools",
    [int]$CdpPort = 9335,
    # Loopback page for the URL-fetch case. Hermetic on purpose: pointing this
    # at a real site would fail on any machine or CI runner without DNS egress,
    # which is a false alarm rather than a product bug.
    [int]$Port = 47822
)
$ErrorActionPreference = 'Stop'

$PerchExe = Join-Path $ToolsDir 'perch.exe'
$DataDir  = Join-Path $env:TEMP ("perch-board-{0}" -f $PID)
$LogPath  = Join-Path $DataDir 'perch\errors.log'
$RepoDir  = Join-Path $DataDir 'repo'

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
function Cdp-Eval { param([string]$Expr)
    $out = & node (Join-Path $PSScriptRoot 'cdp-eval.mjs') $Expr 8000 $CdpPort 2>&1
    return ($out | Out-String).Trim()
}
# The Windows clipboard is a single global lock any process can hold, so
# SetText/SetImage genuinely fail with CLIPBRD_E_CANT_OPEN when something else
# (Explorer, a browser, another test) has it open at that instant. That is
# ambient flakiness, not a fact about Perch - retry rather than report a
# product failure.
$script:LastClipError = ''
function Set-Clip { param([scriptblock]$Set)
    for ($i = 0; $i -lt 20; $i++) {
        try { & $Set; $script:LastClipError = ''; return $true }
        catch { $script:LastClipError = $_.Exception.Message; Start-Sleep -Milliseconds 250 }
    }
    return $false
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
    New-Item -ItemType Directory -Force -Path $RepoDir | Out-Null

    # A throwaway git repo for the board to live in. Real git, because the
    # gitignore assertion below runs a real `git ls-files`.
    Push-Location $RepoDir
    try {
        & git init --quiet 2>$null
        Set-Content -Path (Join-Path $RepoDir 'README.md') -Value 'scratch' -Encoding utf8
    } finally { Pop-Location }

    $env:PERCH_ENABLE_TEST_IPC = '1'
    $env:PERCH_DATA_DIR        = $DataDir
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
    [Perch.WinPos]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, -3400, -3400, 1400, 900,
        ([Perch.WinPos]::NOZORDER -bor [Perch.WinPos]::NOACTIVATE)) | Out-Null
    Write-Host "  pid=$($proc.Id)"
    if (-not (Wait-Until { (Log-Count 'Pane.spawn') -ge 1 } 25)) { throw "initial pane never spawned" }
    Start-Sleep -Milliseconds 600

    # Point the pane at the scratch repo so the board lands there. cd through
    # the PTY is the only route the host learns a cwd (OSC 7).
    [void](Send-Verb 'pane.simulate-input' @{ text = "cd '$RepoDir'`r" })
    Start-Sleep -Milliseconds 1200

    # --- 1. a board pane opens, and spawns NO shell -------------------------
    Write-Host "`n[1] board.new opens a board pane without spawning a shell"
    $spawnsBefore = Log-Count 'Pane.spawn'
    if (-not (Send-Verb 'board.new-active')) { throw "board.new-active verb rejected" }
    Check "board folder created" (Wait-Until { (Log-Count 'Board.create') -ge 1 } 15)
    Start-Sleep -Milliseconds 1500
    # THE assertion: a board leaf must never get a PTY. Nothing guards this - a
    # board simply never sends pane.resize - so a regression is silent.
    Check "no shell spawned for the board pane" ((Log-Count 'Pane.spawn') -eq $spawnsBefore) `
        "- spawn count went $spawnsBefore -> $(Log-Count 'Pane.spawn')"

    $boardDir = Join-Path $RepoDir '.perch\boards'
    Check "board folder is under the repo's .perch/boards" (Test-Path $boardDir)
    $mdFiles = @(Get-ChildItem -Path $boardDir -Recurse -Filter 'board.md' -EA SilentlyContinue)
    Check "board.md was written" ($mdFiles.Count -eq 1) "- found $($mdFiles.Count)"

    # --- 2. the pane renders, and says it is empty --------------------------
    Write-Host "`n[2] the pane renders the empty state rather than a blank grid"
    Check "a board pane is in the DOM" `
        ((Cdp-Eval "document.querySelectorAll('.pane--board').length") -eq '1')
    $empty = Cdp-Eval "(document.querySelector('.board-message__title')||{}).innerText||''"
    Check "it says the board is empty" ($empty -match 'Nothing on this board') "- DOM said: $empty"

    # --- 3. git must not see the board --------------------------------------
    Write-Host "`n[3] the board is invisible to git (the LOC-chip regression)"
    Check ".perch/.gitignore was written" (Test-Path (Join-Path $RepoDir '.perch\.gitignore'))
    Push-Location $RepoDir
    try {
        # Exactly the enumeration GitProc.UntrackedStatsAsync runs. If the board
        # shows up here, every fetched ref folds into the agent's linesAdded.
        $untracked = @(& git ls-files --others --exclude-standard 2>$null)
    } finally { Pop-Location }
    $leaked = @($untracked | Where-Object { $_ -like '.perch/*' })
    Check "no board file is untracked-visible to git" ($leaked.Count -eq 0) `
        "- git would count: $($leaked -join ', ')"

    # --- 3b. paste an image ---------------------------------------------------
    Write-Host "`n[3b] a pasted image lands in assets/ and renders on the card"
    # A real bitmap on the real Windows clipboard: the host reads it with
    # Clipboard.GetImage on the UI thread, so nothing here can be faked by
    # sending bytes over the bridge.
    Add-Type -AssemblyName PresentationCore, WindowsBase
    $bmp = New-Object System.Windows.Media.Imaging.WriteableBitmap 64, 48, 96, 96,
        ([System.Windows.Media.PixelFormats]::Bgra32), $null
    $stride = 64 * 4
    $pixels = New-Object byte[] ($stride * 48)
    for ($i = 0; $i -lt $pixels.Length; $i += 4) {
        $pixels[$i] = 200; $pixels[$i+1] = 120; $pixels[$i+2] = 60; $pixels[$i+3] = 255
    }
    $bmp.WritePixels((New-Object System.Windows.Int32Rect 0, 0, 64, 48), $pixels, $stride, 0)
    if (-not (Set-Clip { [System.Windows.Clipboard]::SetImage($bmp) })) {
        throw "could not put an image on the clipboard: $script:LastClipError"
    }
    Start-Sleep -Milliseconds 300

    $paneId = (Cdp-Eval "document.querySelector('.pane--board').dataset.paneId").Trim('"')
    [void](Cdp-Eval "(()=>{window.chrome.webview.postMessage(JSON.stringify({type:'board.paste',paneId:'$paneId',x:16,y:16}));return 1})()")
    Check "the image was written to assets/" `
        (Wait-Until { (Log-Count 'Board.paste.image') -ge 1 } 10)
    Start-Sleep -Milliseconds 1200
    $assets = @(Get-ChildItem -Path $boardDir -Recurse -Filter '*.png' -EA SilentlyContinue)
    Check "exactly one asset file exists" ($assets.Count -eq 1) "- found $($assets.Count)"
    # THE point of the feature: the card shows the picture, not a filename.
    Check "the card renders an <img>, not just a path" `
        ((Cdp-Eval "document.querySelectorAll('.board-node--image .board-node__img').length") -eq '1')
    Check "the img actually decoded" `
        ((Cdp-Eval "(()=>{const i=document.querySelector('.board-node__img');return i&&i.complete&&i.naturalWidth>0?1:0})()") -eq '1')

    # --- 3c. resize persists --------------------------------------------------
    Write-Host "`n[3c] a resize is written to board.md"
    [void](Cdp-Eval "(()=>{const n=document.querySelector('.board-node');window.chrome.webview.postMessage(JSON.stringify({type:'board.resize',paneId:'$paneId',nodeId:n.dataset.nodeId,w:320,h:260,final:true}));return 1})()")
    Start-Sleep -Milliseconds 900
    $mdText = Get-Content ($mdFiles[0].FullName) -Raw
    Check "the new size is in the layout block" ($mdText -match '"w":320') "- board.md has no w:320"
    Check "the card is actually that wide in the DOM" `
        ((Cdp-Eval "Math.round(document.querySelector('.board-node').getBoundingClientRect().width)") -eq '320')

    # --- 3e. paste a URL: fetch, extract, cache ------------------------------
    Write-Host "`n[3e] a pasted link is fetched and cached as readable markdown"
    $stopFile = Join-Path $DataDir 'server.stop'
    $server = Start-Job -ArgumentList $Port, $stopFile -ScriptBlock {
        param([int]$Port, [string]$StopFile)
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
        $listener.Start()
        $body = '<html><head><title>Session cookies</title></head><body>' +
                '<nav><a href="/other">Nav link</a></nav>' +
                '<script>window.tracker=1</script>' +
                '<main><h1>Session cookies</h1>' +
                '<p>Keep the cookie <strong>HttpOnly</strong>. ' +
                'That is the whole point of this page and it needs to be long enough ' +
                'that the extractor does not think it is a JavaScript shell, so here ' +
                'is some more prose about cookies and their many attributes.</p>' +
                '<ul><li>Script cannot read it</li><li>Survives XSS</li></ul>' +
                '<p>See <a href="/spec/6.2">section 6.2</a> for the details.</p>' +
                '</main></body></html>'
        $head = "HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`n" +
                "Content-Length: $($body.Length)`r`nConnection: close`r`n`r`n"
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($head + $body)
        try {
            while (-not (Test-Path $StopFile)) {
                if (-not $listener.Pending()) { Start-Sleep -Milliseconds 50; continue }
                $client = $listener.AcceptTcpClient()
                try {
                    $stream = $client.GetStream()
                    $buf = New-Object byte[] 4096
                    [void]$stream.Read($buf, 0, $buf.Length)
                    $stream.Write($bytes, 0, $bytes.Length)
                    $stream.Flush()
                } finally { $client.Close() }
            }
        } finally { $listener.Stop() }
    }
    # Gate on the fixture being up, else a nav.fail-shaped failure looks like a
    # product bug.
    if (-not (Wait-Until {
        $c = New-Object System.Net.Sockets.TcpClient
        try { $c.Connect('127.0.0.1', $Port); return $c.Connected } catch { return $false } finally { $c.Close() }
    } 25)) { throw "loopback server never came up on port $Port" }

    # board.add directly rather than via the clipboard. The clipboard READ path
    # is already covered by the image case above; routing this one through it
    # too would only add flakiness, because every running Perch has a
    # ClipboardWatcher that reads on each change and they contend for the
    # single global clipboard lock. board.add with kind "auto" is the exact
    # same code path a text paste takes once the host has the string.
    $addUrl = "(()=>{window.chrome.webview.postMessage(JSON.stringify({type:'board.add',paneId:'$paneId',kind:'auto',text:'http://127.0.0.1:$Port/cookies',x:16,y:240}));return 1})()"
    [void](Cdp-Eval $addUrl)
    Check "the page was fetched and extracted" (Wait-Until { (Log-Count 'Board.fetch.ok') -ge 1 } 25)
    Start-Sleep -Milliseconds 800

    $refs = @(Get-ChildItem -Path $boardDir -Recurse -Filter '*.md' -EA SilentlyContinue |
              Where-Object { $_.Directory.Name -eq 'refs' })
    Check "a cached copy landed in refs/" ($refs.Count -eq 1) "- found $($refs.Count)"
    if ($refs.Count -eq 1) {
        $ref = Get-Content $refs[0].FullName -Raw
        Check "it records where it came from and when" `
            (($ref -match '127\.0\.0\.1') -and ($ref -match '\d{4}-\d{2}-\d{2}'))
        Check "the prose survived" ($ref -match 'Keep the cookie \*\*HttpOnly\*\*')
        Check "list items became markdown bullets" ($ref -match '- Script cannot read it')
        Check "a relative link was made absolute" ($ref -match '\[section 6\.2\]\(http://127\.0\.0\.1')
        # The two things that must NEVER survive into a file an agent reads.
        Check "the script body did not survive" (-not ($ref -match 'window\.tracker'))
        Check "the nav was dropped" (-not ($ref -match 'Nav link'))
    }
    # And the card should now point at the cached file rather than the bare URL.
    Check "the card shows the cached ref" `
        ((Cdp-Eval "(()=>{const e=[...document.querySelectorAll('.board-node--url .board-node__ref')];return e.some(x=>x.textContent.includes('refs/'))?1:0})()") -eq '1')

    # --- 3d. zoom ------------------------------------------------------------
    Write-Host "`n[3d] zoom scales the canvas and Ctrl+0 resets it"
    # Ctrl+= reaches the active pane through the SAME path the terminal font
    # shortcut uses, so this also proves a board is reachable by it.
    $zoomIn = @"
(()=>{const s=document.querySelector('.pane--board');
s.dispatchEvent(new MouseEvent('mousedown',{bubbles:true}));
for(let i=0;i<3;i++)window.dispatchEvent(new KeyboardEvent('keydown',{code:'Equal',ctrlKey:true,bubbles:true}));
const c=document.querySelector('.board__canvas');
return c.style.transform;})()
"@
    $t = Cdp-Eval $zoomIn
    Check "Ctrl+= scales the canvas up" ($t -match 'scale\(1\.[1-9]') "- transform was: $t"
    Check "a zoom readout appears" `
        ((Cdp-Eval "document.querySelectorAll('.board__zoom--on').length") -eq '1')
    # A board must NOT write the terminal font pref when zoomed - changeFontSize
    # returns 0 precisely so main.ts skips the prefs.set.
    Check "zooming did not touch the font preference" `
        ((Log-Count 'prefs.set') -eq 0) "- a prefs.set was sent"

    $t0 = Cdp-Eval "(()=>{window.dispatchEvent(new KeyboardEvent('keydown',{code:'Digit0',ctrlKey:true,bubbles:true}));return document.querySelector('.board__canvas').style.transform;})()"
    Check "Ctrl+0 resets to 100% at the origin" `
        ($t0 -match 'translate\(0px, 0px\) scale\(1\)') "- transform was: $t0"

    # --- 4. a missing board folder shows a reason ---------------------------
    Write-Host "`n[4] a board whose folder vanished says so"
    Remove-Item (Join-Path $RepoDir '.perch\boards') -Recurse -Force -EA SilentlyContinue
    # Re-request by toggling away and back would need a second session; drive
    # the request directly by reloading the page's board state instead.
    [void](Cdp-Eval "(()=>{const p=document.querySelector('.pane--board');if(!p)return 0;window.chrome.webview.postMessage(JSON.stringify({type:'board.request',paneId:p.dataset.paneId}));return 1;})()")
    Start-Sleep -Milliseconds 900
    $err = Cdp-Eval "(document.querySelector('.board-message__title')||{}).innerText||''"
    Check "the pane reports the missing folder" ($err -match 'can') "- DOM said: $err"

    # --- summary ------------------------------------------------------------
    Write-Host "`n--- host log (board lines) ---" -ForegroundColor DarkGray
    Select-String -Path $LogPath -Pattern 'Board' -EA SilentlyContinue |
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
        Write-Host "`nRESULT: PASS -- boards open, stay out of git, and fail loudly" -ForegroundColor Green
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
        # Ask the loop to exit on its own; a job parked in a blocking accept
        # doesn't answer Stop-Job and would wedge this script.
        if ($stopFile) { New-Item -ItemType File -Path $stopFile -Force -EA SilentlyContinue | Out-Null }
        Wait-Job $server -Timeout 3 -EA SilentlyContinue | Out-Null
        Remove-Job $server -Force -EA SilentlyContinue
    }
    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    Remove-Item Env:PERCH_ENABLE_TEST_IPC, Env:PERCH_DATA_DIR,
                Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -EA SilentlyContinue
}
exit $exit
