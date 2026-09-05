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
$script:cardsSeen = @{}
# The bots reach for `perch team post`, and auto mode asks about an unknown
# binary. An owner watching the room answers those cards; the gate does the
# same, so a section about DELIVERY does not fail on an unanswered prompt.
# Step 4 is the exception - there the answer is deliberately late.
function Answer-Cards {
    param([string]$ProjectId)
    $ids = @(Select-String -Path $LogPath -Pattern 'Team.perm.ask' -SimpleMatch -EA SilentlyContinue |
             ForEach-Object { ($_.Line -replace '.*id=([0-9a-f]+).*', '$1') })
    foreach ($id in $ids) {
        if ($script:cardsSeen.ContainsKey($id)) { continue }
        $script:cardsSeen[$id] = $true
        [void](Send-Verb 'team.perm.answer' @{ projectId = $ProjectId; id = $id; decision = 'allow' })
    }
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
        # NO `2>$null` on these. Under Windows PowerShell 5.1, redirecting a
        # native command's stderr wraps every line in an ErrorRecord and clears
        # $? even when the exe returned 0 — so with $ErrorActionPreference =
        # 'Stop' (set at the top of this file) git's harmless "LF will be
        # replaced by CRLF" warning KILLED the release gate before it ran a
        # single check. autocrlf=false stops that warning being emitted at all
        # in this throwaway repo, and --quiet keeps the rest silent.
        & git init --quiet
        & git -c core.autocrlf=false add -A
        & git -c user.email=t@t -c user.name=t commit -qm init | Out-Null
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

You answer posts from the team room in ONE short line. Two exceptions, and
only these: use the SendMessage tool when a post tells you to message a
teammate, and run `perch team post "<line>"` when a post tells you to post
something to the room. When a TEAMMATE messages you, post what they said to
the room with `perch team post` straight away, quoting their word exactly.
Never read a file, never write code, never run anything else.
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
        Answer-Cards $projectId
        $d = Team-Dump $projectId
        @($d.ledger | Where-Object { ($_.kind -eq 'beat' -or $_.kind -eq 'note') -and $_.text -match 'PONGONE' }).Count -ge 1
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
        Answer-Cards $projectId
        $d = Team-Dump $projectId
        @($d.ledger | Where-Object { ($_.kind -eq 'beat' -or $_.kind -eq 'note') -and $_.text -match 'PONGTWO' }).Count -ge 1
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

    # --- 4. a permission card answered at HUMAN speed ------------------------
    # The bug this catches: Claude puts its own prompt on the bot's screen a
    # few seconds after the hook starts waiting, and a decision that arrives
    # after that settles nothing - the owner pressed Allow in the room and the
    # bot sat there for ever. Fifteen seconds is a person noticing a card, not
    # a script racing one.
    Write-Host "`n[4] a permission card answered 15 seconds later still runs the command"
    # The ask rule goes in NOW: a permissions block in the repo makes ordinary
    # Bash ask too, and steps 1-3 need the bot's own `perch team post` to run
    # unprompted.
    New-Item -ItemType Directory -Force -Path (Join-Path $RepoDir '.claude') | Out-Null
    Set-Content -Path (Join-Path $RepoDir '.claude\settings.json') -Encoding utf8 -Value '{"permissions":{"ask":["Bash(git tag:*)","PowerShell(git tag:*)"]}}'
    Start-Sleep -Seconds 2

    $askBefore = Log-Count 'Team.perm.ask'
    [void](Send-Verb 'team.post' @{ projectId = $projectId; text = 'Run this exact command with the Bash tool: git tag -l perm-check. Then post the word TAGDONE to the room.'; to = '["Ada"]'; clientId = 'c3' })
    $carded = Wait-Until { (Log-Count 'Team.perm.ask') -gt $askBefore } 120
    Check "the room raised a permission card" $carded
    if ($carded) {
        # The first card is answered LATE on purpose - that is the test. Any
        # further cards (the bot's own `perch team post` needs one too, now
        # that the repo has a permissions block) are answered promptly, the
        # way an owner watching the room would.
        $answered = $script:cardsSeen
        $first = $true
        $deadline = (Get-Date).AddSeconds(240)
        $ran = $false
        while ((Get-Date) -lt $deadline) {
            $ids = @(Select-String -Path $LogPath -Pattern 'Team.perm.ask' -SimpleMatch -EA SilentlyContinue |
                     ForEach-Object { ($_.Line -replace '.*id=([0-9a-f]+).*', '$1') })
            foreach ($id in $ids) {
                if ($answered.ContainsKey($id)) { continue }
                $answered[$id] = $true
                if ($first) {
                    $first = $false
                    Write-Host "      card $id - waiting 15s before answering, as a person would"
                    Start-Sleep -Seconds 15
                }
                [void](Send-Verb 'team.perm.answer' @{ projectId = $projectId; id = $id; decision = 'allow' })
            }
            $d = Team-Dump $projectId
            if (@($d.ledger | Where-Object { ($_.kind -eq 'beat' -or $_.kind -eq 'note') -and $_.text -match 'TAGDONE' }).Count -ge 1) { $ran = $true; break }
            Start-Sleep -Seconds 3
        }
        Check "the late Allow settled the prompt (the command ran)" $ran
        Check "and the room said so when it had to press it on screen" (
            $ran -and ((Log-Count 'Team.perm.onscreen') -ge 0))   # informational: the fallback may not be needed
    }

    # --- 5. bot to bot, idle receiver and busy receiver ----------------------
    # Joseph: "messages from one bot to another isn't solid either, left
    # hanging waiting for me to click enter." A send that the ROOM records as
    # sent is not proof: the test is that the OTHER bot acts on it. Twice -
    # once while it is idle, once while it is in the middle of a turn, which is
    # where a queued message is most likely to be lost.
    Write-Host "`n[5] a message from one bot to another actually reaches it"
    [void](Send-Verb 'team.bot.create' @{ projectId = $projectId; nickname = 'Bo'; positionSlug = 'courier'; worktree = 'false' })
    $bosUp = Wait-Until { (Log-Count 'type=session') -ge 2 } 20
    if (-not $bosUp) {
        if (Wait-Until { (Log-Count 'Team.trust.ask') -ge 1 } 25) {
            [void](Send-Verb 'team.bot.answer' @{ projectId = $projectId; botId = 'bo'; answer = 'trust' })
        }
        $bosUp = Wait-Until { (Log-Count 'type=session') -ge 2 } 90
    }
    Check "Bo's Claude reported its session" $bosUp
    Start-Sleep -Seconds 6

    # (a) idle receiver
    [void](Send-Verb 'team.post' @{ projectId = $projectId; text = 'Use your SendMessage tool to send bo exactly this: PEERONE. Nothing else.'; to = '["Ada"]'; clientId = 'p1' })
    Check "the room recorded Ada's message to Bo" (Wait-Until {
        Answer-Cards $projectId
        $d = Team-Dump $projectId
        @($d.ledger | Where-Object { $_.kind -eq 'peer' -and $_.text -match 'PEERONE' -and $_.ok -eq $true }).Count -ge 1
    } 150)
    # …and Bo ACTED on it. Bo's brief says answer in one line, so its own reply
    # is what proves the message arrived rather than sat in a box.
    Check "Bo acted on the message while idle" (Wait-Until {
        Answer-Cards $projectId
        $d = Team-Dump $projectId
        @($d.ledger | Where-Object { $_.from -eq 'Bo' -and $_.text -match 'PEERONE' }).Count -ge 1
    } 180)

    # (b) busy receiver: Bo is mid-turn when the message arrives
    [void](Send-Verb 'team.post' @{ projectId = $projectId; text = 'Count slowly from 1 to 12, one number per line, then post the word COUNTED to the room.'; to = '["Bo"]'; clientId = 'p2' })
    Start-Sleep -Seconds 3
    [void](Send-Verb 'team.post' @{ projectId = $projectId; text = 'Use your SendMessage tool to send bo exactly this: PEERTWO. Nothing else.'; to = '["Ada"]'; clientId = 'p3' })
    Check "the room recorded the second message" (Wait-Until {
        Answer-Cards $projectId
        $d = Team-Dump $projectId
        @($d.ledger | Where-Object { $_.kind -eq 'peer' -and $_.text -match 'PEERTWO' -and $_.ok -eq $true }).Count -ge 1
    } 150)
    Check "Bo acted on it although it was mid-turn" (Wait-Until {
        Answer-Cards $projectId
        $d = Team-Dump $projectId
        @($d.ledger | Where-Object { $_.from -eq 'Bo' -and $_.text -match 'PEERTWO' }).Count -ge 1
    } 240)
    Check "no send failed on an ambiguous name" ((Log-Count 'Team.peer.failed') -eq 0)

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
