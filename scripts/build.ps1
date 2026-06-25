<#
.SYNOPSIS
Build the web bundle AND copy it into the running host's output — in one step.

.DESCRIPTION
`npm run build` (esbuild) writes the bundle to src/Perch/wwwroot, but the app
serves the COPY under src/Perch/bin/<cfg>/.../wwwroot that `dotnet build`
produces. So editing the web and only running esbuild leaves a launched app on
the STALE bundle — the gotcha that bit us during the onboarding work. This runs
both, in order, so what you launch is what you just built.

Runs from anywhere (resolves the repo via $PSScriptRoot).

.PARAMETER SkipTypecheck
Skip `tsc --noEmit`. Faster, but esbuild does NOT type-check, so you lose TS
error detection — use only for quick iteration.

.PARAMETER Configuration
dotnet build configuration. Default: Debug.

.EXAMPLE
./scripts/build.ps1
./scripts/build.ps1 -SkipTypecheck
#>
[CmdletBinding()]
param(
    [switch]$SkipTypecheck,
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repo   = Split-Path -Parent $PSScriptRoot
$web    = Join-Path $repo 'src/web'
$csproj = Join-Path $repo 'src/Perch/Perch.csproj'

Push-Location $web
try {
    if (-not $SkipTypecheck) {
        Write-Host '> typecheck (tsc --noEmit)' -ForegroundColor Cyan
        npm run typecheck
        if ($LASTEXITCODE -ne 0) { throw 'typecheck failed' }
    }
    Write-Host '> bundle (esbuild -> src/Perch/wwwroot)' -ForegroundColor Cyan
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'esbuild failed' }
}
finally { Pop-Location }

Write-Host '> dotnet build (copies wwwroot into bin)' -ForegroundColor Cyan
dotnet build $csproj -c $Configuration -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

Write-Host '✓ built — host output now has the fresh bundle' -ForegroundColor Green
