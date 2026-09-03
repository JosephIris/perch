<#
End-to-end: the team feature's host plumbing, against a FAKE claude.

What a unit test cannot see, and this can:

  - The bot's brief actually reaches the `claude` launch. The host writes a
    marker file, the pane's shell inherits PERCH_PANE_ID, the wrap-claude shim
    reads the marker and appends --append-system-prompt-file. Three processes,
    one contract - so a fake `claude` records the argv it was started with.
  - The owner's post is typed into the bot's PTY only once a Claude is up
    (Team.deliver in the log), and is parked until then (Team.parked).
  - The ledger, roster and markers change as bots join and leave.

The fake claude sits FIRST on PATH after Perch's own tools dir, so the real
shim resolves to it (BinResolver skips every perch dir). It fires the
session-start hook like the real thing, then idles in `cmd /k` so the pane
looks like a running agent. It never fires prompt-submit, so every typed post
is "unconfirmed": the host's Enter-again retries and its give-up row are
exactly what section 3 checks.

ISOLATION / SAFETY (same rules as test-board.ps1):
  - Unique PERCH_DATA_DIR keyed to this shell's PID; CLAUDE_CONFIG_DIR isolated.
  - Aborts if a test-IPC control pipe is already up.
  - Kills ONLY the PID this script launches.
  - Window parked off-screen at a real size so layout/render still run.
  - The scratch repo is a throwaway `git init` under TEMP, never your checkout.

NOTE: the running app serves wwwroot from the BIN OUTPUT, so build first:
  ./scripts/build.ps1
#>
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools",
    [int]$CdpPort = 9336
)
$ErrorActionPreference = 'Stop'

$PerchExe = Join-Path $ToolsDir 'perch.exe'
$DataDir  = Join-Path $env:TEMP ("perch-team-{0}" -f $PID)
$LogPath  = Join-Path $DataDir 'perch\errors.log'
$RepoDir  = Join-Path $DataDir 'repo'
$FakeDir  = Join-Path $DataDir 'fake-claude'
$Capture  = Join-Path $DataDir 'claude-argv.txt'

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
function Wait-Until { param([scriptblock]$Cond, [int]$TimeoutSec = 15)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try { if (& $Cond) { return $true } } catch {}
        Start-Sleep -Milliseconds 200
    }
    return $false
}
function Team-Dump { param([string]$ProjectId)
    $before = Log-Count 'TEAM_DUMP'
    [void](Send-Verb 'team.dump' @{ projectId = $ProjectId })
    if (-not (Wait-Until { (Log-Count 'TEAM_DUMP') -gt $before } 10)) { throw "team.dump never logged" }
    $line = (Select-String -Path $LogPath -Pattern 'TEAM_DUMP' -SimpleMatch | Select-Object -Last 1).Line
    $json = $line.Substring($line.IndexOf('TEAM_DUMP') + 9)
    return ($json | ConvertFrom-Json)
}
function Brief-Markers {
    # Every brief marker in %TEMP% that points into OUR scratch repo.
    return @(Get-ChildItem -Path $env:TEMP -Filter 'perch-claude-brief-*.txt' -EA SilentlyContinue |
        Where-Object { (Get-Content $_.FullName -Raw -EA SilentlyContinue) -like "$RepoDir*" })
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
$exit = 0
try {
    if (Test-Path '\\.\pipe\perch\control') {
        throw "control pipe already exists - another test-IPC Perch is running. Aborting to avoid driving the wrong instance."
    }

    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'perch') | Out-Null
    New-Item -ItemType Directory -Force -Path $RepoDir, $FakeDir, (Join-Path $DataDir 'claude\projects') | Out-Null

    Push-Location $RepoDir
    try {
        & git init --quiet 2>$null
        Set-Content -Path (Join-Path $RepoDir 'README.md') -Value 'scratch' -Encoding utf8
        & git add -A 2>$null; & git -c user.email=t@t -c user.name=t commit -qm init 2>$null
    } finally { Pop-Location }

    # The fake claude: record argv, fire the session-start hook with the id the
    # host minted, then the stop hook (so the pane reads as an agent at rest,
    # not one still working — a working bot's unconfirmed post is "queued",
    # a resting one's is "stuck", and section 3 wants the latter), then idle.
    $fake = @"
@echo off
setlocal
echo ARGV=%* >> "$Capture"
set SID=
:loop
if "%~1"=="" goto done
if "%~1"=="--session-id" set SID=%~2
if "%~1"=="-p" goto headless
shift
goto loop
:headless
echo not-json
exit /b 0
:done
echo {"session_id":"%SID%","source":"startup"} | "$PerchExe" hooks claude session-start
echo {"session_id":"%SID%"} | "$PerchExe" hooks claude stop
cmd /k
"@
    Set-Content -Path (Join-Path $FakeDir 'claude.cmd') -Value $fake -Encoding ascii

    $env:PERCH_ENABLE_TEST_IPC = '1'
    $env:PERCH_DATA_DIR        = $DataDir
    $env:CLAUDE_CONFIG_DIR     = Join-Path $DataDir 'claude'
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$CdpPort"
    $oldPath = $env:PATH
    $env:PATH = "$FakeDir;$oldPath"

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

    # --- 0. a project, and a position seeded on disk ------------------------
    Write-Host "`n[0] register the scratch repo and seed a position"
    if (-not (Send-Verb 'project.add' @{ path = $RepoDir })) { throw "project.add rejected" }
    $projectsJson = Join-Path $DataDir 'perch\projects.json'
    if (-not (Wait-Until { (Test-Path $projectsJson) -and ((Get-Content $projectsJson -Raw) -match 'repo') } 10)) {
        throw "projects.json never listed the repo"
    }
    $proj = (Get-Content $projectsJson -Raw | ConvertFrom-Json).projects | Where-Object { $_.path -like "*repo*" } | Select-Object -First 1
    $pid2 = [string]$proj.id
    Check "project registered" ($pid2.Length -gt 0)

    $teamDir = Join-Path $RepoDir '.perch\team'
    New-Item -ItemType Directory -Force -Path (Join-Path $teamDir 'positions\frontend-dev') | Out-Null
    Set-Content -Path (Join-Path $teamDir 'team.json') -Encoding utf8 -Value @'
{"v":1,"positions":[{"slug":"frontend-dev","name":"Frontend dev","purpose":"Owns src/web.","referenceRepo":"","model":"","createdAtMs":1,"briefGeneratedAtMs":0,"briefModel":""}],"bots":[]}
'@
    Set-Content -Path (Join-Path $teamDir 'positions\frontend-dev\brief.md') -Value "## Role`nYou own src/web." -Encoding utf8
    # A repo where boards have run already: Perch's boards-only .gitignore is
    # there, and opening the team must rewrite it so the team folder travels.
    Set-Content -Path (Join-Path $RepoDir '.perch\.gitignore') -Value "# Perch boards`n*" -Encoding ascii

    # --- 1. a bot joins: files, markers, and the launch argv ----------------
    Write-Host "`n[1] team.bot.create opens a tab whose claude gets the brief"
    $spawnsBefore = Log-Count 'Pane.spawn'
    if (-not (Send-Verb 'team.bot.create' @{ projectId = $pid2; nickname = 'Ada'; positionSlug = 'frontend-dev'; worktree = 'false' })) {
        throw "team.bot.create rejected"
    }
    Check "Team.create logged" (Wait-Until { (Log-Count 'Team.create') -ge 1 } 15)
    Check "a shell spawned for the bot's tab" (Wait-Until { (Log-Count 'Pane.spawn') -gt $spawnsBefore } 20)
    Check "the fake claude was launched" (Wait-Until { Test-Path $Capture } 30)
    Start-Sleep -Milliseconds 800
    $argv = if (Test-Path $Capture) { Get-Content $Capture -Raw } else { '' }
    Check "launched with --name ada" ($argv -match '--name ada\b') "- argv: $argv"
    Check "launched with the brief appended" ($argv -match '--append-system-prompt-file "?[^"]*\\\.perch\\team\\local\\bots\\ada\\system\.md') "- argv: $argv"
    Check "system.md written (local)" (Test-Path (Join-Path $teamDir 'local\bots\ada\system.md'))
    Check "roster.md names the address" ((Get-Content (Join-Path $teamDir 'local\roster.md') -Raw) -match '`ada`')
    Check "the bot's context carries its memory" ((Get-Content (Join-Path $teamDir 'local\bots\ada\context.md') -Raw) -match '# Your memory')
    Check "memory.md seeded in the shared folder" (Test-Path (Join-Path $teamDir 'bots\ada\memory.md'))
    Check "team.json carries the face, not the session" ((Get-Content (Join-Path $teamDir 'team.json') -Raw) -match '"look"' -and -not ((Get-Content (Join-Path $teamDir 'team.json') -Raw) -match '"sessionId"'))
    Check ".perch/.gitignore lets the team travel" ((Get-Content (Join-Path $RepoDir '.perch\.gitignore') -Raw) -match 'team/local/')
    $markers = Brief-Markers
    Check "exactly one brief marker points into the repo" ($markers.Count -eq 1) "- found $($markers.Count)"
    Check "session-start hook reached the host" (Wait-Until { (Log-Count 'type=session') -ge 1 } 15)

    $dump = Team-Dump $pid2
    Check "the bot is on the team with a session" (($dump.team.bots.Count -eq 1) -and ($dump.team.bots[0].sessionId))
    Check "the room logged the join" (@($dump.ledger | Where-Object { $_.event -eq 'joined' }).Count -eq 1)

    # --- 2. a post reaches the running bot ----------------------------------
    Write-Host "`n[2] team.post is typed into the bot's terminal"
    $deliverBefore = Log-Count 'Team.deliver'
    if (-not (Send-Verb 'team.post' @{ projectId = $pid2; text = 'reply with the word PONG'; to = 'everyone'; clientId = 'c1' })) {
        throw "team.post rejected"
    }
    Check "delivered to the bot" (Wait-Until { (Log-Count 'Team.deliver') -gt $deliverBefore } 10)
    $dump = Team-Dump $pid2
    $post = @($dump.ledger | Where-Object { $_.kind -eq 'user' }) | Select-Object -Last 1
    Check "the post is in the ledger with its clientId" ($post -and $post.clientId -eq 'c1')
    # Delivery is a fact on the post when Claude was already up; a "delivered"
    # row follows only when the post had to wait for the session-start hook.
    Check "and it is marked delivered" (($post -and $post.delivered -eq $true) -or
        (@($dump.ledger | Where-Object { $_.event -eq 'delivered' }).Count -ge 1))

    # --- 3. an untagged post goes to everyone; an unconfirmed submit is retried
    Write-Host "`n[3] an untagged post goes to everyone (here: the one bot), and the submit is watched"
    $deliverBefore = Log-Count 'Team.deliver'
    [void](Send-Verb 'team.post' @{ projectId = $pid2; text = 'how is it going'; clientId = 'c2' })
    Check "delivered to everyone" (Wait-Until { (Log-Count 'Team.deliver') -gt $deliverBefore } 10)
    $dump = Team-Dump $pid2
    $post = @($dump.ledger | Where-Object { $_.kind -eq 'user' -and $_.clientId -eq 'c2' }) | Select-Object -Last 1
    Check "the post is addressed to everyone" ($post -and $post.to -eq 'everyone')
    Check "no router ran" ((Log-Count 'Team.route') -eq 0)
    # The fake claude never submits anything, so the prompt-submit hook never
    # echoes the line: Enter is pressed again twice, then the room is told.
    Check "Enter was pressed again" (Wait-Until { (Log-Count 'enter-again=1') -ge 1 } 6)
    Check "and again" (Wait-Until { (Log-Count 'enter-again=2') -ge 1 } 6)
    Check "then the room says the post is stuck" (Wait-Until {
        @((Team-Dump $pid2).ledger | Where-Object { $_.event -eq 'undelivered' -and $_.text -like "*didn't take the post*" }).Count -ge 1 } 10)

    # --- 4. a second bot; everyone fans out, tagged or not ------------------
    Write-Host "`n[4] a second bot: @everyone and an untagged post both reach both"
    if (-not (Send-Verb 'team.bot.create' @{ projectId = $pid2; nickname = 'Bo'; positionSlug = 'frontend-dev'; worktree = 'false' })) {
        throw "second team.bot.create rejected"
    }
    Check "second Team.create logged" (Wait-Until { (Log-Count 'Team.create') -ge 2 } 15)
    Check "second claude launched" (Wait-Until { ((Get-Content $Capture -Raw -EA SilentlyContinue) -split "`n" | Where-Object { $_ -match 'ARGV=' }).Count -ge 2 } 30)
    Check "second session-start reached the host" (Wait-Until { (Log-Count 'type=session') -ge 2 } 15)
    Start-Sleep -Milliseconds 500
    $deliverBefore = Log-Count 'Team.deliver'
    [void](Send-Verb 'team.post' @{ projectId = $pid2; text = 'introduce yourselves'; to = 'everyone'; clientId = 'c3' })
    Check "@everyone delivered twice" (Wait-Until { (Log-Count 'Team.deliver') -ge ($deliverBefore + 2) } 10) `
        "- deliveries went $deliverBefore -> $(Log-Count 'Team.deliver')"

    # Untagged with two bots: no router, no guessing — both get it and each
    # decides for itself (the roster says how).
    $deliverBefore = Log-Count 'Team.deliver'
    [void](Send-Verb 'team.post' @{ projectId = $pid2; text = 'who owns the sidebar?'; clientId = 'c4' })
    Check "untagged post delivered twice" (Wait-Until { (Log-Count 'Team.deliver') -ge ($deliverBefore + 2) } 10) `
        "- deliveries went $deliverBefore -> $(Log-Count 'Team.deliver')"
    $dump = Team-Dump $pid2
    Check "and recorded as to everyone" (@($dump.ledger | Where-Object { $_.clientId -eq 'c4' -and $_.to -eq 'everyone' }).Count -eq 1)
    Check "nothing asked the owner who it was for" (@($dump.ledger | Where-Object { $_.event -eq 'error' }).Count -eq 0)

    # --- 5. removing a bot -------------------------------------------------
    Write-Host "`n[5] team.bot.remove closes the tab and clears its markers"
    $markersBefore = (Brief-Markers).Count
    [void](Send-Verb 'team.bot.remove' @{ projectId = $pid2; botId = 'bo'; closeTab = 'true' })
    Check "Bo left the room" (Wait-Until { @((Team-Dump $pid2).ledger | Where-Object { $_.event -eq 'left' }).Count -ge 1 } 10)
    Start-Sleep -Milliseconds 800
    $dump = Team-Dump $pid2
    Check "one bot remains" ($dump.team.bots.Count -eq 1) "- $($dump.team.bots.Count)"
    Check "Bo's brief marker is gone" ((Brief-Markers).Count -eq ($markersBefore - 1)) "- $markersBefore -> $((Brief-Markers).Count)"
    Check "roster no longer lists bo" (-not ((Get-Content (Join-Path $teamDir 'local\roster.md') -Raw) -match 'Bo \(session name'))

    Write-Host ""
    if ($fails.Count -eq 0) { Write-Host "RESULT: PASS" -ForegroundColor Green }
    else { Write-Host "RESULT: FAIL ($($fails.Count)): $($fails -join '; ')" -ForegroundColor Red; $exit = 1 }
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
    # The fake's `cmd /k` children die with the app's job object; sweep the
    # markers our bots left in %TEMP% so a later run starts clean.
    foreach ($m in (Brief-Markers)) { Remove-Item $m.FullName -Force -EA SilentlyContinue }
    if ($oldPath) { $env:PATH = $oldPath }
    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    Remove-Item Env:PERCH_ENABLE_TEST_IPC, Env:PERCH_DATA_DIR, Env:CLAUDE_CONFIG_DIR,
                Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -EA SilentlyContinue
}
exit $exit
