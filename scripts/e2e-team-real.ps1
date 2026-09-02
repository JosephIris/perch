<#
End-to-end against the REAL Claude Code: the things a fake claude cannot prove.

  - A brief actually gets written from a repository (headless `claude -p`).
  - A bot launched with the brief answers a room post typed into its pane, and
    its reply shows up in the room (transcript rows copied into the ledger).
  - One bot messages another with SendMessage and the room records it with the
    full text; the receiver's transcript row is printed so the inbound shape
    can be pinned (the `peer-in` carve-out).

COSTS TOKENS. One haiku brief over a three-file repo, and two bots × two short
turns on the account's default model — cents, but not zero. Run on purpose.

ISOLATION: own PERCH_DATA_DIR and test-IPC pipe; the real ~/.claude (auth,
transcripts) is used on purpose. Kills only the PID it launches. The scratch
repo is a throwaway under TEMP.
#>
param(
    [string]$ExePath  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\Perch.exe",
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools",
    [int]$CdpPort = 9338,
    [string]$BotModel = ""      # "" = account default; e.g. "sonnet" to pin
)
$ErrorActionPreference = 'Stop'

$PerchExe = Join-Path $ToolsDir 'perch.exe'
$DataDir  = Join-Path $env:TEMP ("perch-e2e-team-{0}" -f $PID)
$LogPath  = Join-Path $DataDir 'perch\errors.log'
$RepoDir  = Join-Path $DataDir 'notes-app'

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
    # team.request first: that is what copies the bots' transcript rows in.
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
    New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'perch'), (Join-Path $RepoDir 'src\web'), (Join-Path $RepoDir 'src\api') | Out-Null
    Set-Content -Path (Join-Path $RepoDir 'README.md') -Encoding utf8 -Value @'
# notes-app

A tiny notes app. `src/web` is the browser front end (vanilla TypeScript, one
`index.html`, no framework). `src/api` is the HTTP back end (Node, one file,
JSON over REST). Build: `npm run build` in each folder. Tests: `npm test`.
'@
    Set-Content -Path (Join-Path $RepoDir 'src\web\app.ts') -Encoding utf8 -Value "export function render(notes: string[]) { return notes.map(n => '<li>' + n + '</li>').join(''); }`n"
    Set-Content -Path (Join-Path $RepoDir 'src\api\server.js') -Encoding utf8 -Value "const notes = []; module.exports = { list: () => notes, add: (n) => notes.push(n) };`n"
    Push-Location $RepoDir
    try {
        & git init --quiet 2>$null
        & git add -A 2>$null; & git -c user.email=t@t -c user.name=t commit -qm init 2>$null
    } finally { Pop-Location }

    $env:PERCH_ENABLE_TEST_IPC = '1'
    $env:PERCH_DATA_DIR        = $DataDir
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$CdpPort"

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
    [Perch.WinPos]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, -3400, -3400, 1400, 900,
        ([Perch.WinPos]::NOZORDER -bor [Perch.WinPos]::NOACTIVATE)) | Out-Null
    if (-not (Wait-Until { (Log-Count 'Pane.spawn') -ge 1 } 25)) { throw "initial pane never spawned" }
    Start-Sleep -Milliseconds 600

    if (-not (Send-Verb 'project.add' @{ path = $RepoDir })) { throw "project.add rejected" }
    $projectsJson = Join-Path $DataDir 'perch\projects.json'
    if (-not (Wait-Until { (Test-Path $projectsJson) -and ((Get-Content $projectsJson -Raw) -match 'notes-app') } 10)) { throw "no project" }
    $proj = (Get-Content $projectsJson -Raw | ConvertFrom-Json).projects | Where-Object { $_.path -like "*notes-app*" } | Select-Object -First 1
    $pid2 = [string]$proj.id

    # --- 1. a brief is written from the repository ---------------------------
    Write-Host "`n[1] team.brief.generate writes a brief from the repo (haiku, capped)"
    [void](Send-Verb 'team.brief.generate' @{ jobId = 'j1'; projectId = $pid2; positionName = 'Frontend dev'; purpose = 'Owns everything under src/web: the page, its build and its tests.'; model = 'haiku' })
    Check "brief job started" (Wait-Until { (Log-Count 'Team.brief.start') -ge 1 } 10)
    $done = Wait-Until { ((Log-Count 'Team.brief.done') + (Log-Count 'Team.brief.fail')) -ge 1 } 300
    Check "brief job finished within 5 minutes" $done
    $briefLine = Log-Last 'Team.brief.'
    Check "brief job succeeded" ($briefLine -match 'Team\.brief\.done') "- $briefLine"
    Write-Host "      $briefLine"

    # --- 2. two bots on a seeded position ------------------------------------
    Write-Host "`n[2] two bots come up with the brief"
    $teamDir = Join-Path $RepoDir '.perch\team'
    New-Item -ItemType Directory -Force -Path (Join-Path $teamDir 'positions\frontend-dev'), (Join-Path $teamDir 'positions\backend-dev') | Out-Null
    $model = $BotModel
    Set-Content -Path (Join-Path $teamDir 'team.json') -Encoding utf8 -Value @"
{"v":1,"positions":[
 {"slug":"frontend-dev","name":"Frontend dev","purpose":"Owns everything under src/web.","referenceRepo":"","model":"$model","createdAtMs":1,"briefGeneratedAtMs":0,"briefModel":""},
 {"slug":"backend-dev","name":"Backend dev","purpose":"Owns everything under src/api.","referenceRepo":"","model":"$model","createdAtMs":1,"briefGeneratedAtMs":0,"briefModel":""}],
 "bots":[]}
"@
    Set-Content -Path (Join-Path $teamDir 'positions\frontend-dev\brief.md') -Encoding utf8 -Value "## Role`nYou own src/web. Keep replies to one or two lines. Never run a shell command unless asked."
    Set-Content -Path (Join-Path $teamDir 'positions\backend-dev\brief.md') -Encoding utf8 -Value "## Role`nYou own src/api. Keep replies to one or two lines. Never run a shell command unless asked."

    # A brand-new folder makes Claude Code ask "trust this folder?" before it
    # starts, and the session-start hook waits behind that question. The new
    # tab is the active session, so pty.send reaches its pane: answer with
    # Enter (harmless when there was no question — an empty submit).
    function Start-Bot { param([string]$Nick, [string]$Slug, [int]$Expected)
        [void](Send-Verb 'team.bot.create' @{ projectId = $pid2; nickname = $Nick; positionSlug = $Slug; worktree = 'false' })
        $up = Wait-Until { (Log-Count 'type=session') -ge $Expected } 12
        if (-not $up) {
            # The dialog's default is "No, exit" — Enter alone would close
            # Claude. Down selects "Yes, I trust this folder", then Enter.
            [void](Send-Verb 'pty.send' @{ text = "$([char]27)[B`r" })
            $up = Wait-Until { (Log-Count 'type=session') -ge $Expected } 60
        }
        return $up
    }
    Check "Ada's session started (real hook)" (Start-Bot 'Ada' 'frontend-dev' 1)
    Check "Bo's session started (real hook)" (Start-Bot 'Bo' 'backend-dev' 2)
    Start-Sleep -Seconds 6   # let the TUIs paint before typing into them

    # --- 3. a room post gets real replies -----------------------------------
    Write-Host "`n[3] @everyone gets a reply from each bot, visible in the room"
    [void](Send-Verb 'team.post' @{ projectId = $pid2; text = 'Reply with exactly one line: your name and what you own. Do not use any tools.'; to = 'everyone'; clientId = 'e1' })
    Check "delivered to both" (Wait-Until { (Log-Count 'Team.deliver') -ge 2 } 15)
    $gotBeats = Wait-Until {
        $d = Team-Dump $pid2
        $beats = @($d.ledger | Where-Object { $_.kind -eq 'beat' })
        (($beats | Where-Object { $_.from -eq 'Ada' }).Count -ge 1) -and (($beats | Where-Object { $_.from -eq 'Bo' }).Count -ge 1)
    } 180
    Check "both bots' replies landed in the room" $gotBeats
    $d = Team-Dump $pid2
    foreach ($b in @($d.ledger | Where-Object { $_.kind -eq 'beat' } | Select-Object -Last 4)) { Write-Host "      $($b.from): $($b.text)" }

    # --- 4. bot-to-bot: SendMessage observed with full text ------------------
    Write-Host "`n[4] Ada messages Bo; the room records it; Bo's transcript shows the inbound row"
    [void](Send-Verb 'team.post' @{ projectId = $pid2; text = 'Use your SendMessage tool to send bo exactly this message: PING-7731. Then reply done.'; to = 'Ada'; clientId = 'e2' })
    Check "delivered to Ada" (Wait-Until { (Log-Count 'Team.deliver') -ge 3 } 15)
    Check "the hook saw a SendMessage" (Wait-Until { (Log-Count 'type=peer.msg') -ge 1 } 180)
    Start-Sleep -Seconds 3
    $d = Team-Dump $pid2
    $peer = @($d.ledger | Where-Object { $_.kind -eq 'peer' }) | Select-Object -Last 1
    Check "a peer entry from Ada to Bo with the full text" ($peer -and $peer.from -eq 'Ada' -and $peer.text -like '*PING-7731*') "- $($peer | ConvertTo-Json -Compress)"
    Write-Host "      peer: $($peer | ConvertTo-Json -Compress)"

    # The inbound row, for the peer-in carve-out (TranscriptReader).
    # Only transcripts filed under the scratch repo's cwd (Claude Code names
    # the folder after the path), so this session's own transcript — which
    # contains this script's source — can't match.
    $recent = Get-ChildItem -Path (Join-Path $env:USERPROFILE '.claude\projects') -Recurse -Filter '*.jsonl' -EA SilentlyContinue |
        Where-Object { $_.DirectoryName -like '*perch-e2e-team*' -and $_.LastWriteTime -gt (Get-Date).AddMinutes(-20) }
    $rows = @()
    foreach ($f in $recent) {
        $rows += Select-String -Path $f.FullName -Pattern 'PING-7731' -SimpleMatch -EA SilentlyContinue |
            Where-Object { $_.Line -match '"type":"user"' } | ForEach-Object { $_.Line }
    }
    Check "Bo's transcript has the inbound user row" ($rows.Count -ge 1)
    foreach ($r in $rows | Select-Object -First 2) {
        $trim = if ($r.Length -gt 900) { $r.Substring(0, 900) + '…' } else { $r }
        Write-Host "      INBOUND ROW: $trim"
    }

    Write-Host ""
    if ($fails.Count -eq 0) { Write-Host "RESULT: PASS" -ForegroundColor Green }
    else { Write-Host "RESULT: FAIL ($($fails.Count)): $($fails -join '; ')" -ForegroundColor Red; $exit = 1 }
}
catch {
    Write-Host "`nRESULT: FAIL -- $($_.Exception.Message)" -ForegroundColor Red
    $exit = 1
}
finally {
    if ($exit -ne 0 -and (Test-Path $LogPath)) {
        Write-Host "`n  --- errors.log (team / pane / hook lines) ---" -ForegroundColor Yellow
        Select-String -Path $LogPath -Pattern 'Team\.|Pane\.spawn|type=session|type=status|ERROR|wrap|hooks' -EA SilentlyContinue |
            Select-Object -Last 30 | ForEach-Object { Write-Host "    $($_.Line)" }
    }
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -EA SilentlyContinue; Start-Sleep -Milliseconds 600 }
    foreach ($m in (Get-ChildItem -Path $env:TEMP -Filter 'perch-claude-brief-*.txt' -EA SilentlyContinue |
        Where-Object { (Get-Content $_.FullName -Raw -EA SilentlyContinue) -like "$RepoDir*" })) { Remove-Item $m.FullName -Force -EA SilentlyContinue }
    # Keep the data dir on failure so the log and the team folder can be read.
    if ($exit -eq 0) { Remove-Item $DataDir -Recurse -Force -EA SilentlyContinue }
    else { Write-Host "  kept for inspection: $DataDir" -ForegroundColor Yellow }
    Remove-Item Env:PERCH_ENABLE_TEST_IPC, Env:PERCH_DATA_DIR, Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -EA SilentlyContinue
}
exit $exit
