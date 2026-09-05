# Builds a Microsoft Store MSIX for Perch.
#
#   packaging/pack-msix.ps1                 # unsigned .msix (what you upload to the Store)
#   packaging/pack-msix.ps1 -Sign           # + self-signed, for local sideload testing only
#   packaging/pack-msix.ps1 -SkipPublish     # repack existing staged output (fast iteration)
#
# The Store re-signs the package on ingestion, so the artifact you SUBMIT is
# unsigned. -Sign exists only so you can install the package on THIS machine to
# smoke-test that ConPTY / claude / WebView2 all work under package identity.
#
# Identity (Name/Publisher/PublisherDisplayName/Version) comes from
# packaging/identity.json (copy identity.example.json and paste the Partner
# Center values). That file is gitignored on purpose.
[CmdletBinding()]
param(
  [string]$Configuration = 'Release',
  [string]$Version,                 # overrides identity.json Version if given
  [switch]$Sign,
  [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot   # repo root
$pkgDir  = $PSScriptRoot
$stage   = Join-Path $pkgDir 'stage'
$outDir  = Join-Path $pkgDir 'out'
$assets  = Join-Path $pkgDir 'Assets'

# --- locate SDK tools -------------------------------------------------------
function Find-KitTool($name) {
  $bin = 'C:\Program Files (x86)\Windows Kits\10\bin'
  $hit = Get-ChildItem "$bin\*\x64\$name" -ErrorAction SilentlyContinue |
         Sort-Object FullName -Descending | Select-Object -First 1
  if (-not $hit) { throw "$name not found under $bin. Install the Windows 10/11 SDK." }
  return $hit.FullName
}
$makeappx = Find-KitTool 'makeappx.exe'

# --- identity ---------------------------------------------------------------
$idFile = Join-Path $pkgDir 'identity.json'
if (-not (Test-Path $idFile)) {
  throw "packaging/identity.json missing. Copy identity.example.json to identity.json and fill in the Partner Center values."
}
$id = Get-Content $idFile -Raw | ConvertFrom-Json

# Version precedence: -Version > git tag > identity.json.
#
# identity.json is gitignored, so a version living only in that file is a
# version no commit records: the Store and the Velopack channel drift apart
# and nothing in the repo says which commit a Store user is running. The git
# tag is the same source build.yml uses for the Velopack channel, so deriving
# from it keeps both channels on one number. The Store reserves the 4th part,
# so a 3-part tag (v1.41.0) becomes 1.41.0.0.
if (-not $Version) {
  $tag = & git -C $root describe --tags --abbrev=0 2>$null
  if ($LASTEXITCODE -eq 0 -and $tag -match '^v?(\d+)\.(\d+)\.(\d+)$') {
    $Version = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
    Write-Host "Version from git tag '$tag': $Version"
  } else {
    Write-Warning "No usable git tag; falling back to identity.json Version ($($id.Version))."
  }
}
if ($Version) { $id.Version = $Version }
# Only the submission artifact has to honour the reserved 4th part. Local
# sideload testing legitimately bumps it (1.41.0.1, .2, ...) because
# Add-AppxPackage needs a higher version to upgrade in place, and those builds
# are self-signed and never leave the machine.
if (-not $Sign -and $id.Version -notmatch '^\d+\.\d+\.\d+\.0$') {
  throw "Version '$($id.Version)' must be 4-part with a trailing .0; the Store reserves the 4th part and rejects anything else. (Use -Sign for local test builds, which may bump it.)"
}
foreach ($k in 'Name','Publisher','PublisherDisplayName','Version') {
  if (-not $id.$k -or "$($id.$k)" -match 'XXXX|PublisherName|Your Name') {
    throw "identity.json field '$k' is still a placeholder ($($id.$k))."
  }
}
Write-Host "Identity: $($id.Name)  $($id.Publisher)  v$($id.Version)"

# --- publish the app --------------------------------------------------------
if (-not $SkipPublish) {
  if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
  Write-Host "Publishing Perch ($Configuration, self-contained win-x64) ..."
  & dotnet publish (Join-Path $root 'src\Perch\Perch.csproj') `
      -c $Configuration -r win-x64 --self-contained true `
      -o $stage --nologo
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
  # Trim payload the package does not need.
  Get-ChildItem $stage -Recurse -Include *.pdb -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
}
if (-not (Test-Path (Join-Path $stage 'Perch.exe'))) {
  throw "staged Perch.exe missing. Run without -SkipPublish first."
}

# --- manifest + assets into the stage --------------------------------------
$tpl = Get-Content (Join-Path $pkgDir 'AppxManifest.template.xml') -Raw
$manifest = $tpl.
  Replace('{{PackageName}}',          $id.Name).
  Replace('{{Publisher}}',            $id.Publisher).
  Replace('{{PublisherDisplayName}}', $id.PublisherDisplayName).
  Replace('{{Version}}',              $id.Version)
# UTF-8 without BOM: makeappx rejects a BOM on the manifest.
[System.IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, (New-Object System.Text.UTF8Encoding($false)))

$stageAssets = Join-Path $stage 'Assets'
if (Test-Path $stageAssets) { Remove-Item $stageAssets -Recurse -Force }
Copy-Item $assets $stageAssets -Recurse

# --- pack -------------------------------------------------------------------
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$msix = Join-Path $outDir "Perch_$($id.Version).msix"
if (Test-Path $msix) { Remove-Item $msix -Force }
Write-Host "Packing $msix ..."
& $makeappx pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)" }
Write-Host "Built $msix"

# --- optional local-test signing -------------------------------------------
if ($Sign) {
  $signtool = Find-KitTool 'signtool.exe'
  $existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $id.Publisher -and $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3' } |
    Select-Object -First 1
  if (-not $existing) {
    Write-Host "Creating self-signed test cert for $($id.Publisher) ..."
    $existing = New-SelfSignedCertificate -Type CodeSigningCert -Subject $id.Publisher `
      -KeyUsage DigitalSignature -FriendlyName 'Perch MSIX Test (local only)' `
      -CertStoreLocation Cert:\CurrentUser\My `
      -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
  }
  & $signtool sign /fd SHA256 /sha1 $existing.Thumbprint $msix
  if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }

  $cer = Join-Path $outDir 'PerchTest.cer'
  Export-Certificate -Cert $existing -FilePath $cer -Force | Out-Null
  Write-Host ""
  Write-Host "Signed for LOCAL TEST ONLY. To trust + install (elevated PowerShell):" -ForegroundColor Yellow
  Write-Host "  Import-Certificate -FilePath `"$cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
  Write-Host "  Add-AppxPackage `"$msix`""
  Write-Host "Do NOT sign the package you upload to the Store; submit the unsigned one."
}
