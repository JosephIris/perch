<#
.SYNOPSIS
Integration smoke test for the attention center (sidebar Needs-you / Projects
partition + framed rows, and the dashboard surface).

Launches the Debug build in an ISOLATED data dir via PERCH_DATA_DIR. This is
essential: a normally-installed or already-running perch shares
%APPDATA%\perch, which means the same sessions.json, the same
machine-global pipe names (\\.\pipe\perch\<paneId>), and the same errors.log.
Without isolation, the state pushes below land nondeterministically in the
OTHER process and the assertions flake.

It creates three sessions, pushes real waiting / done / notify state through
the per-pane IPC pipes (the exact path Claude Code's hooks use), screenshots
the sidebar for human review, and asserts every message was received with no
host errors. Throws (non-zero exit) on any failure.

.EXAMPLE
./scripts/test-attention.ps1
#>
[CmdletBinding()]
param(
  [string]$Exe     = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
  # Unique dir per run under C:\tmp: a reused dir keeps a WebView2 profile whose
  # lingering child processes lock files and stall the next cold init. (Use
  # C:\tmp, not %TEMP% — WebView2 user-data init under %LOCALAPPDATA%\Temp can
  # stall on a fresh profile.)
  [string]$DataDir = (Join-Path 'C:\tmp' "perch-attn-test-$(Get-Random)"),
  [string]$ShotPath = 'C:\tmp\perch-attn-sidebar.png'
)
$ErrorActionPreference = 'Stop'
# AppPaths.DataRoot = PERCH_DATA_DIR verbatim, but all data lives under
# <DataRoot>\perch\ (session store, errors.log, WebView2 profile).
$AppDir = Join-Path $DataDir 'perch'
$Log = Join-Path $AppDir 'errors.log'

# One JSON line per pipe connection. Spaced out so each connection is cleanly
# accepted+drained before the next (the per-pane IPC server accepts one
# connection at a time; the real perch CLI / Claude hooks are naturally spaced).
function Send-Pipe([string]$name, [string]$json) {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $name, [System.IO.Pipes.PipeDirection]::Out)
  $c.Connect(2500)
  $w = New-Object System.IO.StreamWriter($c)
  $w.WriteLine($json); $w.Flush(); $w.Dispose(); $c.Dispose()
  Start-Sleep -Milliseconds 500
}
function Ctl($obj) { Send-Pipe 'perch\control' ($obj | ConvertTo-Json -Compress) }
function First-Leaf($node) {
  if (-not $node.Children -or $node.Children.Count -eq 0) { return $node.Id }
  return First-Leaf $node.Children[0]
}

if (-not (Test-Path $Exe)) { throw "Debug build not found at $Exe -- run: dotnet build src/Perch -c Debug" }

# --- isolated, clean launch ------------------------------------------------
if (Test-Path $DataDir) { Remove-Item $DataDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
$env:PERCH_DATA_DIR = $DataDir          # isolate sessions.json / pipes / log
$env:PERCH_ENABLE_TEST_IPC = '1'        # enable the \\.\pipe\perch\control verb pipe
# Kill ONLY our dev/Debug build. The guard excludes any installed copy
# (e.g. %APPDATA%\perch\Perch.exe) so a running production perch is never
# touched, and we never kill msedgewebview2 (shared with other apps).
Get-Process -Name Perch -EA SilentlyContinue |
  Where-Object { $_.Path -like '*Debug*' -and $_.Path -notlike '*Roaming\perch\*' } |
  Stop-Process -Force
Start-Sleep -Milliseconds 600

$p = Start-Process -PassThru -FilePath $Exe
try {
  # Cold WebView2 init in a fresh data dir is slow (it builds a whole Chromium
  # profile), so wait for the app to write its first sessions.json rather than
  # a fixed sleep.
  $sj = Join-Path $AppDir 'sessions.json'
  $deadline = (Get-Date).AddSeconds(45)
  while (-not (Test-Path $sj) -and (Get-Date) -lt $deadline) {
    if ($p.HasExited) { throw "Perch exited early ($($p.ExitCode))" }
    Start-Sleep -Milliseconds 500
  }
  if (-not (Test-Path $sj)) { throw "app never wrote sessions.json (WebView2 init too slow?)" }
  Write-Host "launched pid=$($p.Id)  dataDir=$DataDir"

  # Fresh data dir starts with one session; add two so we get a populated
  # Needs-you section AND a Projects section.
  Ctl @{ verb = 'session.new' }
  Ctl @{ verb = 'session.new' }
  Start-Sleep -Milliseconds 1200

  $sessions = (Get-Content $sj -Raw | ConvertFrom-Json).Sessions
  if ($sessions.Count -lt 2) { throw "expected >=2 sessions, got $($sessions.Count)" }
  Write-Host "sessions: $($sessions.Count)"

  # Select each so its panes spawn (the per-pane IPC pipe goes live on spawn).
  foreach ($s in $sessions) { Ctl @{ verb = 'session.select'; id = $s.Id }; Start-Sleep -Milliseconds 1000 }

  # S0 -> waiting (feedback); S1 -> permission (blocked on you); rest stay idle.
  # Both land in the "Needs you" section.
  $l0 = (First-Leaf $sessions[0].Root) -replace '-', ''
  Send-Pipe "perch\$l0" '{"type":"status","state":"waiting"}'
  Send-Pipe "perch\$l0" '{"type":"notify","level":"warn","text":"Approve the deploy to main, or open a PR first?"}'

  $l1 = (First-Leaf $sessions[1].Root) -replace '-', ''
  Send-Pipe "perch\$l1" '{"type":"status","state":"permission"}'
  Send-Pipe "perch\$l1" '{"type":"notify","level":"error","text":"Needs your permission to run: git push"}'

  # Make the waiting session active, then capture the sidebar for review.
  Ctl @{ verb = 'session.select'; id = $sessions[0].Id }
  Start-Sleep -Milliseconds 800
  & "$PSScriptRoot\screenshot.ps1" -ProcessId $p.Id -OutDir (Split-Path $ShotPath) 2>&1 | Out-Null
  Copy-Item (Join-Path (Split-Path $ShotPath) 'current.png') $ShotPath -Force -EA SilentlyContinue
  Write-Host "sidebar screenshot: $ShotPath (review: Needs you = waiting+permission, Projects = idle)"
}
finally {
  Stop-Process -Id $p.Id -EA SilentlyContinue
}

# --- assertions ------------------------------------------------------------
$lines    = if (Test-Path $Log) { Get-Content $Log } else { @() }
$statusEv = @($lines | Where-Object { $_ -match 'PerchIpc\.recv.*type=status' })
$notifyEv = @($lines | Where-Object { $_ -match 'PerchIpc\.recv.*type=notify' })
$errors   = @($lines | Where-Object { $_ -match 'ERROR' })

Write-Host ""
Write-Host "status received: $($statusEv.Count)  notify received: $($notifyEv.Count)  errors: $($errors.Count)"
if ($errors.Count) { $errors | ForEach-Object { Write-Host "  $_" } }

$fail = @()
if ($statusEv.Count -lt 2) { $fail += "expected >=2 status messages, got $($statusEv.Count)" }
if ($notifyEv.Count -lt 2) { $fail += "expected >=2 notify messages, got $($notifyEv.Count)" }
if ($errors.Count -gt 0)   { $fail += "$($errors.Count) ERROR line(s) in host log" }
if ($fail.Count) { throw ("FAIL:`n  " + ($fail -join "`n  ")) }

Write-Host ""
Write-Host "PASS: attention state pipeline dispatched cleanly, no host errors."

# Best-effort cleanup of this run's unique data dir (WebView2 children may
# still hold locks for a moment; the OS clears any leftover from TEMP later).
Start-Sleep -Milliseconds 500
Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
