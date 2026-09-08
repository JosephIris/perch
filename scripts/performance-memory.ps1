param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$all = Get-CimInstance Win32_Process
$browser = @($all | Where-Object { $_.ParentProcessId -eq $ProcessId -and $_.Name -eq 'msedgewebview2.exe' })
if ($browser.Count -ne 1) { throw 'Expected exactly one WebView browser under the specified isolated app PID.' }
$sample = @($all | Where-Object {
    $_.ProcessId -eq $ProcessId -or $_.ProcessId -eq $browser[0].ProcessId -or $_.ParentProcessId -eq $browser[0].ProcessId
} | ForEach-Object {
    $process = Get-Process -Id $_.ProcessId
    [pscustomobject]@{
        Id = $process.Id
        Role = if ($_.CommandLine -match '--type=([^ ]+)') { $matches[1] } else { $process.ProcessName }
        PrivateMiB = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)
        WorkingMiB = [math]::Round($process.WorkingSet64 / 1MB, 1)
    }
})
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$sample | ConvertTo-Json | Set-Content -LiteralPath $OutputPath
$sample | ConvertTo-Json -Compress
