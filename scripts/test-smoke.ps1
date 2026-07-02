<#
.SYNOPSIS
End-to-end smoke of the host dispatch pipeline against a LIVE isolated
instance: launch → lazy PTY spawn → flat splits → CSV weights → both moveDir
spellings → string-typed prefs/settings → session close/restore/purge →
malformed-payload probe. Asserts on errors.log + persisted state, then kills
ONLY the PID it started. No screenshots, no focus games.

Isolated under C:\tmp\perch-smoke (PERCH_DATA_DIR) — never touches prod state.
Exit code 0 = PASS. Run after any change to the message router, PaneManager,
PaneTree or StateProjection.

.EXAMPLE
  ./scripts/test-smoke.ps1
#>
[CmdletBinding()]
param(
    [string]$OutDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64"
)

$ErrorActionPreference = 'Stop'
$ExePath = Join-Path $OutDir 'Perch.exe'
$DataDir = 'C:\tmp\perch-smoke'
$LogPath = Join-Path $DataDir 'perch\errors.log'
$StorePath = Join-Path $DataDir 'perch\sessions.json'

if (-not (Test-Path $ExePath)) {
    throw "Perch.exe not found at $ExePath. Build first: dotnet build src/Perch/Perch.csproj -c Debug"
}

Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $DataDir 'perch') | Out-Null

$env:PERCH_DATA_DIR = $DataDir
$env:PERCH_ENABLE_TEST_IPC = '1'

$p = Start-Process -PassThru -FilePath $ExePath
Write-Host "launched pid=$($p.Id)" -ForegroundColor Green

function Send-Verbs {
    param([string[]]$Lines)
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'perch\control', [System.IO.Pipes.PipeDirection]::Out)
    $pipe.Connect(3000)
    $sw = [System.IO.StreamWriter]::new($pipe)
    foreach ($l in $Lines) { $sw.WriteLine($l); Write-Host "  >> $l" -ForegroundColor DarkGray }
    $sw.Flush(); $sw.Dispose()
}

function Get-Store { Get-Content $StorePath -Raw | ConvertFrom-Json }

try {
    Start-Sleep -Seconds 7
    if ($p.HasExited) { throw "FAIL: Perch exited early ($($p.ExitCode))" }

    # -- Splits: two right-splits must yield a FLAT 3-child root, not nesting.
    Send-Verbs @(
        '{"verb":"pane.split-active","dir":"right"}',
        '{"verb":"pane.split-active","dir":"right"}'
    )
    Start-Sleep -Seconds 2
    $root = (Get-Store).Sessions[0].Root
    if ($root.Children.Count -ne 3) { throw "FAIL: expected flat 3-child split, got $($root.Children.Count)" }
    $firstSessionId = (Get-Store).Sessions[0].Id
    $firstPane = $root.Children[0].Id
    Write-Host "flat 3-way split OK" -ForegroundColor Green

    # -- Unified router: CSV weights shim, both moveDir spellings, lenient
    #    string flags (perch-test wire form), second session.
    Send-Verbs @(
        ('{"verb":"pane.resize-split","splitId":"' + $root.Id + '","weights":"2,1,1"}'),
        ('{"verb":"pane.move-dir","paneId":"' + $firstPane + '","dir":"right"}'),
        ('{"verb":"pane.moveDir","paneId":"' + $firstPane + '","dir":"left"}'),
        '{"verb":"prefs.set","fontSize":"15"}',
        '{"verb":"settings.save","resumeAgentsOnLaunch":"false"}',
        '{"verb":"session.new"}'
    )
    Start-Sleep -Seconds 2
    $store = Get-Store
    if ($store.Sessions.Count -ne 2) { throw "FAIL: expected 2 sessions, got $($store.Sessions.Count)" }
    $weights = ($store.Sessions | Where-Object { $_.Id -eq $firstSessionId }).Root.Children | ForEach-Object { $_.Weight }
    if (($weights -join ',') -ne '2,1,1') { throw "FAIL: weights not persisted, got $($weights -join ',')" }
    Write-Host "weights + moveDir + string flags OK" -ForegroundColor Green

    # -- Session lifecycle: close → Recently closed; restore → live; purge → gone.
    Send-Verbs @(('{"verb":"session.close","id":"' + $firstSessionId + '"}'))
    Start-Sleep -Seconds 1
    $store = Get-Store
    if ($store.Sessions.Count -ne 1) { throw "FAIL: close didn't remove the session" }
    if ($store.ClosedSessions[0].Id -ne $firstSessionId) { throw "FAIL: closed session not archived" }

    Send-Verbs @(('{"verb":"session.restore","id":"' + $firstSessionId + '"}'))
    Start-Sleep -Seconds 2
    $store = Get-Store
    if (($store.Sessions | Where-Object { $_.Id -eq $firstSessionId }).Count -ne 1) { throw "FAIL: restore didn't bring it back" }
    if ($store.ClosedSessions.Count -ne 0) { throw "FAIL: restore left it in Recently closed" }

    Send-Verbs @(
        ('{"verb":"session.close","id":"' + $firstSessionId + '"}'),
        ('{"verb":"session.purge","id":"' + $firstSessionId + '"}')
    )
    Start-Sleep -Seconds 1
    $store = Get-Store
    if ($store.ClosedSessions.Count -ne 0) { throw "FAIL: purge didn't drop it" }
    Write-Host "session close/restore/purge OK" -ForegroundColor Green

    # -- Loud boundary: malformed payloads must log ERROR WITH the payload and
    #    NOT kill the app or the pipe loop.
    Send-Verbs @(
        '{"verb":"pane.close","paneId":"not-a-guid"}',
        '{"verb":"bogus.verb"}',
        '{"verb":"state.dump"}'
    )
    Start-Sleep -Seconds 1
    if ($p.HasExited) { throw "FAIL: app died on malformed payload" }

    $lines = Get-Content $LogPath
    if (-not ($lines | Where-Object { $_ -match 'ControlIpc\.json verb=pane\.close payload=' })) {
        throw "FAIL: malformed payload wasn't logged loudly"
    }
    if (-not ($lines | Where-Object { $_ -match 'ControlIpc\.unknown verb=bogus\.verb' })) {
        throw "FAIL: unknown verb wasn't logged"
    }
    if (-not ($lines | Where-Object { $_ -match 'STATE_DUMP' })) {
        throw "FAIL: state.dump after the probe never arrived (dispatch loop broken?)"
    }
    # Only the deliberately-provoked error may appear.
    $unexpected = $lines | Where-Object { $_ -match 'ERROR' -and $_ -notmatch 'ControlIpc\.json verb=pane\.close' }
    if ($unexpected) {
        $unexpected | ForEach-Object { Write-Warning $_ }
        throw "FAIL: unexpected errors in log"
    }
    Write-Host "loud-boundary probe OK (app survived, errors logged with payload)" -ForegroundColor Green

    Write-Host ""
    Write-Host "PASS: launch, spawn, splits, weights, moveDir x2, string flags, session lifecycle, malformed-payload resilience." -ForegroundColor Green
}
finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -EA SilentlyContinue }
    Remove-Item Env:PERCH_DATA_DIR, Env:PERCH_ENABLE_TEST_IPC -EA SilentlyContinue
}
