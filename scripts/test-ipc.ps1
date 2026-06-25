# Headless end-to-end test of the perch IPC plumbing.
#
# Stands up a NamedPipeServerStream that mimics what PerchIpcServer does on
# the host side, spawns perch.exe with PERCH_PIPE pointing at that pipe, and
# asserts the JSON payload that arrives matches the subcommand we ran.
#
# Run from anywhere; the script resolves perch.exe relative to the repo.

$ErrorActionPreference = 'Stop'

$repo  = Split-Path $PSScriptRoot -Parent
$perch  = Join-Path $repo 'src\Perch\bin\Debug\net8.0-windows\win10-x64\tools\perch.exe'
if (-not (Test-Path $perch)) {
    throw "perch.exe not found at $perch — build the host first (dotnet build src/Perch)"
}

Add-Type -AssemblyName System.Core

function Invoke-Case {
    param([string]$Name, [string[]]$CliArgs, [scriptblock]$Assert)

    $pipeName = "perch\test-$([Guid]::NewGuid().ToString('N'))"
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
        $psi = [System.Diagnostics.ProcessStartInfo]::new($perch)
        foreach ($a in $CliArgs) { $psi.ArgumentList.Add($a) }
        $psi.RedirectStandardError = $true
        $psi.RedirectStandardOutput = $true
        $psi.UseShellExecute = $false
        $psi.EnvironmentVariables['PERCH_PIPE'] = $pipePath
        $psi.EnvironmentVariables['PERCH_PANE_ID'] = 'deadbeef'
        $proc = [System.Diagnostics.Process]::Start($psi)

        if (-not $acceptTask.Wait(4000)) { throw "[$Name] server never accepted" }
        $reader = [System.IO.StreamReader]::new($server)
        $line = $reader.ReadLine()

        if (-not $proc.WaitForExit(3000)) { $proc.Kill(); throw "[$Name] perch didn't exit" }
        $stderr = $proc.StandardError.ReadToEnd()
        if ($proc.ExitCode -ne 0) {
            throw "[$Name] perch.exe exited $($proc.ExitCode): $stderr"
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

Write-Host "Running perch IPC tests..."

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

# Smoke: no PERCH_PIPE -> silent no-op, exit 0, prints nothing.
$env:PERCH_PIPE = $null
$noop = & $perch notify "should be silent" 2>&1
$noopExit = $LASTEXITCODE
if ($noopExit -ne 0) { throw "no-op: expected exit 0, got $noopExit" }
if ($noop) { throw "no-op: expected no output, got: $noop" }
Write-Host "  OK   no-op when PERCH_PIPE is unset"

Write-Host "All perch IPC tests passed."
