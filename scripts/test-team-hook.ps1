<#
The team -> agent handoff, run as the real binary.

Two CLI-side pieces carry a bot's identity, and both run on the agent's
critical path with no app instance to catch a mistake:

  * `perch hooks claude prompt-submit` injects the team ROSTER as
    additionalContext on EVERY turn of EVERY Claude pane. Claude Code reads
    stdout as ONE JSON object, so a board hint and a roster must be joined into
    a single object - two objects is a parse error at best and raw context at
    worst - and a pane that is neither a board tab nor a bot must print
    NOTHING.
  * `perch wrap-claude` appends the bot's BRIEF as --append-system-prompt-file
    at launch. That file becomes the model's instructions, so a stale or
    tampered marker must not be able to point it at an arbitrary file: only a
    .md under a `.perch\team\` folder is honoured.

The wrapper also records the --name it ACTUALLY passed, because the host used
to record the name it intended and drop every peer note addressed to the real
one.

So the properties under test are:

  1. No markers -> stdout completely empty, exit 0.
  2. Roster only -> exactly one hookSpecificOutput object carrying the roster.
  3. Board + roster -> STILL exactly one object, carrying both.
  4. An oversized roster is cut with a visible marker, still one object.
  5. Every degenerate roster pointer (missing, outside .perch\team, not .md,
     empty, junk) -> silent, exit 0.
  6. The wrapper adds --append-system-prompt-file once for a brief under
     .perch\team, never for one outside it, and records the effective --name.

Needs only the built CLI - no app instance, no isolation dance.
#>
param(
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools"
)
$ErrorActionPreference = 'Stop'

$PerchExe = Join-Path $ToolsDir 'perch.exe'
if (-not (Test-Path $PerchExe)) { throw "perch.exe not found: $PerchExe (build first)" }

$Scratch  = Join-Path $env:TEMP ("perch-teamhooktest-{0}" -f $PID)
$PaneId   = ([guid]::NewGuid()).ToString('N')
$Board    = Join-Path $env:TEMP "perch-board-$PaneId.txt"
$Roster   = Join-Path $env:TEMP "perch-team-$PaneId.txt"
$Brief    = Join-Path $env:TEMP "perch-claude-brief-$PaneId.txt"
$NameFile = Join-Path $env:TEMP "perch-claude-name-$PaneId.txt"
$Launched = Join-Path $env:TEMP "perch-claude-launched-name-$PaneId.txt"
$Markers  = @($Board, $Roster, $Brief, $NameFile, $Launched)

$fails = @()
function Check { param([string]$Name, [bool]$Ok, [string]$Detail = '')
    if ($Ok) { Write-Host "  [+] $Name" -ForegroundColor Green }
    else {
        Write-Host "  [-] $Name $Detail" -ForegroundColor Red
        $script:fails += $Name
    }
}

# Run the hook exactly as Claude Code does: payload on stdin, event in argv.
# PERCH_PIPE is deliberately pointed at a pipe nobody is serving - the hook's
# IPC send must fail harmlessly and not affect stdout.
function Invoke-Hook {
    $payload = '{"prompt":"fix the login bug","session_id":"abc"}'
    $inFile  = Join-Path $Scratch 'in.json'
    $outFile = Join-Path $Scratch 'out.txt'
    $errFile = Join-Path $Scratch 'err.txt'
    Set-Content -Path $inFile -Value $payload -Encoding utf8 -NoNewline
    $p = Start-Process -FilePath $PerchExe `
        -ArgumentList 'hooks', 'claude', 'prompt-submit' `
        -RedirectStandardInput $inFile -RedirectStandardOutput $outFile `
        -RedirectStandardError $errFile -NoNewWindow -PassThru -Wait
    return [pscustomobject]@{
        Code   = $p.ExitCode
        Out    = (Get-Content $outFile -Raw -EA SilentlyContinue)
        Err    = (Get-Content $errFile -Raw -EA SilentlyContinue)
    }
}

# Parse stdout as ONE object. Two concatenated objects fail ConvertFrom-Json,
# which is exactly the property we want to pin.
function Parse-One { param([string]$Out)
    if ([string]::IsNullOrWhiteSpace($Out)) { return $null }
    try { return ($Out | ConvertFrom-Json) } catch { return $null }
}

function Set-Marker { param([string]$Path, [string]$Value)
    Set-Content -Path $Path -Value $Value -Encoding utf8 -NoNewline
}

# Run wrap-claude against a fake `claude.cmd` that just records its argv.
function Invoke-Wrapper { param([string[]]$ExtraArgs = @())
    $capture = Join-Path $Scratch 'argv.txt'
    Remove-Item $capture -Force -EA SilentlyContinue
    $outFile = Join-Path $Scratch 'wout.txt'
    $errFile = Join-Path $Scratch 'werr.txt'
    $argList = @('wrap-claude') + $ExtraArgs
    $p = Start-Process -FilePath $PerchExe -ArgumentList $argList `
        -RedirectStandardOutput $outFile -RedirectStandardError $errFile `
        -NoNewWindow -PassThru -Wait
    return [pscustomobject]@{
        Code = $p.ExitCode
        Argv = (Get-Content $capture -Raw -EA SilentlyContinue)
        Err  = (Get-Content $errFile -Raw -EA SilentlyContinue)
    }
}

$savedPath = $env:PATH
try {
    New-Item -ItemType Directory -Force -Path $Scratch | Out-Null
    $env:PERCH_PANE_ID = $PaneId
    $env:PERCH_PIPE    = "\\.\pipe\perch\$PaneId"
    $Markers | ForEach-Object { Remove-Item $_ -Force -EA SilentlyContinue }

    # A repo with a team folder, so the containment rule can be satisfied.
    $teamDir   = Join-Path $Scratch 'repo\.perch\team'
    $rosterMd  = Join-Path $teamDir 'roster.md'
    $systemMd  = Join-Path $teamDir 'bots\ada\system.md'
    New-Item -ItemType Directory -Force -Path (Split-Path $systemMd) | Out-Null
    $rosterText = "Your team on repo:`n- Ada (ada) - Frontend dev: owns src/web`n- Bo (bo) - Backend dev: owns src/Perch`nMessage a teammate with SendMessage(to: name)."
    Set-Content -Path $rosterMd -Value $rosterText -Encoding utf8 -NoNewline
    Set-Content -Path $systemMd -Value "You are Ada, the Frontend dev.`n" -Encoding utf8

    # --- 1. no markers: stdout must be completely silent ---------------------
    Write-Host "`n[1] a pane with no board and no team injects NOTHING"
    $r = Invoke-Hook
    Check "exit 0" ($r.Code -eq 0) "- got $($r.Code)"
    Check "stdout is empty" ([string]::IsNullOrWhiteSpace($r.Out)) "- printed: $($r.Out)"

    # --- 2. roster only: one object carrying the roster ----------------------
    Write-Host "`n[2] a bot pane gets one object carrying the roster"
    Set-Marker $Roster $rosterMd
    $r = Invoke-Hook
    Check "exit 0" ($r.Code -eq 0) "- got $($r.Code)"
    $json = Parse-One $r.Out
    Check "stdout parses as a single JSON object" ($null -ne $json) "- got: $($r.Out)"
    if ($json) {
        Check "it is a UserPromptSubmit hookSpecificOutput" `
            ($json.hookSpecificOutput.hookEventName -eq 'UserPromptSubmit')
        $ctx = [string]$json.hookSpecificOutput.additionalContext
        Check "it carries the roster text" ($ctx -match 'Ada \(ada\) - Frontend dev')
        Check "it carries the etiquette line" ($ctx -match 'SendMessage')
        Check "it does not mention a board" (-not ($ctx -match 'board\.md'))
    }

    # --- 3. board + roster: STILL one object, both inside --------------------
    Write-Host "`n[3] a bot pane with a board gets ONE object carrying both"
    $boardDir = Join-Path $Scratch 'boards\login-bug'
    New-Item -ItemType Directory -Force -Path $boardDir | Out-Null
    Set-Content -Path (Join-Path $boardDir 'board.md') -Value "# login bug`n" -Encoding utf8
    Set-Marker $Board $boardDir
    $r = Invoke-Hook
    Check "exit 0" ($r.Code -eq 0) "- got $($r.Code)"
    $json = Parse-One $r.Out
    Check "stdout parses as a single JSON object" ($null -ne $json) "- got: $($r.Out)"
    Check "exactly one hookSpecificOutput on stdout" `
        (([regex]::Matches($r.Out, 'hookSpecificOutput')).Count -eq 1)
    if ($json) {
        $ctx = [string]$json.hookSpecificOutput.additionalContext
        Check "it names the board's index file" ($ctx -match 'board\.md')
        Check "it carries the roster text" ($ctx -match 'Bo \(bo\) - Backend dev')
        Check "board comes first, roster after a blank line" ($ctx -match "board\.md[\s\S]*\n\n[\s\S]*Ada \(ada\)")
    }
    Remove-Item $Board -Force -EA SilentlyContinue

    # --- 4. oversized roster is cut, still one object ------------------------
    Write-Host "`n[4] an oversized roster is truncated with a visible marker"
    $bigRoster = Join-Path $teamDir 'roster-big.md'
    $line = "- Zed (zed) - Analyst: owns the numbers and the dashboards`n"
    Set-Content -Path $bigRoster -Value ($line * 200) -Encoding utf8 -NoNewline   # ~12 KB
    Set-Marker $Roster $bigRoster
    $r = Invoke-Hook
    $json = Parse-One $r.Out
    Check "stdout parses as a single JSON object" ($null -ne $json) "- got: $($r.Out)"
    if ($json) {
        $ctx = [string]$json.hookSpecificOutput.additionalContext
        Check "it ends with the truncation marker" ($ctx -match '\[roster truncated\]$')
        Check "it is capped near 6 KB" ($ctx.Length -le 6300) "- $($ctx.Length) chars"
    }

    # --- 5. degenerate roster pointers all stay silent and succeed -----------
    Write-Host "`n[5] every broken roster pointer exits 0 with empty stdout"
    $outside = Join-Path $Scratch 'elsewhere\roster.md'
    New-Item -ItemType Directory -Force -Path (Split-Path $outside) | Out-Null
    Set-Content -Path $outside -Value "- Mallory (mal) - Intruder" -Encoding utf8
    $notMd = Join-Path $teamDir 'roster.txt'
    Set-Content -Path $notMd -Value "- Ada" -Encoding utf8
    $cases = [ordered]@{
        'pointer at a file that does not exist'   = (Join-Path $teamDir 'nope.md')
        'pointer at a .md OUTSIDE .perch\team'     = $outside
        'pointer at a non-.md inside .perch\team'  = $notMd
        'pointer is empty'                         = ''
        'pointer is whitespace'                    = "   `n  "
        'pointer is junk'                          = 'not a path at all <>|"'
    }
    foreach ($name in $cases.Keys) {
        Set-Marker $Roster $cases[$name]
        $r = Invoke-Hook
        Check "$name -> exit 0, silent" `
            (($r.Code -eq 0) -and [string]::IsNullOrWhiteSpace($r.Out)) `
            "- code $($r.Code), out: $($r.Out)"
    }
    Remove-Item $Roster -Force -EA SilentlyContinue

    # --- 6. the wrapper: brief flag + effective name ---------------------------
    Write-Host "`n[6] wrap-claude appends the brief only from .perch\team and records the real --name"
    $fakeBin = Join-Path $Scratch 'fakebin'
    New-Item -ItemType Directory -Force -Path $fakeBin | Out-Null
    $capture = Join-Path $Scratch 'argv.txt'
    # A stand-in `claude` that appends its argv to a file and exits 0.
    Set-Content -Path (Join-Path $fakeBin 'claude.cmd') -Value "@echo %*>>`"$capture`"`r`n@exit /b 0" -Encoding ascii
    $env:PATH = "$fakeBin;$savedPath"

    # 6a. brief under .perch\team -> flag present exactly once, pointing at it
    Set-Marker $Brief $systemMd
    Set-Marker $NameFile 'ada'
    $w = Invoke-Wrapper @('--version')
    Check "wrapper exit 0" ($w.Code -eq 0) "- got $($w.Code); err: $($w.Err)"
    Check "fake claude ran" (-not [string]::IsNullOrWhiteSpace($w.Argv)) "- err: $($w.Err)"
    $argv = [string]$w.Argv
    Check "--append-system-prompt-file appears exactly once" `
        (([regex]::Matches($argv, '--append-system-prompt-file')).Count -eq 1) "- argv: $argv"
    Check "it points at the bot's system.md" ($argv -match [regex]::Escape($systemMd))
    Check "--name ada was passed" ($argv -match '--name ada')
    Check "the launched-name record says ada" `
        ((Test-Path $Launched) -and ((Get-Content $Launched -Raw).Trim() -eq 'ada')) `
        "- got: $(Get-Content $Launched -Raw -EA SilentlyContinue)"

    # 6b. a caller's own --name wins, and THAT is what gets recorded
    $w = Invoke-Wrapper @('--name', 'ada-2', '--version')
    $argv = [string]$w.Argv
    Check "caller --name is passed through once" `
        (([regex]::Matches($argv, '--name')).Count -eq 1) "- argv: $argv"
    Check "the launched-name record says ada-2" `
        ((Get-Content $Launched -Raw -EA SilentlyContinue).Trim() -eq 'ada-2') `
        "- got: $(Get-Content $Launched -Raw -EA SilentlyContinue)"

    # 6c. brief OUTSIDE .perch\team -> no flag
    $outsideMd = Join-Path $Scratch 'elsewhere\system.md'
    Set-Content -Path $outsideMd -Value "You are Mallory." -Encoding utf8
    Set-Marker $Brief $outsideMd
    $w = Invoke-Wrapper @('--version')
    $argv = [string]$w.Argv
    Check "a brief outside .perch\team is ignored" (-not ($argv -match '--append-system-prompt-file')) "- argv: $argv"

    # 6d. caller supplies its own system prompt -> ours must not stack
    Set-Marker $Brief $systemMd
    $w = Invoke-Wrapper @('--append-system-prompt', 'be terse', '--version')
    $argv = [string]$w.Argv
    Check "a caller's own system-prompt flag suppresses ours" `
        (-not ($argv -match '--append-system-prompt-file')) "- argv: $argv"

    # 6e. no name anywhere -> the record is removed, not left stale
    Remove-Item $NameFile -Force -EA SilentlyContinue
    $w = Invoke-Wrapper @('--version')
    Check "no name -> no launched-name record" (-not (Test-Path $Launched))

    if ($fails.Count -gt 0) {
        Write-Host "`nRESULT: FAIL -- $($fails.Count) check(s) failed:" -ForegroundColor Red
        $fails | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        $exit = 1
    } else {
        Write-Host "`nRESULT: PASS -- one object or silence; the brief only from .perch\team; the real name recorded" -ForegroundColor Green
        $exit = 0
    }
}
catch {
    Write-Host "`nRESULT: FAIL -- $($_.Exception.Message)" -ForegroundColor Red
    $exit = 1
}
finally {
    $env:PATH = $savedPath
    $Markers | ForEach-Object { Remove-Item $_ -Force -EA SilentlyContinue }
    Remove-Item (Join-Path $env:TEMP "perch-claude-hooks-$PaneId.json") -Force -EA SilentlyContinue
    Remove-Item $Scratch -Recurse -Force -EA SilentlyContinue
    Remove-Item Env:PERCH_PANE_ID, Env:PERCH_PIPE -EA SilentlyContinue
}
exit $exit
