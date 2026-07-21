# Self-test for the "Setting up…" boot cover + /color choreography.
#
# Launches an ISOLATED Perch (PERCH_DATA_DIR → a scratch dir, so the user's real
# session store is never touched), drives a project tab over the test control
# pipe, and asserts the ordering AND the timing the feature exists to guarantee:
#
#     session-start hook  →  /color typed (only after cc SETTLES)  →  uncovered
#
# Two bugs this pins down:
#  1. The cover used to drop ON the session-start hook, which fires before cc has
#     painted; an earlier fix typed /color on a timeout that could expire BEFORE
#     the hook — i.e. into a PTY with no reader attached. (ordering asserts)
#  2. The 150ms quiet gate fired inside cc's FIRST inter-paint boot lull (~250ms
#     in, 3 chunks), typing /color into a not-yet-ready cc and uncovering while
#     it was still painting. Ordering alone did NOT catch this — the phase-1
#     SETTLE gate must hold /color until cc genuinely goes quiet, so /color must
#     land well after the hook, not a few hundred ms later. (timing assert below)
#
# PERCH_SETUP_DIAG=1 makes the host log each boot chunk + the settle gate firing.
#
# Usage:  pwsh -File scripts/verify-setup-cover.ps1
# Exit 0 = pass. Leaves the app running only long enough to observe one launch.

$ErrorActionPreference = 'Stop'

$sandbox = Join-Path $env:TEMP "perch-selftest-$(Get-Random)"
$exe     = Join-Path $PSScriptRoot "..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe"
$log     = Join-Path $sandbox "perch\errors.log"
$repo    = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

if (-not (Test-Path $exe)) { throw "Build first: dotnet build src/Perch/Perch.csproj  (missing $exe)" }
New-Item -ItemType Directory -Force (Join-Path $sandbox "perch") | Out-Null

# NO_COLOR is set in some tool shells and is inherited all the way down into cc,
# which then emits zero ANSI and renders monochrome. Clear it so the instance
# behaves like one the user would launch themselves.
Remove-Item env:NO_COLOR -ErrorAction SilentlyContinue
$env:PERCH_DATA_DIR       = $sandbox
$env:PERCH_ENABLE_TEST_IPC = "1"
$env:PERCH_SETUP_DIAG      = "1"   # richer boot-timing log for the assertions below

$proc = Start-Process -FilePath $exe -PassThru
Write-Host "launched pid=$($proc.Id) data=$sandbox"

function Wait-ForLog([string]$pattern, [int]$timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $log) {
            $hit = Get-Content $log -ErrorAction SilentlyContinue | Select-String $pattern
            if ($hit) { return $hit }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

try {
    if (-not (Wait-ForLog "ControlIpc.start" 30)) { throw "control IPC never came up" }

    # Register this repo as a project, then open an agent tab in it.
    $pipe = new-object System.IO.Pipes.NamedPipeClientStream('.', 'perch\control', [System.IO.Pipes.PipeDirection]::Out)
    $pipe.Connect(5000)
    $w = new-object System.IO.StreamWriter($pipe); $w.AutoFlush = $true
    # NOTE: the control pipe keys on "verb" (ControlIpcServer.Dispatch), not the
    # "type" the page-side protocol uses. A message with "type" is dropped silently.
    $w.WriteLine((@{ verb = 'project.add'; path = $repo } | ConvertTo-Json -Compress))
    Start-Sleep -Seconds 2

    # Projects live in their own store (Project.cs → projects.json), not sessions.json.
    $projFile = Join-Path $sandbox "perch\projects.json"
    if (-not (Test-Path $projFile)) { throw "project.add wrote no projects.json" }
    # ProjectStore serializes as { "Projects": [ ... ] }.
    $projects = Get-Content $projFile -Raw | ConvertFrom-Json
    $projId   = @($projects.Projects)[0].Id
    if (-not $projId) { throw "project.add did not register a project" }
    Write-Host "project id=$projId"

    $w.WriteLine((@{ verb = 'project.tab.new'; projectId = $projId; name = 'selftest'; agent = 'claude' } | ConvertTo-Json -Compress))
    $w.Dispose(); $pipe.Dispose()

    # cc cold start has measured up to 7.3s; allow generous headroom.
    if (-not (Wait-ForLog "Setup: .* settled; uncovering" 60)) {
        Get-Content $log -Tail 30
        throw "cover never settled — check for a 'gave up' or capped line above"
    }

    $seq = Get-Content $log | Select-String "type=session|CcColor:|Setup:" | ForEach-Object { $_.Line }
    Write-Host "`n--- observed sequence ---"; $seq | ForEach-Object { Write-Host "  $_" }

    $iSession = ($seq | Select-String "type=session" | Select-Object -First 1).LineNumber
    $iColor   = ($seq | Select-String "CcColor:"     | Select-Object -First 1).LineNumber
    $iUncover = ($seq | Select-String "uncovering"   | Select-Object -First 1).LineNumber

    if (-not $iSession) { throw "FAIL: cc session-start hook never arrived" }
    if (-not $iColor)   { throw "FAIL: /color was never typed" }
    if ($iColor -lt $iSession) { throw "FAIL: /color typed BEFORE the session hook — PTY had no reader attached" }
    if ($iUncover -lt $iColor) { throw "FAIL: uncovered BEFORE /color was applied" }
    if ($seq | Select-String "capped|gave up") { throw "FAIL: hit a cap instead of settling naturally" }

    # TIMING assert (bug #2): /color must land only AFTER cc's boot settles, not
    # inside the first ~250ms inter-paint lull. The settle gate structurally can't
    # fire until cc has been quiet for SetupSettleMs, so the hook→/color delta is
    # necessarily > ~1s on a healthy boot; the bug produced ~250ms. Parse the
    # "[HH:mm:ss.fff]" stamps and require a clear separation.
    function Stamp([string]$line) {
        if ($line -match '^\[(\d\d:\d\d:\d\d\.\d\d\d)\]') {
            return [datetime]::ParseExact($matches[1], 'HH:mm:ss.fff', $null)
        }
        return $null
    }
    $tSession = Stamp (($seq | Select-String "type=session" | Select-Object -First 1).Line)
    $tColor   = Stamp (($seq | Select-String "CcColor:"     | Select-Object -First 1).Line)
    if ($tSession -and $tColor) {
        $deltaMs = ($tColor - $tSession).TotalMilliseconds
        Write-Host ("hook -> /color delta: {0:N0}ms" -f $deltaMs)
        if ($deltaMs -lt 1000) {
            throw "FAIL: /color typed only ${deltaMs}ms after the hook — the settle gate did NOT hold; it fired inside a boot lull (the pre-fix bug)."
        }
    } else {
        Write-Host "WARN: could not parse timestamps for the timing assert" -ForegroundColor Yellow
    }

    Write-Host "`nPASS: hook -> settle -> /color -> uncover, no cap" -ForegroundColor Green
    exit 0
}
finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -Recurse -Force $sandbox -ErrorAction SilentlyContinue
}
