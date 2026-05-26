# Fake agent that exercises every cmux CLI verb we built, mimicking what a
# Claude Code session would emit via PreToolUse / Notification / Stop hooks.
# Lives in cmux-win/scripts/ but is meant to be run *inside* a cmux pane —
# CMUX_PIPE points at the pane's host pipe, so each call routes back through
# the IPC server we built in phases 1-3.

if (-not $env:CMUX_PIPE) {
    Write-Host "agent-sim: not in a cmux pane (CMUX_PIPE unset) — skipping IPC calls"
    return
}

Write-Host "agent-sim: starting (pipe=$env:CMUX_PIPE)"

cmux status working 'starting up'
cmux meta --branch main --port 3000 --port 5173

Start-Sleep -Milliseconds 800
cmux notify 'analyzing code'

Start-Sleep -Milliseconds 800
cmux status waiting 'awaiting permission'
cmux notify --level warn 'tool approval needed'

Start-Sleep -Milliseconds 800
cmux status working 'applying changes'

Start-Sleep -Milliseconds 800
cmux status done
cmux notify --level success 'complete'

Write-Host "agent-sim: done"
