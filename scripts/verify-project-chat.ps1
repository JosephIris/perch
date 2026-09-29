<#
LIVE check of the project chat + threads with the REAL Claude Code CLI.
Modeled on verify-comms.ps1 (the team-room gate). Real Perch, real claude,
no fakes: the bugs this guards (lines lost on the way into a thread, Stop
that left a thread "working", replies that never reached a question) are all
timing and TUI details a fake cannot have.

  [1] a project chat starts a thread (the coordinator runs `perch thread new`)
      and the thread's Claude comes up and finishes its first turn;
  [2] a short steer (thread.send) is submitted (Delivery.submitted, the
      tagged prompt in the thread's transcript) and answered;
  [2b] the board: a thread and the coordinator each put a card on it with
      `perch thread board set`, with no permission prompt;
  [3] a long message (~1,400 chars) from the user AND from the coordinator
      reaches the thread WHOLE: it goes by file (LineDelivery.SaveLong), the
      thread reads that file (no permission prompt) and quotes the code word
      that is only in the message's LAST sentence;
  [4] Stop mid-turn: a slow task, a line queued behind it, thread.stop ->
      state leaves "working" at once, a second Stop sends no second Escape
      (no rewind menu), the queued line goes in and is answered, and a fresh
      line after that is answered too;
  [5] a thread on an AskUserQuestion dialog: thread.send dismisses it (Escape)
      and the reply lands as a clean prompt and is answered;
  [6] no Delivery.gaveup / "couldn't get a message into" anywhere in the run.
  [7] resolving a thread puts it to sleep; a message wakes it and is answered.
  [8] a thread marked as grown long (thread.fresh) takes its next message in
      a new Claude session in the same pane, whose prompt says where it left
      off — and it is not reported as failed.

COSTS TOKENS: coordinator and thread are pinned to -Model (haiku by default)
through ANTHROPIC_MODEL in the launched app's environment. About 15-20 short
haiku turns; the run prints an estimate from the transcripts at the end.

ISOLATION: its own PERCH_DATA_DIR (inside TEMP), a snapshot copy of the build
output (another agent rebuilding bin/Debug can't swap DLLs under it), a
throwaway repo, its own CDP port, and it kills only the PID it launched. The
test-IPC control pipe name is global (perch\control) - the run refuses to
start if another test instance already owns it. Claude-session markers,
NO_COLOR and a parent Perch pane's PERCH_PIPE/PERCH_PANE_ID are removed from
the launched app's environment. The real ~/.claude is used on purpose.

  pwsh -File scripts/verify-project-chat.ps1            # build first: ./scripts/build.ps1
  pwsh -File scripts/verify-project-chat.ps1 -Model sonnet -Keep
#>
param(
    [string]$BinDir  = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64",
    [string]$Model   = "haiku",
    [int]   $CdpPort = 9347,
    [switch]$Keep     # keep the data dir even on a green run
)
$ErrorActionPreference = 'Stop'

$TempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
$DataDir  = [IO.Path]::GetFullPath((Join-Path $TempRoot ("perch-pchat-{0}" -f [Guid]::NewGuid().ToString('N').Substring(0, 12))))
if (-not $DataDir.StartsWith($TempRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'data dir must be inside TEMP' }
$LogPath  = Join-Path $DataDir 'perch\errors.log'
$RepoDir  = Join-Path $DataDir 'pchat-repo'
$AppDir   = Join-Path $DataDir 'app'
$BinDir   = [IO.Path]::GetFullPath($BinDir)

if (-not (Test-Path "$BinDir\Perch.exe")) { throw "Perch.exe not found in $BinDir (build first)" }
if (-not (Get-Command claude -EA SilentlyContinue)) { throw "claude is not on PATH" }
if (Test-Path '\\.\pipe\perch\control') { throw "control pipe perch\control already exists - another test-IPC Perch is running." }
try { $t = [Net.Sockets.TcpClient]::new(); $t.Connect('127.0.0.1', $CdpPort); $t.Close(); throw "port $CdpPort is taken - pass -CdpPort" }
catch [Net.Sockets.SocketException] { }

if (-not ('PChat.WinPos' -as [type])) {
    Add-Type @'
using System; using System.Runtime.InteropServices;
namespace PChat { public static class WinPos {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int X, int Y, int cx, int cy, uint flags);
} }
'@
}

# ---- helpers ----------------------------------------------------------------
function Send-Json { param([hashtable]$Msg)
    $json = ($Msg | ConvertTo-Json -Compress -Depth 5)
    for ($i = 0; $i -lt 3; $i++) {
        try {
            $c = [IO.Pipes.NamedPipeClientStream]::new('.', 'perch\control', [IO.Pipes.PipeDirection]::Out)
            $c.Connect(3000)
            $w = [IO.StreamWriter]::new($c, [Text.UTF8Encoding]::new($false)); $w.Write($json + "`n"); $w.Flush(); $w.Dispose(); $c.Dispose()
            Start-Sleep -Milliseconds 150
            return
        } catch { Start-Sleep -Milliseconds 300 }
    }
    throw "control pipe send failed: $($Msg.verb)"
}
function Log-Lines { param([string]$Pat)
    if (-not (Test-Path $LogPath)) { return @() }
    return ,@(Select-String -Path $LogPath -Pattern $Pat -SimpleMatch -EA SilentlyContinue | ForEach-Object { $_.Line })
}
function Log-Count { param([string]$Pat) return (Log-Lines $Pat).Count }
function Wait-Until { param([scriptblock]$Cond, [int]$TimeoutSec = 15, [int]$PollMs = 700)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try { if (& $Cond) { return $true } } catch { }
        Start-Sleep -Milliseconds $PollMs
    }
    return $false
}
function State-Dump {
    $before = Log-Count 'STATE_DUMP'
    Send-Json @{ verb = 'state.dump' }
    if (-not (Wait-Until { (Log-Count 'STATE_DUMP') -gt $before } 10 200)) { throw 'state.dump never logged' }
    $line = (Log-Lines 'STATE_DUMP')[-1]
    return ($line.Substring($line.IndexOf('STATE_DUMP') + 10) | ConvertFrom-Json)
}
function N2D { param([string]$N) return ([Guid]::Parse($N)).ToString('D') }
function Pane-Of { param([string]$SessionD)
    $s = (State-Dump).sessions | Where-Object { $_.id -eq $SessionD } | Select-Object -First 1
    if ($s) { return $s.panes[0] } else { return $null }
}
function Pane-State { param([string]$SessionD) $p = Pane-Of $SessionD; if ($p) { return $p.agentState } else { return '' } }
function Pty-Tail { param([string]$PaneD)
    $n = ([Guid]::Parse($PaneD)).ToString('N')
    $before = Log-Count "PTY_TAIL pane=$n"
    Send-Json @{ verb = 'pty.tail'; paneId = $PaneD }
    if (-not (Wait-Until { (Log-Count "PTY_TAIL pane=$n") -gt $before } 8 200)) { return '' }
    $line = (Log-Lines "PTY_TAIL pane=$n")[-1]
    $b64 = $line.Substring($line.IndexOf("pane=$n") + 5 + 32).Trim()
    $txt = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64))
    return ($txt -replace "`e\[[0-9;?]*[ -/]*[@-~]", '' -replace "`e\][^`a]*`a", '')
}
function Transcript-Path { param([string]$Sid)
    if (-not $Sid) { return $null }
    $root = Join-Path $HOME '.claude\projects'
    foreach ($d in Get-ChildItem $root -Directory -EA SilentlyContinue) {
        $p = Join-Path $d.FullName "$Sid.jsonl"
        if (Test-Path $p) { return $p }
    }
    return $null
}
# The transcript as rows: prompts (what went in), assistant prose, tool calls.
function Transcript { param([string]$Sid)
    $path = Transcript-Path $Sid
    $rows = @()
    if (-not $path) { return $rows }
    foreach ($l in [IO.File]::ReadAllLines($path)) {
        try { $j = $l | ConvertFrom-Json -Depth 30 } catch { continue }
        if (-not $j.message) { continue }
        $c = $j.message.content
        if ($j.type -eq 'user') {
            if ($c -is [string]) { $rows += [pscustomobject]@{ kind = 'prompt'; text = $c } }
            else { foreach ($b in @($c)) { if ($b.type -eq 'text') { $rows += [pscustomobject]@{ kind = 'prompt'; text = $b.text } }
                                          elseif ($b.type -eq 'tool_result') { $rows += [pscustomobject]@{ kind = 'result'; text = ($b.content | ConvertTo-Json -Compress -Depth 8) } } } }
        } elseif ($j.type -eq 'assistant') {
            foreach ($b in @($c)) {
                if ($b.type -eq 'text') { $rows += [pscustomobject]@{ kind = 'say'; text = $b.text } }
                elseif ($b.type -eq 'tool_use') { $rows += [pscustomobject]@{ kind = 'tool'; text = "$($b.name) $($b.input | ConvertTo-Json -Compress -Depth 8)" } }
            }
        }
    }
    return $rows
}
# Tokens by model across transcripts, and an estimate in USD (list prices).
function Cost-Of { param([string[]]$Sids)
    $price = @{ haiku = @(1.0, 5.0, 1.25, 2.0, 0.10); sonnet = @(3.0, 15.0, 3.75, 6.0, 0.30); opus = @(5.0, 25.0, 6.25, 10.0, 0.50) }
    $usd = 0.0; $seen = @{}
    foreach ($sid in $Sids | Select-Object -Unique) {
        $path = Transcript-Path $sid; if (-not $path) { continue }
        foreach ($l in [IO.File]::ReadAllLines($path)) {
            if ($l -notmatch '"usage"') { continue }
            try { $j = $l | ConvertFrom-Json -Depth 30 } catch { continue }
            $u = $j.message.usage; if (-not $u) { continue }
            $mid = [string]$j.message.id; if ($mid -and $seen.ContainsKey($mid)) { continue }; if ($mid) { $seen[$mid] = 1 }
            $m = [string]$j.message.model
            $p = if ($m -match 'opus') { $price.opus } elseif ($m -match 'sonnet') { $price.sonnet } else { $price.haiku }
            $cw5 = [double]($u.cache_creation.ephemeral_5m_input_tokens); $cw1 = [double]($u.cache_creation.ephemeral_1h_input_tokens)
            if (($cw5 + $cw1) -eq 0) { $cw5 = [double]$u.cache_creation_input_tokens }
            $usd += ([double]$u.input_tokens * $p[0] + [double]$u.output_tokens * $p[1] + $cw5 * $p[2] + $cw1 * $p[3] + [double]$u.cache_read_input_tokens * $p[4]) / 1e6
        }
    }
    return $usd
}

$fails = @()
function Check { param([string]$Name, [bool]$Ok, [string]$Detail = '')
    if ($Ok) { Write-Host "  [+] $Name $Detail" -ForegroundColor Green }
    else { Write-Host "  [-] $Name $Detail" -ForegroundColor Red; $script:fails += $Name }
}
function Note { param([string]$Text) Write-Host "      $Text" -ForegroundColor DarkGray }

# Wait for a thread to finish the turn a line started: its line submitted,
# then its pane back to done/idle and the transcript showing `Pattern` in prose.
function Wait-Answer { param([string]$Sid, [string]$Pattern, [int]$TimeoutSec = 150)
    return (Wait-Until {
        @(Transcript $Sid | Where-Object { $_.kind -eq 'say' -and $_.text -match $Pattern }).Count -ge 1
    } $TimeoutSec 1500)
}
function Watch-Permission { param([string]$SessionD)
    if ((Pane-State $SessionD) -eq 'permission') { $script:sawPermission = $true }
}

# ---- a long message: distinctive first and last sentences --------------------
function Long-Text { param([string]$First, [string]$Last)
    $filler = 'This middle part is filler that exists only to make the message long enough that Perch will not type it into the terminal and will save it to a file instead, so ignore its content.'
    $body = "The opening code word is $First and it appears in this first sentence only. "
    while ($body.Length -lt 1200) { $body += $filler + ' ' }
    $body += "Now reply with one line, exactly in the form FIRST=<opening code word> LAST=<closing code word>. The closing code word is $Last and it appears in this last sentence only."
    return $body
}

$proc = $null
$exit = 0
$sawPermission = $false
$sids = @()
try {
    # ---- setup ----------------------------------------------------------------
    New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'perch'), $RepoDir | Out-Null
    Write-Host "Snapshotting the build to $AppDir"
    Copy-Item -Recurse -Path $BinDir -Destination $AppDir
    $ExePath = Join-Path $AppDir 'Perch.exe'

    Set-Content -Path (Join-Path $RepoDir 'README.md') -Encoding utf8 -Value "# pchat-repo`n`nA throwaway repository for the project chat check."
    Push-Location $RepoDir
    try {
        & git init --quiet
        & git -c core.autocrlf=false add -A
        & git -c user.email=t@t -c user.name=t commit -qm init | Out-Null
    } finally { Pop-Location }

    # The launched app's environment: isolated, cheap model, no leaked markers.
    foreach ($n in 'CLAUDECODE', 'CLAUDE_CODE_CHILD_SESSION', 'CLAUDE_CODE_ENTRYPOINT', 'CLAUDE_CODE_MESSAGING_SOCKET',
                   'CLAUDE_CODE_MESSAGING_TOKEN', 'CLAUDE_CODE_SESSION_ATTENDED', 'CLAUDE_CODE_SESSION_ID', 'CLAUDE_CODE_EXECPATH',
                   'CLAUDE_CODE_BRIDGE_SESSION_ID', 'CLAUDE_PID', 'CLAUDE_EFFORT', 'NO_COLOR', 'PERCH_PIPE', 'PERCH_PANE_ID', 'PERCH_RUN') {
        Remove-Item "Env:\$n" -EA SilentlyContinue
    }
    $env:PERCH_ENABLE_TEST_IPC = '1'
    $env:PERCH_DATA_DIR        = $DataDir
    $env:ANTHROPIC_MODEL       = $Model
    $env:CLAUDE_CODE_SUBAGENT_MODEL = $Model
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$CdpPort"

    Write-Host "Launching isolated Perch (data: $DataDir, model: $Model, CDP: $CdpPort)"
    $proc = Start-Process -PassThru -FilePath $ExePath -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($proc.HasExited) { throw "Perch exited early (code $($proc.ExitCode))" }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($proc.MainWindowHandle -eq [IntPtr]::Zero) { throw "main window never appeared" }
    [PChat.WinPos]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, -3400, -3400, 1400, 900, 0x14) | Out-Null
    Write-Host "  Perch PID $($proc.Id)"
    if (-not (Wait-Until { (Log-Count 'Pane.spawn') -ge 1 } 25)) { throw "initial pane never spawned" }
    Start-Sleep -Milliseconds 600

    Send-Json @{ verb = 'project.add'; path = $RepoDir }
    $projectsJson = Join-Path $DataDir 'perch\projects.json'
    if (-not (Wait-Until { (Test-Path $projectsJson) -and ((Get-Content $projectsJson -Raw) -match 'pchat-repo') } 10)) { throw "project.add did nothing" }
    $projectId = [string]((Get-Content $projectsJson -Raw | ConvertFrom-Json).projects | Where-Object { $_.path -like '*pchat-repo*' } | Select-Object -First 1).id

    # ---- 1. a project chat starts a thread ------------------------------------
    Write-Host "`n[1] a project chat starts a thread"
    Send-Json @{ verb = 'projectchat.new'; id = $projectId; name = 'Verify chat' }
    if (-not (Wait-Until { (Log-Count 'ProjectChat.new') -ge 1 } 10)) { throw "projectchat.new did nothing" }
    $leadN = ((Log-Lines 'ProjectChat.new')[-1] -replace '.*session=([0-9a-f]{32}).*', '$1')
    $leadD = N2D $leadN
    $chatPane = Pane-Of $leadD
    $chatPaneD = $chatPane.id
    $sids += $chatPane.claudeSessionId
    $leadDir = Join-Path $DataDir "perch\threads\$leadN"
    Note "chat session $leadD, chat pane $chatPaneD"

    $brief = 'You are a probe thread in an automated test of Perch. Do exactly what each message you get says and nothing more. Reply in one short line. Never message the coordinator. Make no commits and edit no files.'
    Send-Json @{ verb = 'chat.send'; paneId = $chatPaneD; text = @"
This is an automated test of Perch's threads. For this whole conversation: do exactly what each message says and nothing more; never send a thread anything unless a message from me tells you to; when a thread reports back, reply with the single word NOTED and end your turn.
Now start one thread by running exactly this command, then end your turn:
perch thread new "Probe" --brief "$brief"
"@ }
    $started = Wait-Until { (Log-Count 'Thread.new') -ge 1 } 180 1500
    if (-not $started) {
        Note "the coordinator did not start a thread; starting it as a suggestion instead (the same StartThreadAsync)"
        $sug = @(@{ Id = 'probe00001'; Title = 'Probe'; Brief = $brief; ThreadId = $null; Dismissed = $false })
        New-Item -ItemType Directory -Force -Path $leadDir | Out-Null
        ConvertTo-Json -InputObject $sug -Depth 4 | Set-Content -Encoding utf8 (Join-Path $leadDir 'suggestions.json')
        Send-Json @{ verb = 'suggestion.start'; sessionId = $leadD; id = 'probe00001' }
        $started = Wait-Until { (Log-Count 'Thread.new') -ge 1 } 90
    }
    Check "the coordinator started a thread (perch thread new)" $started
    if (-not $started) { throw "no thread; nothing below can be tested" }
    $threadN = ((Log-Lines 'Thread.new')[-1] -replace '.*session=([0-9a-f]{32}).*', '$1')
    $threadD = N2D $threadN
    $tp = Pane-Of $threadD
    $threadPaneD = $tp.id; $threadPaneN = ([Guid]::Parse($threadPaneD)).ToString('N')
    $threadSid = $tp.claudeSessionId
    $sids += $threadSid
    Note "thread session $threadD, pane $threadPaneD, claude session $threadSid"

    $up = Wait-Until { (Log-Count "pane=$threadPaneN type=session") -ge 1 } 45
    if (-not $up) {
        $tail = Pty-Tail $threadPaneD
        if ($tail -match 'trust this folder') {
            Note "trust prompt on screen and Perch did not answer it - answering Down+Enter"
            Send-Json @{ verb = 'pty.send'; paneId = $threadPaneD; text = "$([char]27)[B`r" }
        }
        $up = Wait-Until { (Log-Count "pane=$threadPaneN type=session") -ge 1 } 90
    }
    Check "the thread's Claude came up (session hook)" $up
    if (-not $up) { throw "the thread's Claude never came up" }
    $kicked = Wait-Until { (Pane-State $threadD) -in @('done', 'idle') -and @(Transcript $threadSid | Where-Object kind -eq 'say').Count -ge 1 } 180 2000
    Check "the thread finished its first turn (kickoff)" $kicked
    Check "the chat shows the thread card" ((Test-Path "$leadDir\chat.jsonl") -and ((Get-Content "$leadDir\chat.jsonl" -Raw) -match '"thread:'))
    Start-Sleep -Seconds 3

    # ---- 2. a short steer -------------------------------------------------------
    Write-Host "`n[2] a short steer is submitted and answered"
    $subBefore = Log-Count "Delivery.submitted: session=$threadN"
    Send-Json @{ verb = 'thread.send'; id = $threadD; text = 'Reply with exactly this word and nothing else: STEERONE' }
    Check "the line was submitted (prompt-submit echo)" (Wait-Until { (Log-Count "Delivery.submitted: session=$threadN") -gt $subBefore } 60)
    # The submit hook fires before Claude writes the prompt row to disk.
    [void](Wait-Until { @(Transcript $threadSid | Where-Object { $_.kind -eq 'prompt' -and $_.text -match 'STEERONE' }).Count -ge 1 } 20 1000)
    $pr = @(Transcript $threadSid | Where-Object { $_.kind -eq 'prompt' -and $_.text -match 'STEERONE' })
    Check "the thread's transcript has the tagged prompt" ($pr.Count -ge 1 -and $pr[-1].text -match '^\s*(<pasted_content[^>]*>\s*)?\[Perch #\d+\] Reply with exactly') ("- " + ($(if ($pr.Count) { $pr[-1].text.Substring(0, [Math]::Min(80, $pr[-1].text.Length)) } else { '(none)' })))
    Check "the thread answered STEERONE" (Wait-Answer $threadSid 'STEERONE' 120)
    [void](Wait-Until { (Pane-State $threadD) -in @('done', 'idle') } 60)
    Start-Sleep -Seconds 3

    # ---- 2b. the board: a thread and the coordinator put cards on it ------------
    # `perch thread board` from both sides, with no permission prompt (it rides
    # the `perch thread` allow rule), landing in the chat's board.json.
    Write-Host "`n[2b] the board: a thread and the coordinator put cards on it"
    $boardJson = Join-Path $leadDir 'board.json'
    $threadNo = [int]((Get-Content (Join-Path $DataDir 'perch\sessions.json') -Raw | ConvertFrom-Json).Sessions | Where-Object { $_.Id -eq $threadD }).ThreadNumber
    if (-not $threadNo) { $threadNo = 1 }   # the first thread of a fresh chat
    Send-Json @{ verb = 'thread.send'; id = $threadD; text = 'Run exactly this command with Bash, then reply with the single word BOARDTWO: perch thread board set GATE-2 --title "Gate thread card" --verdict manual --question "Is this a test?"' }
    $card2 = Wait-Until { Watch-Permission $threadD; (Test-Path $boardJson) -and ((Get-Content $boardJson -Raw) -match 'GATE-2') } 150 1500
    Check "a thread put its card on the board" $card2
    $b = if (Test-Path $boardJson) { Get-Content $boardJson -Raw | ConvertFrom-Json } else { $null }
    $g2 = @($b.Items | Where-Object { $_.Key -eq 'GATE-2' })[0]
    Check "the card says what the thread set, and which thread it is" ($g2 -and $g2.Verdict -eq 'manual' -and $g2.Question -eq 'Is this a test?' -and [int]$g2.Thread -eq $threadNo) "- thread $($g2.Thread) (expected $threadNo)"
    Check "the thread answered BOARDTWO" (Wait-Answer $threadSid 'BOARDTWO' 120)
    Check "no permission prompt for the board command" (-not $sawPermission)
    Send-Json @{ verb = 'chat.send'; paneId = $chatPaneD; text = 'Run exactly this command, then reply with the single word NOTED: perch thread board set GATE-1 --title "Gate card" --verdict verified --status Done' }
    $card1 = Wait-Until { (Test-Path $boardJson) -and ((Get-Content $boardJson -Raw) -match 'GATE-1') } 180 1500
    Check "the coordinator put a card on the board" $card1
    [void](Wait-Until { (Pane-State $threadD) -in @('done', 'idle') } 60)
    Start-Sleep -Seconds 3

    # ---- 3. long messages go whole, by file ------------------------------------
    Write-Host "`n[3] long messages (user and coordinator) reach the thread whole"
    $filesBefore = @(Get-ChildItem $leadDir -Filter 'message-*.md' -EA SilentlyContinue).Count
    $long = Long-Text 'ALPHAKIWI' 'OMEGAPLUM'
    Note "user message: $($long.Length) chars"
    $subBefore = Log-Count "Delivery.submitted: session=$threadN"
    $sawPermission = $false
    Send-Json @{ verb = 'thread.send'; id = $threadD; text = $long }
    Check "user: it was saved to a file, not typed" (Wait-Until { @(Get-ChildItem $leadDir -Filter 'message-*.md' -EA SilentlyContinue).Count -gt $filesBefore } 10)
    $f = Get-ChildItem $leadDir -Filter 'message-*.md' | Sort-Object LastWriteTime | Select-Object -Last 1
    Check "user: the file holds all of it" ($f -and ((Get-Content $f.FullName -Raw) -match 'OMEGAPLUM') -and ((Get-Content $f.FullName -Raw).Trim() -eq $long.Trim()))
    Check "user: the pointer line was submitted" (Wait-Until { (Log-Count "Delivery.submitted: session=$threadN") -gt $subBefore } 60)
    $ok = Wait-Until { Watch-Permission $threadD; @(Transcript $threadSid | Where-Object { $_.kind -eq 'say' -and $_.text -match 'OMEGAPLUM' }).Count -ge 1 } 150 2000
    Check "user: the thread read the file and quoted the LAST sentence's word" $ok
    Check "user: it read the file without a permission prompt" (-not $sawPermission -and (Log-Count "Thread.ask: session=$threadN") -eq 0)
    Check "user: its Read targeted the message file" (@(Transcript $threadSid | Where-Object { $_.kind -eq 'tool' -and $_.text -match [regex]::Escape($f.Name) }).Count -ge 1)
    [void](Wait-Until { (Pane-State $threadD) -in @('done', 'idle') } 60)
    Start-Sleep -Seconds 3

    # From the coordinator: it runs `perch thread send 1 '<long>'` itself.
    $long2 = Long-Text 'BRAVOLIME' 'ZULUPEAR'
    Note "coordinator message: $($long2.Length) chars"
    $filesBefore = @(Get-ChildItem $leadDir -Filter 'message-*.md' -EA SilentlyContinue).Count
    $subBefore = Log-Count "Delivery.submitted: session=$threadN"
    $sawPermission = $false
    Send-Json @{ verb = 'chat.send'; paneId = $chatPaneD; text = "Run exactly this one command, copying the text between the single quotes character for character (it is long on purpose), then end your turn:`nperch thread send 1 '$long2'" }
    Check "coordinator: its message was saved to a file" (Wait-Until { @(Get-ChildItem $leadDir -Filter 'message-*.md' -EA SilentlyContinue).Count -gt $filesBefore } 150 1500)
    $f2 = Get-ChildItem $leadDir -Filter 'message-*.md' | Sort-Object LastWriteTime | Select-Object -Last 1
    Check "coordinator: the file holds all of it (first and last sentences)" ($f2 -and ((Get-Content $f2.FullName -Raw) -match 'BRAVOLIME') -and ((Get-Content $f2.FullName -Raw) -match 'ZULUPEAR'))
    Check "coordinator: the pointer line was submitted" (Wait-Until { (Log-Count "Delivery.submitted: session=$threadN") -gt $subBefore } 90)
    $ok = Wait-Until { Watch-Permission $threadD; @(Transcript $threadSid | Where-Object { $_.kind -eq 'say' -and $_.text -match 'ZULUPEAR' }).Count -ge 1 } 150 2000
    Check "coordinator: the thread quoted the LAST sentence's word" $ok
    Check "coordinator: no permission prompt" (-not $sawPermission -and (Log-Count "Thread.ask: session=$threadN") -eq 0)
    [void](Wait-Until { (Pane-State $threadD) -in @('done', 'idle') } 60)
    Start-Sleep -Seconds 4

    # ---- 4. Stop mid-turn -------------------------------------------------------
    Write-Host "`n[4] Stop mid-turn: state leaves working, no rewind menu, queued and next lines go in"
    $subBefore = Log-Count "Delivery.submitted: session=$threadN"
    # Not `sleep`: user-level hooks may block a bare sleep (the owner's do), and the thread then backgrounds it.
    Send-Json @{ verb = 'thread.send'; id = $threadD; text = 'Run this exact command with your Bash tool in the foreground (not in the background), then reply with the word SLOWDONE: ping -n 90 127.0.0.1' }
    Check "the slow task went in" (Wait-Until { (Log-Count "Delivery.submitted: session=$threadN") -gt $subBefore } 60)
    $running = Wait-Until { @(Transcript $threadSid | Where-Object { $_.kind -eq 'tool' -and $_.text -match 'ping -n 90' }).Count -ge 1 } 60 1000
    Check "the thread is running the 90-second ping" $running
    Start-Sleep -Seconds 3
    Check "the pane reads working" ((Pane-State $threadD) -eq 'working')
    $queuedBefore = Log-Count "Delivery.queue: session=$threadN"
    Send-Json @{ verb = 'thread.send'; id = $threadD; text = 'Reply with exactly this word and nothing else: QUEUEDONE' }
    Start-Sleep -Seconds 3
    $subMid = Log-Count "Delivery.submitted: session=$threadN"
    Check "a line sent mid-turn is queued, not typed" ((Log-Count "Delivery.queue: session=$threadN") -gt $queuedBefore -and (Log-Count "Delivery.submitted: session=$threadN") -eq ($subBefore + 1))

    $stopsBefore = Log-Count "Thread.stop: session=$threadN pane="
    $tailBefore = Pty-Tail $threadPaneD
    $t0 = Get-Date
    Send-Json @{ verb = 'thread.stop'; id = $threadD }
    $left = Wait-Until { (Pane-State $threadD) -ne 'working' } 5 250
    $ms = [int]((Get-Date) - $t0).TotalMilliseconds
    Check "Stop: the state left working promptly" $left "- ${ms}ms"
    # The double click. (Only a FAST one here: the queued line goes in ~1s
    # after the Stop, so a click two seconds later rightly stops THAT turn.
    # A Stop on an idle prompt is checked below, once the thread is idle.)
    Start-Sleep -Milliseconds 300
    Send-Json @{ verb = 'thread.stop'; id = $threadD }
    Start-Sleep -Milliseconds 700
    Check "Stop: one Escape for a double click" ((Log-Count "Thread.stop: session=$threadN pane=") -eq ($stopsBefore + 1)) "- $((Log-Count "Thread.stop: session=$threadN pane=") - $stopsBefore)"
    Check "the queued line went in after the Stop" (Wait-Until { (Log-Count "Delivery.submitted: session=$threadN") -gt $subMid } 60)
    Check "the queued line was answered" (Wait-Answer $threadSid 'QUEUEDONE' 120)
    $tailAfter = Pty-Tail $threadPaneD
    $rewind = ([regex]::Matches($tailAfter, 'Rewind|Restore the code|Restore conversation')).Count -gt ([regex]::Matches($tailBefore, 'Rewind|Restore the code|Restore conversation')).Count
    Check "no rewind menu on screen" (-not $rewind)
    Check "the slow command never finished (the Stop interrupted it)" (@(Transcript $threadSid | Where-Object { $_.kind -eq 'say' -and $_.text -match 'SLOWDONE' }).Count -eq 0)
    [void](Wait-Until { (Pane-State $threadD) -in @('done', 'idle') } 60)
    Start-Sleep -Seconds 3
    $subBefore = Log-Count "Delivery.submitted: session=$threadN"
    Send-Json @{ verb = 'thread.send'; id = $threadD; text = 'Reply with exactly this word and nothing else: AFTERSTOP' }
    Check "the next line went in" (Wait-Until { (Log-Count "Delivery.submitted: session=$threadN") -gt $subBefore } 60)
    Check "and was answered" (Wait-Answer $threadSid 'AFTERSTOP' 120)
    [void](Wait-Until { (Pane-State $threadD) -in @('done', 'idle') } 60)
    Start-Sleep -Seconds 3

    # Stop on an idle prompt: two Escapes there are Claude Code's rewind menu.
    $stopsIdle = Log-Count "Thread.stop: session=$threadN pane="
    $tailBefore = Pty-Tail $threadPaneD
    Send-Json @{ verb = 'thread.stop'; id = $threadD }
    Start-Sleep -Seconds 2
    Send-Json @{ verb = 'thread.stop'; id = $threadD }
    Start-Sleep -Seconds 2
    $tailAfter = Pty-Tail $threadPaneD
    Check "Stop on an idle thread sends no Escape" ((Log-Count "Thread.stop: session=$threadN pane=") -eq $stopsIdle)
    Check "and no rewind menu appears" (([regex]::Matches($tailAfter, 'Rewind|Restore the code|Restore conversation')).Count -le ([regex]::Matches($tailBefore, 'Rewind|Restore the code|Restore conversation')).Count)

    # ---- 5. a question, answered from the Overview -------------------------------
    Write-Host "`n[5] a thread's question: the reply dismisses it and lands as a clean prompt"
    $subBefore = Log-Count "Delivery.submitted: session=$threadN"
    Send-Json @{ verb = 'thread.send'; id = $threadD; text = 'Use the AskUserQuestion tool to ask me one question, Which fruit?, with the two options Apple and Banana. Do nothing else before I answer.' }
    Check "the ask went in" (Wait-Until { (Log-Count "Delivery.submitted: session=$threadN") -gt $subBefore } 60)
    $asked = Wait-Until { @(Transcript $threadSid | Where-Object { $_.kind -eq 'tool' -and $_.text -match '^AskUserQuestion' }).Count -ge 1 } 90 1000
    Check "the thread called AskUserQuestion" $asked
    $waiting = Wait-Until { (Pane-State $threadD) -eq 'waiting' } 20 700
    $st = Pane-State $threadD
    Check "Perch reads the thread as waiting on a question" $waiting "- state=$st"
    $screen = Pty-Tail $threadPaneD
    Note ("dialog on screen: " + [bool]($screen -match 'Which fruit'))
    $dismissBefore = Log-Count "Thread.send: session=$threadN dismissed"
    $subBefore = Log-Count "Delivery.submitted: session=$threadN"
    Send-Json @{ verb = 'thread.send'; id = $threadD; text = 'My answer is Banana. Reply with exactly this and nothing else: FRUIT=BANANA' }
    $dismissed = Wait-Until { (Log-Count "Thread.send: session=$threadN dismissed") -gt $dismissBefore } 5
    Check "the reply dismissed the question (Escape)" $dismissed
    if (-not $dismissed) {
        # The reply is queued behind a thread Perch thinks is busy. Stop
        # (Escape, then the queue is pumped) is what a user would reach for;
        # it also lets the rest of the run go on.
        Note "the reply is stuck in the queue; pressing Stop to dismiss the dialog"
        Send-Json @{ verb = 'thread.stop'; id = $threadD }
    }
    Check "the reply was submitted" (Wait-Until { (Log-Count "Delivery.submitted: session=$threadN") -gt $subBefore } 60)
    [void](Wait-Until { @(Transcript $threadSid | Where-Object { $_.kind -eq 'prompt' -and $_.text -match 'FRUIT=BANANA' }).Count -ge 1 } 20 1000)
    $pr = @(Transcript $threadSid | Where-Object { $_.kind -eq 'prompt' -and $_.text -match 'FRUIT=BANANA' })
    Check "it landed as a clean prompt (tag first, nothing before it)" ($pr.Count -ge 1 -and $pr[-1].text -match '^\s*\[Perch #\d+\] My answer is Banana') ("- " + ($(if ($pr.Count) { $pr[-1].text.Substring(0, [Math]::Min(80, $pr[-1].text.Length)) } else { '(none)' })))
    Check "and was answered" (Wait-Answer $threadSid 'FRUIT=BANANA' 120)
    [void](Wait-Until { (Pane-State $threadD) -in @('done', 'idle') } 60)

    # Stop right after a line went in, before the thread has answered: Claude
    # Code puts an interrupted prompt that has no reply yet BACK in its input
    # box. The next line typed must not be glued onto it (seen in a run:
    # "[Perch #5] …QUEUEDONE[Perch #6] …AFTERSTOP" submitted as one prompt,
    # #6 never confirmed, held, and on its way to a false give-up).
    Write-Host "`n[4b, run last: a stuck line would hold up everything after it] Stop just after a line went in, then send the next line"
    $subBefore = Log-Count "Delivery.submitted: session=$threadN"
    Send-Json @{ verb = 'thread.send'; id = $threadD; text = 'Count from 1 to 40, one number per line, then reply with the word EARLYDONE.' }
    [void](Wait-Until { (Log-Count "Delivery.submitted: session=$threadN") -gt $subBefore } 60 150)
    Send-Json @{ verb = 'thread.stop'; id = $threadD }
    [void](Wait-Until { (Pane-State $threadD) -ne 'working' } 5 250)
    Start-Sleep -Seconds 3
    $subBefore = Log-Count "Delivery.submitted: session=$threadN"
    Send-Json @{ verb = 'thread.send'; id = $threadD; text = 'Reply with exactly this word and nothing else: CLEANNEXT' }
    Check "the next line was confirmed" (Wait-Until { (Log-Count "Delivery.submitted: session=$threadN") -gt $subBefore } 45)
    [void](Wait-Until { @(Transcript $threadSid | Where-Object { $_.kind -eq 'prompt' -and $_.text -match 'CLEANNEXT' }).Count -ge 1 } 30 1000)
    $pr = @(Transcript $threadSid | Where-Object { $_.kind -eq 'prompt' -and $_.text -match 'CLEANNEXT' })
    Check "it went in on its own, not glued to the interrupted line" ($pr.Count -ge 1 -and $pr[-1].text -match '^\s*\[Perch #\d+\] Reply with exactly' -and $pr[-1].text -notmatch 'EARLYDONE') ("- " + ($(if ($pr.Count) { $pr[-1].text.Substring(0, [Math]::Min(120, $pr[-1].text.Length)) } else { '(none)' })))
    [void](Wait-Answer $threadSid 'CLEANNEXT' 90)
    [void](Wait-Until { (Pane-State $threadD) -in @('done', 'idle') } 60)
    Start-Sleep -Seconds 3

    # ---- 7. resolve sleeps the thread; a message wakes it -----------------------
    # A resolved thread used to keep its terminal and Claude running until the
    # idle timer (hours). Now it sleeps at once, and a message to it wakes it
    # where it left off (--resume) and is answered.
    Write-Host "`n[7] resolving a thread puts it to sleep; a message wakes it and is answered"
    Send-Json @{ verb = 'thread.resolve'; id = $threadD; resolved = $true }
    $slept = Wait-Until { ((State-Dump).sessions | Where-Object { $_.id -eq $threadD } | Select-Object -First 1).dormant -eq $true } 15 700
    Check "the resolved thread went to sleep" $slept
    Start-Sleep -Seconds 4   # let its polite /exit finish
    $subBefore = Log-Count "Delivery.submitted: session=$threadN"
    Send-Json @{ verb = 'thread.send'; id = $threadD; text = 'Reply with exactly this word and nothing else: WOKEUP' }
    $woke = Wait-Until { ((State-Dump).sessions | Where-Object { $_.id -eq $threadD } | Select-Object -First 1).dormant -eq $false } 20 700
    Check "a message woke it" $woke
    # Woken by its chat, not opened by the user: no "Resuming session" box.
    Check "it woke in the background (no Resuming session box)" (Wait-Until { (Log-Count "Session.wake.background: session=$threadN") -ge 1 } 10 500)
    Check "the message went in once its Claude was back" (Wait-Until { Watch-Permission $threadD; (Log-Count "Delivery.submitted: session=$threadN") -gt $subBefore } 150 1500)
    $sidNow = (Pane-Of $threadD).claudeSessionId
    if (-not $sidNow) { $sidNow = $threadSid }
    $answered = Wait-Until { @(Transcript $sidNow | Where-Object { $_.kind -eq 'say' -and $_.text -match 'WOKEUP' }).Count -ge 1 -or @(Transcript $threadSid | Where-Object { $_.kind -eq 'say' -and $_.text -match 'WOKEUP' }).Count -ge 1 } 120 2000
    Check "and was answered, in the same conversation" $answered ("- session " + $(if ($sidNow -eq $threadSid) { 'kept' } else { "resumed as $sidNow" }))
    [void](Wait-Until { (Pane-State $threadD) -in @('done', 'idle') } 60)
    Start-Sleep -Seconds 3

    # ---- 8. a thread grown long continues in a fresh session ---------------------
    # Past ThreadController.FreshAtTokens a thread's next message goes to a new
    # Claude session in the same pane, whose prompt carries the brief and where
    # it left off. `thread.fresh` marks it as if it had grown long.
    Write-Host "`n[8] a thread grown long takes its next message in a fresh session"
    $sidBefore = (Pane-Of $threadD).claudeSessionId
    $freshBefore = Log-Count "Thread.fresh: session=$threadN"
    $subBefore = Log-Count "Delivery.submitted: session=$threadN"
    Send-Json @{ verb = 'thread.fresh'; id = $threadD }
    Start-Sleep -Milliseconds 500
    Send-Json @{ verb = 'thread.send'; id = $threadD; text = 'Reply with exactly this word and nothing else: FRESHONE' }
    Check "the thread was started over in a fresh session" (Wait-Until { (Log-Count "Thread.fresh: session=$threadN") -gt $freshBefore } 20 700)
    Check "a new Claude came up with a new session id" (Wait-Until { $p = Pane-Of $threadD; $p.claudeSessionId -and $p.claudeSessionId -ne $sidBefore -and $p.agentType -eq 'claude' } 90 1500)
    Check "the message went in to the new one" (Wait-Until { Watch-Permission $threadD; (Log-Count "Delivery.submitted: session=$threadN") -gt $subBefore } 150 1500)
    $sidFresh = (Pane-Of $threadD).claudeSessionId
    $sids += $sidFresh
    Check "and was answered there" (Wait-Answer $sidFresh 'FRESHONE' 120)
    $promptFile = Get-ChildItem -Path (Join-Path $DataDir 'perch\threads') -Recurse -Filter 'thread-*.md' | Where-Object { $_.Name -notmatch 'brief' } | Select-Object -First 1
    Check "its prompt says where it left off" ($promptFile -and ((Get-Content $promptFile.FullName -Raw) -match '## Where you left off'))
    Check "the old thread was not reported as failed" ((Log-Count "Thread.failed: session=$threadN") -eq 0)
    [void](Wait-Until { (Pane-State $threadD) -in @('done', 'idle') } 60)
    Start-Sleep -Seconds 3

    # ---- 6. no give-ups ----------------------------------------------------------
    Write-Host "`n[6] no line was given up on"
    # Let the thread's queue drain (every queued line either submitted or
    # given up on) so a held line's fate is known before judging.
    $drained = Wait-Until { (Log-Count "Delivery.queue: session=$threadN") -le ((Log-Count "Delivery.submitted: session=$threadN") + (Log-Count "Delivery.gaveup: session=$threadN")) } 150 2000
    Note "thread queue drained: $drained"
    Check "no Delivery.gaveup in the log" ((Log-Count 'Delivery.gaveup') -eq 0) "- $(Log-Count 'Delivery.gaveup')"
    # (The "couldn't get a message into" toast is posted exactly when
    # Delivery.gaveup is logged; toasts themselves are not logged.)
    # The chat pane has no terminal: the idle watchdog must not read its
    # silence as "the turn ended" while the coordinator is still running.
    $chatPaneN = ([Guid]::Parse($chatPaneD)).ToString('N')
    Check "the idle watchdog never demoted the project chat mid-turn" ((Log-Count "IdleWatchdog: pane=$chatPaneN working->done") -eq 0) "- $(Log-Count "IdleWatchdog: pane=$chatPaneN working->done") times"
    Note ("Delivery.held (Enter pressed again, waited for a free moment): " + (Log-Count 'Delivery.held'))
    Note ("chat turns: " + (Log-Count 'Chat.turn: lead=') + ", errored turns: " + @(Log-Lines 'Chat.turn.end' | Where-Object { $_ -match 'result=False' }).Count)

    Write-Host ""
    if ($fails.Count -gt 0) {
        Write-Host "PROJECT CHAT CHECK FAILED: $($fails -join '; ')" -ForegroundColor Red
        Write-Host "log: $LogPath" -ForegroundColor Red
        $exit = 1
    } else {
        Write-Host "PROJECT CHAT CHECK PASSED." -ForegroundColor Green
    }
}
catch {
    Write-Host "ERROR: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    if (Test-Path $LogPath) { Write-Host "log: $LogPath" }
    $exit = 1
}
finally {
    try {
        if ($sids.Count -gt 0) {
            $chatSid = $null
            try { $chatSid = (Pane-Of $leadD).claudeSessionId } catch { }
            Write-Host ("Estimated model spend: `${0:N3} (transcripts: {1})" -f (Cost-Of (@($sids) + @($chatSid) | Where-Object { $_ })), (($sids | Where-Object { $_ }) -join ', '))
        }
    } catch { }
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -EA SilentlyContinue }
    foreach ($n in 'PERCH_ENABLE_TEST_IPC', 'PERCH_DATA_DIR', 'ANTHROPIC_MODEL', 'CLAUDE_CODE_SUBAGENT_MODEL', 'WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS') { Remove-Item "Env:\$n" -EA SilentlyContinue }
    if ($exit -eq 0 -and -not $Keep) {
        Start-Sleep -Seconds 2
        # Worktrees are real directories here (no seeded junctions in a
        # README-only repo), but unlink any junction first all the same.
        Get-ChildItem $DataDir -Recurse -Force -Attributes ReparsePoint -EA SilentlyContinue | ForEach-Object { try { $_.Delete() } catch { } }
        Remove-Item -LiteralPath $DataDir -Recurse -Force -EA SilentlyContinue
    } else { Write-Host "data dir kept: $DataDir" }
}
exit $exit
