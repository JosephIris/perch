<#
The board -> agent handoff hook, run as the real binary.

This is the riskiest code in the board feature and it deserves its own harness.
`perch hooks claude prompt-submit` runs inside EVERY Claude pane, synchronously,
on the agent's critical path, under a timeout, and its stderr goes to the PTY
rather than to errors.log. A throw, a hang, or a stray line on stdout costs the
user a turn - and stray stdout from this hook does not merely get ignored, it
becomes CONTEXT the model reads.

So the properties under test are not "it works" but:

  1. With a board, stdout is exactly one well-formed hookSpecificOutput object
     naming the board, and nothing else.
  2. With no board - the common case, since most panes have none - stdout is
     COMPLETELY EMPTY. Anything at all here would be injected into every turn
     of every Claude pane in the app.
  3. Every degenerate input (missing marker, marker pointing nowhere, marker
     pointing at a folder with no index, unreadable junk, no PERCH_PANE_ID)
     exits 0 with empty stdout rather than throwing.

Needs only the built CLI - no app instance, no isolation dance.
#>
param(
    [string]$ToolsDir = "$PSScriptRoot\..\src\Perch\bin\Debug\net8.0-windows\win10-x64\tools"
)
$ErrorActionPreference = 'Stop'

$PerchExe = Join-Path $ToolsDir 'perch.exe'
if (-not (Test-Path $PerchExe)) { throw "perch.exe not found: $PerchExe (build first)" }

$Scratch = Join-Path $env:TEMP ("perch-hooktest-{0}" -f $PID)
$PaneId  = ([guid]::NewGuid()).ToString('N')
$Marker  = Join-Path $env:TEMP "perch-board-$PaneId.txt"

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

try {
    New-Item -ItemType Directory -Force -Path $Scratch | Out-Null
    $env:PERCH_PANE_ID = $PaneId
    $env:PERCH_PIPE    = "\\.\pipe\perch\$PaneId"

    # --- 1. no board: stdout must be completely silent ----------------------
    Write-Host "`n[1] a pane with no board injects NOTHING"
    Remove-Item $Marker -Force -EA SilentlyContinue
    $r = Invoke-Hook
    Check "exit 0" ($r.Code -eq 0) "- got $($r.Code)"
    # This is the one that matters most: most panes have no board, and anything
    # printed here lands in every turn of every Claude pane in the app.
    Check "stdout is empty" ([string]::IsNullOrWhiteSpace($r.Out)) "- printed: $($r.Out)"

    # --- 2. a real board: exactly one well-formed object ---------------------
    Write-Host "`n[2] a pane with a board gets one well-formed context object"
    $boardDir = Join-Path $Scratch 'boards\login-bug'
    New-Item -ItemType Directory -Force -Path $boardDir | Out-Null
    Set-Content -Path (Join-Path $boardDir 'board.md') -Value "# login bug`n" -Encoding utf8
    Set-Content -Path $Marker -Value $boardDir -Encoding utf8 -NoNewline

    $r = Invoke-Hook
    Check "exit 0" ($r.Code -eq 0) "- got $($r.Code)"
    Check "stdout is not empty" (-not [string]::IsNullOrWhiteSpace($r.Out))
    $json = $null
    try { $json = $r.Out | ConvertFrom-Json } catch {}
    Check "stdout parses as a single JSON object" ($null -ne $json) "- got: $($r.Out)"
    if ($json) {
        Check "it is a UserPromptSubmit hookSpecificOutput" `
            ($json.hookSpecificOutput.hookEventName -eq 'UserPromptSubmit')
        $ctx = [string]$json.hookSpecificOutput.additionalContext
        Check "it names the board's index file" ($ctx -match 'board\.md')
        # The path, never the contents - so nothing can go stale and the
        # per-turn cost stays negligible.
        Check "it tells the agent it can re-read it" ($ctx -match 're-read')
        Check "it is short (one line of context, not a document)" ($ctx.Length -lt 600) `
            "- $($ctx.Length) chars"
        Check "it does NOT inline the board's contents" (-not ($ctx -match '# login bug'))
    }

    # --- 3. degenerate inputs all stay silent and succeed --------------------
    Write-Host "`n[3] every broken shape exits 0 with empty stdout"
    $cases = @{
        'marker points at a folder that does not exist' = 'C:\no\such\board\anywhere'
        'marker points at a folder with no board.md'    = $Scratch
        'marker is empty'                               = ''
        'marker is whitespace'                          = "   `n  "
        'marker is junk'                                = 'not a path at all <>|"'
    }
    foreach ($name in $cases.Keys) {
        Set-Content -Path $Marker -Value $cases[$name] -Encoding utf8 -NoNewline
        $r = Invoke-Hook
        Check "$name -> exit 0, silent" `
            (($r.Code -eq 0) -and [string]::IsNullOrWhiteSpace($r.Out)) `
            "- code $($r.Code), out: $($r.Out)"
    }

    # A pane with no PERCH_PANE_ID at all (an unrecognized shell, or WSL, where
    # Shell.cs deliberately skips the env injection).
    Remove-Item Env:PERCH_PANE_ID -EA SilentlyContinue
    $r = Invoke-Hook
    Check "no PERCH_PANE_ID -> exit 0, silent" `
        (($r.Code -eq 0) -and [string]::IsNullOrWhiteSpace($r.Out)) `
        "- code $($r.Code), out: $($r.Out)"

    if ($fails.Count -gt 0) {
        Write-Host "`nRESULT: FAIL -- $($fails.Count) check(s) failed:" -ForegroundColor Red
        $fails | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        $exit = 1
    } else {
        Write-Host "`nRESULT: PASS -- the hook is silent unless there is a board, and never throws" -ForegroundColor Green
        $exit = 0
    }
}
catch {
    Write-Host "`nRESULT: FAIL -- $($_.Exception.Message)" -ForegroundColor Red
    $exit = 1
}
finally {
    Remove-Item $Marker -Force -EA SilentlyContinue
    Remove-Item $Scratch -Recurse -Force -EA SilentlyContinue
    Remove-Item Env:PERCH_PANE_ID, Env:PERCH_PIPE -EA SilentlyContinue
}
exit $exit
