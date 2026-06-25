<#
.SYNOPSIS
Capture the perch window into design-loop/. Uses PrintWindow with
PW_RENDERFULLCONTENT so Mica chrome AND native HwndHost children (terminal
HWND, WebView2 panes) all render — unlike screen CopyFromScreen which depends
on z-order and fails for occluded windows.

.PARAMETER ProcessName
Process name to capture. Defaults to Perch.

.PARAMETER ProcessId
Specific PID; overrides ProcessName when set.

.PARAMETER OutDir
Root output directory. current.png goes here; a timestamped copy under
$OutDir/history. Defaults to the repo's design-loop/.

.EXAMPLE
./scripts/screenshot.ps1
./scripts/screenshot.ps1 -ProcessId 12345
#>
[CmdletBinding()]
param(
    [Parameter()][string]$ProcessName = 'Perch',
    [Parameter()][int]$ProcessId,
    [Parameter()][string]$OutDir = (Join-Path $PSScriptRoot '..\design-loop' | Resolve-Path -Relative -ErrorAction SilentlyContinue)
)

if (-not $OutDir) {
    $OutDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'design-loop'
}
$historyDir = Join-Path $OutDir 'history'
New-Item -ItemType Directory -Force -Path $OutDir, $historyDir | Out-Null

Add-Type -AssemblyName System.Drawing | Out-Null
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Shot {
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    // PW_RENDERFULLCONTENT (Win 8.1+) renders WebView/DirectComposition/etc.
    public const uint PW_RENDERFULLCONTENT = 0x00000002;
}
'@ -ErrorAction SilentlyContinue

# --- Resolve target window ---
$proc = $null
if ($ProcessId) {
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
} else {
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } |
            Select-Object -First 1
}
if (-not $proc) { throw "No window found for $ProcessName / pid=$ProcessId. Is the app running?" }

$hwnd = $proc.MainWindowHandle
if (-not [Shot]::IsWindow($hwnd)) { throw "HWND $hwnd is not a valid window." }

$rect = New-Object Shot+RECT
if (-not [Shot]::GetWindowRect($hwnd, [ref]$rect)) { throw "GetWindowRect failed." }
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
if ($w -le 0 -or $h -le 0) { throw "Bad window rect: ${w}x${h}" }

# --- Capture via PrintWindow ---
$bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$gfx = [System.Drawing.Graphics]::FromImage($bmp)
try {
    $hdc = $gfx.GetHdc()
    try {
        $ok = [Shot]::PrintWindow($hwnd, $hdc, [Shot]::PW_RENDERFULLCONTENT)
        if (-not $ok) { throw "PrintWindow returned FALSE (window may not support full-content rendering)." }
    } finally {
        $gfx.ReleaseHdc($hdc)
    }
} finally {
    $gfx.Dispose()
}

# --- Save ---
$currentPath = Join-Path $OutDir 'current.png'
$ts          = (Get-Date).ToString('yyyyMMdd-HHmmss')
$historyPath = Join-Path $historyDir "$ts.png"
$bmp.Save($currentPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Save($historyPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Output "Captured ${w}x${h} from pid=$($proc.Id) hwnd=$hwnd"
Write-Output "  current: $currentPath"
Write-Output "  history: $historyPath"
