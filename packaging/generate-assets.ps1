# Generates the MSIX tile/logo asset set from a single square source PNG.
#
# We don't have ImageMagick/Inkscape on this box, so this uses GDI+
# (System.Drawing, always present on Windows) with high-quality bicubic
# resampling. The source is the 256px branding glyph; every required size is a
# downscale from it (crisp) except Square150 @ scale-200 = 300px, a mild
# upscale that certification accepts. Regenerate from a higher-res raster or an
# SVG rasterizer later if you want the 300px tile pixel-perfect.
#
# Usage:  powershell -ExecutionPolicy Bypass -File packaging/generate-assets.ps1
[CmdletBinding()]
param(
  # The current app icon (bird-with-monocle on navy), refreshed 2026-07-22.
  # This is the same 256px mark the sidebar/workspace render, so tiles match.
  [string]$Source = "$PSScriptRoot/../src/web/perch-logo.png",
  [string]$OutDir = "$PSScriptRoot/Assets"
)

Add-Type -AssemblyName System.Drawing

$Source = (Resolve-Path $Source).Path
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$OutDir = (Resolve-Path $OutDir).Path

# name -> [width,height]. Square unless two dims given.
$targets = @{
  'StoreLogo.png'                                       = @(50, 50)
  'Square44x44Logo.png'                                 = @(44, 44)
  'Square44x44Logo.scale-200.png'                       = @(88, 88)
  'Square44x44Logo.targetsize-24_altform-unplated.png'  = @(24, 24)
  'Square71x71Logo.png'                                 = @(71, 71)
  'Square150x150Logo.png'                               = @(150, 150)
  'Square150x150Logo.scale-200.png'                     = @(300, 300)
}

$src = [System.Drawing.Image]::FromFile($Source)
try {
  foreach ($name in $targets.Keys) {
    $w, $h = $targets[$name]
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
      $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
      $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
      $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
      $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
      $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
      # The logo is a rounded-rect with transparent corners. On a tile those
      # corners would expose Windows' gray plate. Fill the square with the same
      # vertical navy gradient the logo uses (top #1C2C4B -> bottom #0F1A2D) so
      # the corners blend seamlessly; Win11 applies its own corner rounding.
      $top = [System.Drawing.ColorTranslator]::FromHtml('#1C2C4B')
      $bot = [System.Drawing.ColorTranslator]::FromHtml('#0F1A2D')
      $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $top, $bot, 90)
      $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
      $g.FillRectangle($brush, $rect)
      $brush.Dispose()
      $g.DrawImage($src, $rect)
    } finally { $g.Dispose() }
    $path = Join-Path $OutDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ("  {0,-52} {1}x{2}" -f $name, $w, $h)
  }
} finally { $src.Dispose() }

Write-Host "Assets written to $OutDir"
