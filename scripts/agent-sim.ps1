# Fake agent that exercises every perch CLI verb we built, mimicking what a
# Claude Code session would emit via PreToolUse / Notification / Stop hooks.
# Lives in perch/scripts/ but is meant to be run *inside* a perch pane —
# PERCH_PIPE points at the pane's host pipe, so each call routes back through
# the IPC server we built in phases 1-3.

if (-not $env:PERCH_PIPE) {
    Write-Host "agent-sim: not in a perch pane (PERCH_PIPE unset) — skipping IPC calls"
    return
}

Write-Host "agent-sim: starting (pipe=$env:PERCH_PIPE)"

perch status working 'starting up'
perch meta --branch main --port 3000 --port 5173

Start-Sleep -Milliseconds 800
perch notify 'analyzing code'

Start-Sleep -Milliseconds 800
perch status waiting 'awaiting permission'
perch notify --level warn 'tool approval needed'

Start-Sleep -Milliseconds 800
perch status working 'applying changes'

Start-Sleep -Milliseconds 800
perch status done
perch notify --level success 'complete'

Write-Host "agent-sim: done"
