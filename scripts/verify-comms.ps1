<#
THE RELEASE GATE for the team room's delivery path. Real Perch, real Claude
Code, no fakes. Run it before every release; a release does not go out on a
red run.

It exists because every delivery bug so far has been a TIMING detail a fake
host cannot have. The fake claude in test-team.ps1 answers instantly; a real
one takes ten to twenty seconds to boot, paints a TUI over whatever was typed,
and reports its session through a hook. That gap is where two of Joseph's
posts were lost.

What it proves, end to end:

  [1] a bot comes up and a room post reaches it, and its reply comes back into
      the room (warm delivery, the ordinary case);
  [2] a post to a SLEEPING bot waits for that bot's Claude — nothing is typed
      into the booting pane — and lands once the session hook fires;
  [3] "Send again" (team.deliver.retry) types the same line again with no
      second post in the room.

COSTS TOKENS: one bot, three short turns, pinned to haiku by default — cents.

ISOLATION: its own PERCH_DATA_DIR and test-IPC pipe, a throwaway repo under
TEMP, and it kills only the PID it launched. The real ~/.claude is used on
purpose (that is the point: the real CLI, the real hooks).
#>
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools",
    [string]$BotModel = "haiku"     # cheapest model that follows a one-line instruction
)
$ErrorActionPreference = 'Stop'

$PerchExe = Join-Path $ToolsDir 'perch.exe'
$DataDir  = Join-Path $env:TEMP ("perch-comms-{0}" -f $PID)
$LogPath  = Join-Path $DataDir 'perch\errors.log'
$RepoDir  = Join-Path $DataDir 'comms-repo'

if (-not (Test-Path $ExePath))  { throw "Perch.exe not found: $ExePath (build first)" }
if (-not (Test-Path $PerchExe)) { throw "perch.exe not found: $PerchExe (build first)" }
if (-not (Get-Command claude -EA SilentlyContinue)) { throw "claude is not on PATH" }

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
function Log-Last { param([string]$Pat)
    $m = Select-String -Path $LogPath -Pattern $Pat -SimpleMatch -EA SilentlyContinue | Select-Object -Last 1
    if ($m) { return $m.Line } else { return '' }
}
function Wait-Until { param([scriptblock]$Cond, [int]$TimeoutSec = 15)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try { if (& $Cond) { return $true } } catch {}
        Start-Sleep -Milliseconds 500
    }
    return $false
}
function Team-Dump { param([string]$ProjectId)
    [void](Send-Verb 'team.request' @{ projectId = $ProjectId })
    Start-Sleep -Milliseconds 300
    $before = Log-Count 'TEAM_DUMP'
    [void](Send-Verb 'team.dump' @{ projectId = $ProjectId })
    if (-not (Wait-Until { (Log-Count 'TEAM_DUMP') -gt $before } 10)) { throw "team.dump never logged" }
    $line = Log-Last 'TEAM_DUMP'
    return ($line.Substring($line.IndexOf('TEAM_DUMP') + 9) | ConvertFrom-Json)
}
$fails = @()
function Check { param([string]$Name, [bool]$Ok, [string]$Detail = '')
    if ($Ok) { Write-Host "  [+] $Name" -ForegroundColor Green }
    else { Write-Host "  [-] $Name $Detail" -ForegroundColor Red; $script:fails += $Name }
}

$proc = $null
$exit = 0
try {
    if (Test-Path '\\.\pipe\perch\control') { throw "control pipe already exists - another test-IPC Perch is running." }

    Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'perch'), (Join-Path $RepoDir 'src') | Out-Null
    Set-Content -Path (Join-Path $RepoDir 'README.md') -Encoding utf8 -Value "# comms-repo`n`nA throwaway repository for the delivery gate."
    Set-Content -Path (Join-Path $RepoDir 'src\app.ts') -Encoding utf8 -Value "export const hello = () => 'hi';`n"
    Push-Location $RepoDir
    try {
        & git init --quiet 2>$null
        & git add -A 2>$null; & git -c user.email=t@t -c user.name=t commit -qm init 2>$null
    } finally { Pop-Location }

    # The team, seeded on disk: no brief generation, so the gate costs one
    # bot's turns and nothing else.
    $teamDir = Join-Path $RepoDir '.perch\team'
    New-Item -ItemType Directory -Force -Path (Join-Path $teamDir 'positions\courier') | Out-Null
    Set-Content -Path (Join-Path $teamDir 'team.json') -Encoding utf8 -Value @"
{"v":1,"positions":[
 {"slug":"courier","name":"Courier","purpose":"Answers room posts in one line.","referenceRepo":"","model":"$BotModel","createdAtMs":1,"briefGeneratedAtMs":0,"briefModel":""}],
 "bots":[]}
"@
    Set-Content -Path (Join-Path $teamDir 'positions\courier\brief.md') -Encoding utf8 -Value @'
## Role

You answer posts from the team room. Reply with ONE short line and nothing
else. Never run a shell command, never read a file, never use any tool: the
answer is always just the line asked for.
'@

    $env:PERCH_ENABLE_TEST_IPC = '1'
    $env:PERCH_DATA_DIR        = $DataDir
    # Perch launched from inside a Claude Code session would otherwise make
    # every pane's claude a CHILD session (no transcript, no hooks).
    Remove-Item Env:\CLAUDECODE -EA SilentlyContinue
    Remove-Item Env:\CLAUDE_CODE_CHILD_SESSION -EA SilentlyContinue

    Write-Host "Launching isolated Perch with the REAL claude (data: $DataDir)"
    $proc = Start-Process -PassThru -FilePath $ExePath
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($proc.HasExited) { throw "Perch exited early (code $($proc.ExitCode))" }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($proc.MainWindowHandle -eq [IntPtr]::Zero) { throw "main window never appeared" }
    # Off-screen: the gate must never steal the desktop from whoever ran it.
    [Perch.WinPos]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, -3400, -3400, 1400, 900,
        ([Perch.WinPos]::NOZORDER -bor [Perch.WinPos]::NOACTIVATE)) | Out-Null
    if (-not (Wait-Until { (Log-Count 'Pane.spawn') -ge 1 } 25)) { throw "initial pane never spawned" }
    Start-Sleep -Milliseconds 600

    if (-not (Send-Verb 'project.add' @{ path = $RepoDir })) { throw "project.add rejected" }
    $projectsJson = Join-Path $DataDir 'perch\projects.json'
    if (-not (Wait-Until { (Test-Path $projectsJson) -and ((Get-Content $projectsJson -Raw) -match 'comms-repo') } 10)) { throw "no project" }
    $proj = (Get-Content $projectsJson -Raw | ConvertFrom-Json).projects | Where-Object { $_.path -like "*comms-repo*" } | Select-Object -First 1
    $projectId = [string]$proj.id

    # --- 1. a bot comes up, a post reaches it, its reply comes back ----------
    Write-Host "`n[1] warm delivery: a post reaches a running bot and is answered"
    [void](Send-Verb 'team.bot.create' @{ projectId = $projectId; nickname = 'Ada'; positionSlug = 'courier'; worktree = 'false' })
    $up = Wait-Until { (Log-Count 'type=session') -ge 1 } 15
    if (-not $up) {
        # A brand-new folder makes Claude ask "trust this folder?"; the room
        # raises a card for it, and this is the card's own answer.
        if (Wait-Until { (Log-Count 'Team.trust.ask') -ge 1 } 25) {
            [void](Send-Verb 'team.bot.answer' @{ projectId = $projectId; botId = 'ada'; answer = 'trust' })
        } else {
            [void](Send-Verb 'pty.send' @{ text = "$([char]27)[B`r" })
        }
        $up = Wait-Until { (Log-Count 'type=session') -ge 1 } 90
    }
    Check "Ada's Claude reported its session" $up
    if (-not $up) { throw "the bot never started; nothing below can be tested" }
    Start-Sleep -Seconds 6           # let the TUI paint before typing into it

    $before = Log-Count 'Team.deliver'
    [void](Send-Verb 'team.post' @{ projectId = $projectId; text = 'Reply with exactly this word and nothing else: PONGONE'; to = '["Ada"]'; clientId = 'c1' })
    Check "the post was typed into the bot" (Wait-Until { (Log-Count 'Team.deliver') -gt $before } 20)
    Check "the prompt-submit hook confirmed it" (Wait-Until { (Log-Last 'Team.submit') -match 'confirmed' } 30)
    Check "the bot's answer came back into the room" (Wait-Until {
        $d = Team-Dump $projectId
        @($d.ledger | Where-Object { $_.kind -eq 'beat' -and $_.text -match 'PONGONE' }).Count -ge 1
    } 180)
    Check "no 'didn't take the post' row" ((Log-Count "Team.submit") -ge 1 -and (Log-Count 'gave up') -eq 0)

    # --- 2. the case that broke: a post to a bot with no terminal ------------
    # Restart Perch. The bot's tab comes back, but nothing spawns a terminal
    # for it until someone clicks it — which is exactly the state Joseph's
    # bots were in when two posts were lost. Sleeping a tab is NOT the same
    # thing: its Claude survives, so there is no boot to race.
    Write-Host "`n[2] cold delivery: after a restart, the post waits for the bot's Claude"
    Stop-Process -Id $proc.Id -Force -EA SilentlyContinue
    Start-Sleep -Seconds 3
    $proc = Start-Process -PassThru -FilePath $ExePath
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($proc.HasExited) { throw "Perch exited early on restart (code $($proc.ExitCode))" }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($proc.MainWindowHandle -eq [IntPtr]::Zero) { throw "main window never came back" }
    [Perch.WinPos]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, -3400, -3400, 1400, 900,
        ([Perch.WinPos]::NOZORDER -bor [Perch.WinPos]::NOACTIVATE)) | Out-Null
    Start-Sleep -Seconds 3
    # The launch prompt is deliberately NOT answered: that is what holds every
    # restored pane's spawn, so the bot's tab has no terminal at all — the
    # exact state Joseph's bots were in. Perch must start it for the post.

    $deliverBefore = Log-Count 'Team.deliver'
    $sessionsBefore = Log-Count 'type=session'
    $startNeededBefore = Log-Count 'Team.start.needed'
    [void](Send-Verb 'team.post' @{ projectId = $projectId; text = 'Reply with exactly this word and nothing else: PONGTWO'; to = '["Ada"]'; clientId = 'c2' })
    # THE assertion this gate exists for: while the pane is starting, nothing
    # is typed into it. A shell answers long before Claude does.
    Check "Perch started the bot's terminal for the post" (Wait-Until { (Log-Count 'Team.start.needed') -gt $startNeededBefore } 15)
    Start-Sleep -Seconds 4
    $typedEarly = (Log-Count 'Team.deliver') -gt $deliverBefore
    $claudeUpEarly = (Log-Count 'type=session') -gt $sessionsBefore
    Check "nothing was typed into the pane while it was starting" ((-not $typedEarly) -or $claudeUpEarly) `
        "- Team.deliver fired before the session hook"
    Check "the bot's Claude came up" (Wait-Until { (Log-Count 'type=session') -gt $sessionsBefore } 120)
    Check "the parked post was delivered after that" (Wait-Until { (Log-Count 'Team.deliver') -gt $deliverBefore } 30)
    Check "the woken bot answered in the room" (Wait-Until {
        $d = Team-Dump $projectId
        @($d.ledger | Where-Object { $_.kind -eq 'beat' -and $_.text -match 'PONGTWO' }).Count -ge 1
    } 180)
    $d = Team-Dump $projectId
    $cold = @($d.ledger | Where-Object { $_.kind -eq 'user' -and $_.text -match 'PONGTWO' }) | Select-Object -First 1
    # The ledger only appends, so a post that landed LATE is marked by a
    # "delivered" row naming its number - that is what the room reads, and
    # what stops it saying "waiting for the bot" for good.
    $mark = @($d.ledger | Where-Object { $_.event -eq 'delivered' -and $_.note -eq [string]$cold.seq })
    Check "the room marks that post as delivered" ($null -ne $cold -and $mark.Count -ge 1)
    Check "the room never said the bot didn't take it" (@($d.ledger | Where-Object { $_.event -eq 'undelivered' }).Count -eq 0)

    # --- 3. Send again types the same line, with no second post -------------
    Write-Host "`n[3] Send again re-types the same line and adds no second post"
    $postSeq = [int]$cold.seq
    $postsBefore = @($d.ledger | Where-Object { $_.kind -eq 'user' }).Count
    $deliverBefore = Log-Count 'Team.retry'
    [void](Send-Verb 'team.deliver.retry' @{ projectId = $projectId; seq = $postSeq; botId = 'ada' })
    Check "the host typed it again" (Wait-Until { (Log-Last 'Team.retry') -match 'ok=True' } 20)
    $d2 = Team-Dump $projectId
    Check "no second post appeared in the room" (@($d2.ledger | Where-Object { $_.kind -eq 'user' }).Count -eq $postsBefore)

    Write-Host ""
    if ($fails.Count -gt 0) {
        Write-Host "DELIVERY GATE FAILED: $($fails -join ', ')" -ForegroundColor Red
        Write-Host "Do not release. The log is at $LogPath" -ForegroundColor Red
        $exit = 1
    } else {
        Write-Host "DELIVERY GATE PASSED - the room reaches its bots, warm and cold." -ForegroundColor Green
    }
}
catch {
    Write-Host "ERROR: $_" -ForegroundColor Red
    if (Test-Path $LogPath) { Write-Host "log: $LogPath" }
    $exit = 1
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -EA SilentlyContinue }
    Remove-Item Env:\PERCH_ENABLE_TEST_IPC -EA SilentlyContinue
    Remove-Item Env:\PERCH_DATA_DIR -EA SilentlyContinue
    # The data dir is left behind ON FAILURE so the log can be read.
    if ($exit -eq 0) { Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue }
}
exit $exit
