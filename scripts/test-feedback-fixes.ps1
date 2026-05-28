# Verify the four fixes from the 2026-05-28 feedback batch:
#   #2  Terminal font size persists across restart
#   #6  AgentState.Waiting clears the moment the user types into the pane
#   #7  URL underline regex doesn't absorb a trailing ANSI escape sequence
#   #4  Attention-pulse wiring is in place (static checks: CSS keyframe +
#       data-state attribute set in built bundle)
#
# Keystroke-free, by the same rule the other harness scripts follow: the
# app is driven entirely via the control IPC pipe (CMUX_ENABLE_TEST_IPC=1)
# and we minimize the window so any churn stays off your screen.

[CmdletBinding()]
param(
    [string]$ExePath,
    [string]$ToolsDir,
    [string]$WebRoot,
    [switch]$KeepVisible
)

# $PSScriptRoot can be empty depending on how the script is invoked; fall
# back to the script's own location.
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot  = Resolve-Path (Join-Path $scriptDir '..')
if (-not $ExePath)  { $ExePath  = Join-Path $repoRoot 'src\CmuxWin\bin\Debug\net8.0-windows\win10-x64\CmuxWin.exe' }
if (-not $ToolsDir) { $ToolsDir = Join-Path $repoRoot 'src\CmuxWin\bin\Debug\net8.0-windows\win10-x64\tools' }
if (-not $WebRoot)  { $WebRoot  = Join-Path $repoRoot 'src\CmuxWin\wwwroot' }

$ErrorActionPreference = 'Continue'

if (-not (Test-Path $ExePath))  { throw "CmuxWin.exe not found at $ExePath. Build first." }
if (-not (Test-Path $ToolsDir)) { throw "tools/ missing at $ToolsDir." }
$CmuxExe = Join-Path $ToolsDir 'cmux.exe'
if (-not (Test-Path $CmuxExe)) { throw "cmux.exe missing at $CmuxExe." }

if (-not ('Cmux.WinShow' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace Cmux {
  public static class WinShow {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    public const int SW_MINIMIZE = 6;
  }
}
'@
}

$SettingsPath = Join-Path $env:APPDATA 'cmux-win\settings.json'
$SessionsPath = Join-Path $env:APPDATA 'cmux-win\sessions.json'
$ErrorsPath   = Join-Path $env:APPDATA 'cmux-win\errors.log'

function Stop-CmuxWin {
    Get-Process -Name CmuxWin -EA SilentlyContinue |
        Where-Object { $_.Path -like '*\bin\Debug\*' } |
        Stop-Process -Force -EA SilentlyContinue
    Start-Sleep -Milliseconds 400
}

function Reset-CmuxState {
    Remove-Item $SessionsPath -Force -EA SilentlyContinue
    Remove-Item $SettingsPath -Force -EA SilentlyContinue
    Remove-Item $ErrorsPath   -Force -EA SilentlyContinue
}

function Hide-CmuxWindow {
    param([System.Diagnostics.Process]$P)
    if ($KeepVisible) { return }
    if ($P.MainWindowHandle -ne [IntPtr]::Zero) {
        [Cmux.WinShow]::ShowWindow($P.MainWindowHandle, [Cmux.WinShow]::SW_MINIMIZE) | Out-Null
    }
}

function Start-CmuxForTest {
    $env:CMUX_ENABLE_TEST_IPC = '1'
    $p = Start-Process -PassThru -FilePath $ExePath
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $p.Refresh()
        if ($p.HasExited) { throw "CmuxWin exited early with $($p.ExitCode)" }
        if ($p.MainWindowHandle -ne 0) { return $p }
        Start-Sleep -Milliseconds 200
    }
    throw "CmuxWin main window never appeared"
}

function Wait-ForControlPipe {
    param([int]$TimeoutSec = 5)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path '\\.\pipe\cmux\control') { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

function Wait-ForPanePipe {
    param([string]$PaneId, [int]$TimeoutSec = 10)
    # CmuxIpcServer pipe name is `cmux\{Guid:N}` — no dashes — while
    # sessions.json stores the GUID with dashes. Normalize.
    $bare = $PaneId -replace '-', ''
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path "\\.\pipe\cmux\$bare") { return $true }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

function Invoke-CmuxTest {
    param([Parameter(Mandatory)][string]$Verb, [hashtable]$Flags = @{})
    $argList = @('test', $Verb)
    foreach ($k in $Flags.Keys) { $argList += "--$k"; $argList += [string]$Flags[$k] }
    $stderr = & $CmuxExe @argList 2>&1
    if ($LASTEXITCODE -ne 0) { throw ("cmux test {0} exited {1}: {2}" -f $Verb, $LASTEXITCODE, $stderr) }
}

function Send-PaneIpc {
    param([Parameter(Mandatory)][string]$PaneId, [Parameter(Mandatory)]$Payload)
    $bare = $PaneId -replace '-', ''
    $pipeName = "cmux\$bare"
    $client = New-Object System.IO.Pipes.NamedPipeClientStream(
        '.', $pipeName, [System.IO.Pipes.PipeDirection]::Out)
    try {
        $client.Connect(2000)
        $json = ($Payload | ConvertTo-Json -Compress) + "`n"
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
        $client.Write($bytes, 0, $bytes.Length)
        $client.Flush()
    } finally { $client.Dispose() }
}

function Get-LatestStateDump {
    if (-not (Test-Path $ErrorsPath)) { return $null }
    $line = (Get-Content $ErrorsPath -Tail 200 |
             Where-Object { $_ -match 'STATE_DUMP' } |
             Select-Object -Last 1)
    if (-not $line) { return $null }
    $json = $line -replace '^.*STATE_DUMP', ''
    try { return ($json | ConvertFrom-Json) } catch { return $null }
}

function Get-StateDump {
    param([int]$RetrySec = 3)
    $beforeCount = if (Test-Path $ErrorsPath) {
        (Select-String -Path $ErrorsPath -Pattern 'STATE_DUMP' -SimpleMatch -EA SilentlyContinue).Count
    } else { 0 }
    Invoke-CmuxTest 'state.dump'
    $deadline = (Get-Date).AddSeconds($RetrySec)
    while ((Get-Date) -lt $deadline) {
        $now = if (Test-Path $ErrorsPath) {
            (Select-String -Path $ErrorsPath -Pattern 'STATE_DUMP' -SimpleMatch -EA SilentlyContinue).Count
        } else { 0 }
        if ($now -gt $beforeCount) { return Get-LatestStateDump }
        Start-Sleep -Milliseconds 100
    }
    return Get-LatestStateDump
}

function Get-FirstPaneId {
    if (-not (Test-Path $SessionsPath)) { return $null }
    try {
        $store = Get-Content $SessionsPath -Raw | ConvertFrom-Json
        $active = $store.Sessions | Where-Object { $_.Id -eq $store.ActiveSessionId }
        if (-not $active) { $active = $store.Sessions[0] }
        $node = $active.Root
        while ($node.Children) { $node = $node.Children[0] }
        return $node.Id
    } catch { return $null }
}

# ==========================================================================
# Pre-flight: rebuild bundle so static checks reflect the latest sources
# ==========================================================================
Write-Host ""
Write-Host "==== Pre-flight: rebuild web bundle ===="
Push-Location (Join-Path $repoRoot 'src\web')
try { node esbuild.config.mjs | Out-Null } finally { Pop-Location }

# ==========================================================================
# Test A (#7): URL regex doesn't absorb ANSI escape bytes
# ==========================================================================
Write-Host ""
Write-Host "==== Test A (#7): URL regex bounded by control chars ===="
$paneTs = Get-Content (Join-Path $repoRoot 'src\web\src\pane.ts') -Raw
$reMatch = [regex]::Match(
    $paneTs,
    'const URL_RE\s*=\s*(/\(.*?/);',
    [System.Text.RegularExpressions.RegexOptions]::Singleline)
$regexOk = $false
if (-not $reMatch.Success) {
    Write-Host "  could not extract URL_RE from pane.ts -- pattern moved?" -ForegroundColor Red
} else {
    $regexLiteral = $reMatch.Groups[1].Value
    # Build the node script without here-string interpolation pitfalls.
    # We use single-quoted here-string for the body, then string-replace
    # __REGEX__ with the extracted regex literal.
    $nodeTemplate = @'
const re = __REGEX__;
const a = 'see http://localhost:5050/\x1b[0m and more text';
const ma = re.exec(a);
const b = 'see http://localhost:5050/ and more text';
const mb = re.exec(b);
const out = {
  a_match: ma == null ? null : ma[0],
  b_match: mb == null ? null : mb[0],
};
console.log(JSON.stringify(out));
'@
    $node = $nodeTemplate.Replace('__REGEX__', $regexLiteral)
    $tmpFile = New-TemporaryFile
    Set-Content -Path $tmpFile.FullName -Value $node -Encoding utf8
    $out = node $tmpFile.FullName 2>&1
    Remove-Item $tmpFile.FullName -EA SilentlyContinue
    try {
        $parsed = $out | ConvertFrom-Json
        $expected = 'http://localhost:5050/'
        $regexOk = ($parsed.a_match -eq $expected) -and ($parsed.b_match -eq $expected)
        Write-Host ("  with ESC trailing: '{0}'" -f $parsed.a_match)
        Write-Host ("  plain ASCII      : '{0}'" -f $parsed.b_match)
        if (-not $regexOk) {
            Write-Host ("  FAIL -- expected both matches to be '{0}'" -f $expected) -ForegroundColor Red
        }
    } catch {
        Write-Host ("  could not parse node output: {0}" -f $out) -ForegroundColor Red
    }
}

# ==========================================================================
# Test B (#4): Attention pulse wiring exists in built bundle
# ==========================================================================
Write-Host ""
Write-Host "==== Test B (#4): attention pulse wiring ===="
$appCssPath = Join-Path $WebRoot 'app.css'
$appJsPath  = Join-Path $WebRoot 'app.js'
$pulseInCss = if (Test-Path $appCssPath) {
    (Select-String -Path $appCssPath -Pattern 'pane-attention' -SimpleMatch).Count -gt 0
} else { $false }
$datasetInJs = if (Test-Path $appJsPath) {
    (Select-String -Path $appJsPath -Pattern 'dataset.state' -SimpleMatch).Count -gt 0
} else { $false }
$pulseOk = $pulseInCss -and $datasetInJs
Write-Host ("  @keyframes in app.css : {0}" -f $pulseInCss)
Write-Host ("  dataset.state in app.js: {0}" -f $datasetInJs)

# ==========================================================================
# Test E (#1): Inactive panes don't render a competing cursor
#
# Static check on the built bundle: cursorInactiveStyle must be set to
# 'none' on the Terminal constructor, and setActive must wire to both
# .focus() and .blur() so xterm's own focused state stays in sync with
# which pane is currently active. (A live "two cursors blinking"
# observation would require pixel inspection of an xterm canvas, which
# PrintWindow can't reach.)
# ==========================================================================
Write-Host ""
Write-Host "==== Test E (#1): cursor-inactive wiring ===="
$cursorNoneInJs = if (Test-Path $appJsPath) {
    # esbuild minifies but keeps property names. Match either the bare
    # property name or the option-object form.
    (Select-String -Path $appJsPath -Pattern 'cursorInactiveStyle' -SimpleMatch).Count -gt 0
} else { $false }
$blurInJs = if (Test-Path $appJsPath) {
    # The pane.ts setActive(false) path now calls term.blur(). Minified
    # this becomes `.blur()` on some short name; search for `.blur(`.
    (Select-String -Path $appJsPath -Pattern '.blur(' -SimpleMatch).Count -gt 0
} else { $false }
$cursorOk = $cursorNoneInJs -and $blurInJs
Write-Host ("  cursorInactiveStyle in bundle: {0}" -f $cursorNoneInJs)
Write-Host ("  .blur( in bundle             : {0}" -f $blurInJs)

# ==========================================================================
# Test F (#5): windowsPty wired so reflow tracks ConPTY line continuations
#
# Bundle-level check: xterm needs to be told it's driven by a ConPTY so
# its reflow logic on resize matches what ConPTY actually emits. Without
# it, a width change mid-output leaves wrapped fragments stranded in
# scrollback. We just verify the option survived bundling — behavioural
# verification requires watching a real resize on a streaming TUI.
# ==========================================================================
Write-Host ""
Write-Host "==== Test F (#5): windowsPty wired ===="
$windowsPtyInJs = if (Test-Path $appJsPath) {
    (Select-String -Path $appJsPath -Pattern 'windowsPty' -SimpleMatch).Count -gt 0
} else { $false }
$conptyInJs = if (Test-Path $appJsPath) {
    (Select-String -Path $appJsPath -Pattern 'conpty' -SimpleMatch).Count -gt 0
} else { $false }
$ptyOk = $windowsPtyInJs -and $conptyInJs
Write-Host ("  windowsPty in bundle: {0}" -f $windowsPtyInJs)
Write-Host ("  'conpty' in bundle  : {0}" -f $conptyInJs)

# ==========================================================================
# Test C (#2): Font size persistence (page <-> Settings.cs <-> disk)
# ==========================================================================
Write-Host ""
Write-Host "==== Test C (#2): font size persistence ===="
$fontOk = $false
$fontDetail = ''
$p = $null
try {
    Stop-CmuxWin; Reset-CmuxState
    $p = Start-CmuxForTest
    if (-not (Wait-ForControlPipe)) { throw "control pipe never appeared" }
    Hide-CmuxWindow -P $p

    Invoke-CmuxTest 'prefs.set' @{ fontSize = 19 }
    Start-Sleep -Milliseconds 400

    if (-not (Test-Path $SettingsPath)) { throw "settings.json never written" }
    $s1 = Get-Content $SettingsPath -Raw | ConvertFrom-Json
    if ($s1.FontSize -ne 19) { throw "settings.json FontSize=$($s1.FontSize), expected 19" }
    $fontDetail = "after prefs.set: FontSize=$($s1.FontSize)"

    Stop-Process -Id $p.Id -Force -EA SilentlyContinue
    Start-Sleep -Milliseconds 600

    $p = Start-CmuxForTest
    if (-not (Wait-ForControlPipe)) { throw "control pipe never appeared on relaunch" }
    Hide-CmuxWindow -P $p

    $dump = Get-StateDump
    if (-not $dump) { throw "state.dump never wrote to errors.log on relaunch" }
    $fontDetail += "; after relaunch dump: prefs.fontSize=$($dump.prefs.fontSize)"
    $fontOk = ($dump.prefs.fontSize -eq 19)
} catch {
    $fontDetail = "exception: $_"
    Write-Host ("  {0}" -f $fontDetail) -ForegroundColor Red
} finally {
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -EA SilentlyContinue }
    Start-Sleep -Milliseconds 300
}

# ==========================================================================
# Test D (#6): User input clears stale AgentState.Waiting
# ==========================================================================
Write-Host ""
Write-Host "==== Test D (#6): user input clears stale waiting ===="
$waitOk = $false
$waitDetail = ''
$p = $null
try {
    Stop-CmuxWin; Reset-CmuxState
    $p = Start-CmuxForTest
    if (-not (Wait-ForControlPipe)) { throw "control pipe never appeared" }
    Hide-CmuxWindow -P $p

    $paneId = $null
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and -not $paneId) {
        $paneId = Get-FirstPaneId
        if (-not $paneId) { Start-Sleep -Milliseconds 200 }
    }
    if (-not $paneId) { throw "no pane id found in sessions.json" }

    if (-not (Wait-ForPanePipe -PaneId $paneId -TimeoutSec 15)) {
        throw "pane IPC pipe for $paneId never opened -- WebView2 did not spawn the PTY"
    }

    Send-PaneIpc -PaneId $paneId -Payload @{ type = 'status'; state = 'waiting'; detail = $null }
    Start-Sleep -Milliseconds 300

    $dump1 = Get-StateDump
    if (-not $dump1) { throw "no state dump after setting waiting" }
    $pane1 = $dump1.sessions[0].panes | Where-Object { $_.id -eq $paneId } | Select-Object -First 1
    if (-not $pane1 -or $pane1.agentState -ne 'waiting') {
        throw "expected agentState=waiting after status push, got '$($pane1.agentState)'"
    }
    $waitDetail = "after status=waiting: '$($pane1.agentState)'"

    Invoke-CmuxTest 'pane.simulate-input' @{ text = 'y' }
    Start-Sleep -Milliseconds 300

    $dump2 = Get-StateDump
    if (-not $dump2) { throw "no state dump after simulated input" }
    $pane2 = $dump2.sessions[0].panes | Where-Object { $_.id -eq $paneId } | Select-Object -First 1
    $waitDetail += " -> after pane.in: '$($pane2.agentState)'"
    $waitOk = ($pane2.agentState -eq 'working')
} catch {
    $waitDetail = "exception: $_"
    Write-Host ("  {0}" -f $waitDetail) -ForegroundColor Red
} finally {
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -EA SilentlyContinue }
    Start-Sleep -Milliseconds 300
}

# ==========================================================================
# Test G (#3): Settings dialog save path persists shell + cwd + font
#
# 1. Static: the settings dialog wiring survived bundling (settings-card
#    CSS + the settings.request/save message types in JS).
# 2. Functional: drive `settings.save` via the control verb, then read
#    settings.json off disk and confirm all three fields landed.
# ==========================================================================
Write-Host ""
Write-Host "==== Test G (#3): settings dialog ===="
$settingsCssOk = if (Test-Path $appCssPath) {
    (Select-String -Path $appCssPath -Pattern 'settings-card' -SimpleMatch).Count -gt 0
} else { $false }
$settingsJsOk = if (Test-Path $appJsPath) {
    (Select-String -Path $appJsPath -Pattern 'settings.request' -SimpleMatch).Count -gt 0
} else { $false }
Write-Host ("  settings-card in app.css   : {0}" -f $settingsCssOk)
Write-Host ("  settings.request in app.js : {0}" -f $settingsJsOk)

$settingsSaveOk = $false
$settingsDetail = ''
$p = $null
try {
    Stop-CmuxWin; Reset-CmuxState
    $p = Start-CmuxForTest
    if (-not (Wait-ForControlPipe)) { throw "control pipe never appeared" }
    Hide-CmuxWindow -P $p

    $testCwd = $env:TEMP
    Invoke-CmuxTest 'settings.save' @{ defaultShell = 'cmd.exe'; defaultCwd = $testCwd; fontSize = 17 }
    Start-Sleep -Milliseconds 400

    if (-not (Test-Path $SettingsPath)) { throw "settings.json never written" }
    $s = Get-Content $SettingsPath -Raw | ConvertFrom-Json
    $shellOk = ($s.DefaultShell -eq 'cmd.exe')
    $cwdOk   = ($s.DefaultCwd -eq $testCwd)
    $fsOk    = ($s.FontSize -eq 17)
    $settingsSaveOk = $shellOk -and $cwdOk -and $fsOk
    $settingsDetail = "shell='$($s.DefaultShell)' cwd='$($s.DefaultCwd)' font=$($s.FontSize)"
} catch {
    $settingsDetail = "exception: $_"
    Write-Host ("  {0}" -f $settingsDetail) -ForegroundColor Red
} finally {
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -EA SilentlyContinue }
    Start-Sleep -Milliseconds 300
}
$settingsOk = $settingsCssOk -and $settingsJsOk -and $settingsSaveOk

# ==========================================================================
# Summary
# ==========================================================================
Write-Host ""
Write-Host "==== Summary ===="
Write-Host ("  A  #7 URL regex             : {0}" -f $(if($regexOk){'PASS'}else{'FAIL'}))
Write-Host ("  B  #4 pulse wiring          : {0}  css={1} js={2}" -f $(if($pulseOk){'PASS'}else{'FAIL'}), $pulseInCss, $datasetInJs)
Write-Host ("  C  #2 font persistence      : {0}  {1}" -f $(if($fontOk){'PASS'}else{'FAIL'}), $fontDetail)
Write-Host ("  D  #6 stale waiting clears  : {0}  {1}" -f $(if($waitOk){'PASS'}else{'FAIL'}), $waitDetail)
Write-Host ("  E  #1 cursor-inactive wiring: {0}  inactive={1} blur={2}" -f $(if($cursorOk){'PASS'}else{'FAIL'}), $cursorNoneInJs, $blurInJs)
Write-Host ("  F  #5 windowsPty wired      : {0}  windowsPty={1} conpty={2}" -f $(if($ptyOk){'PASS'}else{'FAIL'}), $windowsPtyInJs, $conptyInJs)
Write-Host ("  G  #3 settings dialog       : {0}  {1}" -f $(if($settingsOk){'PASS'}else{'FAIL'}), $settingsDetail)

if ($regexOk -and $pulseOk -and $fontOk -and $waitOk -and $cursorOk -and $ptyOk -and $settingsOk) {
    Write-Host ""
    Write-Host "ALL PASS" -ForegroundColor Green
    exit 0
}
Write-Host ""
Write-Host "FAIL" -ForegroundColor Red
exit 1
