# Headless end-to-end test of the cmux IPC plumbing.
#
# Stands up a NamedPipeServerStream that mimics what CmuxIpcServer does on
# the host side, spawns cmux.exe with CMUX_PIPE pointing at that pipe, and
# asserts the JSON payload that arrives matches the subcommand we ran.
#
# Run from anywhere; the script resolves cmux.exe relative to the repo.

$ErrorActionPreference = 'Stop'

$repo  = Split-Path $PSScriptRoot -Parent
$cmux  = Join-Path $repo 'src\CmuxWin\bin\Debug\net8.0-windows\win10-x64\tools\cmux.exe'
if (-not (Test-Path $cmux)) {
    throw "cmux.exe not found at $cmux — build the host first (dotnet build src/CmuxWin)"
}

Add-Type -AssemblyName System.Core

function Invoke-Case {
    param([string]$Name, [string[]]$CliArgs, [scriptblock]$Assert)

    $pipeName = "cmux\test-$([Guid]::NewGuid().ToString('N'))"
    $pipePath = "\\.\pipe\$pipeName"

    # Stand up the server FIRST so the CLI's Connect() succeeds immediately
    # (it polls with a 2s timeout, but our accept needs to be pending before
    # the client opens the file or we race).
    $server = [System.IO.Pipes.NamedPipeServerStream]::new(
        $pipeName,
        [System.IO.Pipes.PipeDirection]::In,
        1,
        [System.IO.Pipes.PipeTransmissionMode]::Byte,
        [System.IO.Pipes.PipeOptions]::Asynchronous)
    $acceptTask = $server.WaitForConnectionAsync()

    try {
        $psi = [System.Diagnostics.ProcessStartInfo]::new($cmux)
        foreach ($a in $CliArgs) { $psi.ArgumentList.Add($a) }
        $psi.RedirectStandardError = $true
        $psi.RedirectStandardOutput = $true
        $psi.UseShellExecute = $false
        $psi.EnvironmentVariables['CMUX_PIPE'] = $pipePath
        $psi.EnvironmentVariables['CMUX_PANE_ID'] = 'deadbeef'
        $proc = [System.Diagnostics.Process]::Start($psi)

        if (-not $acceptTask.Wait(4000)) { throw "[$Name] server never accepted" }
        $reader = [System.IO.StreamReader]::new($server)
        $line = $reader.ReadLine()

        if (-not $proc.WaitForExit(3000)) { $proc.Kill(); throw "[$Name] cmux didn't exit" }
        $stderr = $proc.StandardError.ReadToEnd()
        if ($proc.ExitCode -ne 0) {
            throw "[$Name] cmux.exe exited $($proc.ExitCode): $stderr"
        }
        if (-not $line) { throw "[$Name] no line received" }

        $obj = $line | ConvertFrom-Json
        & $Assert $obj
        Write-Host "  OK   $Name -> $line"
    }
    finally {
        $server.Dispose()
    }
}

Write-Host "Running cmux IPC tests..."

Invoke-Case -Name 'notify (no level)' `
            -CliArgs @('notify', 'hello world') `
            -Assert {
    param($o)
    if ($o.type -ne 'notify') { throw "type=$($o.type)" }
    if ($o.text -ne 'hello world') { throw "text=$($o.text)" }
    if ($null -ne $o.level) { throw "expected level absent, got $($o.level)" }
}

Invoke-Case -Name 'notify --level success' `
            -CliArgs @('notify', '--level', 'success', 'tests passing') `
            -Assert {
    param($o)
    if ($o.type -ne 'notify') { throw "type=$($o.type)" }
    if ($o.level -ne 'success') { throw "level=$($o.level)" }
    if ($o.text -ne 'tests passing') { throw "text=$($o.text)" }
}

Invoke-Case -Name 'status working with detail' `
            -CliArgs @('status', 'working', 'running', 'tests') `
            -Assert {
    param($o)
    if ($o.type -ne 'status') { throw "type=$($o.type)" }
    if ($o.state -ne 'working') { throw "state=$($o.state)" }
    if ($o.detail -ne 'running tests') { throw "detail=$($o.detail)" }
}

Invoke-Case -Name 'meta with branch+port' `
            -CliArgs @('meta', '--branch', 'main', '--port', '3000') `
            -Assert {
    param($o)
    if ($o.type -ne 'meta') { throw "type=$($o.type)" }
    if ($o.branch -ne 'main') { throw "branch=$($o.branch)" }
    if ($o.ports.Count -ne 1 -or $o.ports[0] -ne 3000) { throw "ports=$($o.ports -join ',')" }
}

# Smoke: no CMUX_PIPE -> silent no-op, exit 0, prints nothing.
$env:CMUX_PIPE = $null
$noop = & $cmux notify "should be silent" 2>&1
$noopExit = $LASTEXITCODE
if ($noopExit -ne 0) { throw "no-op: expected exit 0, got $noopExit" }
if ($noop) { throw "no-op: expected no output, got: $noop" }
Write-Host "  OK   no-op when CMUX_PIPE is unset"

Write-Host "All cmux IPC tests passed."
