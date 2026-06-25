<#
.SYNOPSIS
Capture a window's pixels by HWND region, robust to z-order.

.PARAMETER ProcessName
Process name to capture (e.g. "Perch"). The first instance with a non-zero
MainWindowHandle is used.

.PARAMETER ProcessId
Specific process id to capture, overrides ProcessName.

.PARAMETER OutPath
Output PNG path. Required.

.PARAMETER TopMostFirst
Briefly promote the window to HWND_TOPMOST so the screen capture sees it
even if other windows would have been in front. The window is restored to
non-topmost after capture. Default: $true.

.EXAMPLE
./capture-window.ps1 -ProcessName Perch -OutPath C:\tmp\shot.png
#>
[CmdletBinding()]
param(
    [Parameter()][string]$ProcessName,
    [Parameter()][int]$ProcessId,
    [Parameter(Mandatory)][string]$OutPath,
    [Parameter()][bool]$TopMostFirst = $true
)

Add-Type -AssemblyName System.Drawing | Out-Null
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CapWin {
  public const int HWND_TOPMOST = -1, HWND_NOTOPMOST = -2;
  public const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int X, int Y, int cx, int cy, uint f);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
'@ -ErrorAction SilentlyContinue

$proc = $null
if ($ProcessId) {
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
} elseif ($ProcessName) {
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
}
if (-not $proc) { throw "No matching process with a window found." }

$hwnd = $proc.MainWindowHandle
[void][CapWin]::ShowWindow($hwnd, 9)  # SW_RESTORE
if ($TopMostFirst) {
    [void][CapWin]::SetWindowPos($hwnd, [IntPtr]([CapWin]::HWND_TOPMOST), 0,0,0,0,
        [CapWin]::SWP_NOMOVE -bor [CapWin]::SWP_NOSIZE -bor [CapWin]::SWP_SHOWWINDOW)
}
Start-Sleep -Milliseconds 1200

$r = New-Object CapWin+RECT
[void][CapWin]::GetWindowRect($hwnd, [ref]$r)
$w = $r.R - $r.L; $h = $r.B - $r.T
if ($w -le 0 -or $h -le 0) { throw "Bad window rect: $w x $h" }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
try {
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $g.Dispose(); $bmp.Dispose()
}

if ($TopMostFirst) {
    [void][CapWin]::SetWindowPos($hwnd, [IntPtr]([CapWin]::HWND_NOTOPMOST), 0,0,0,0,
        [CapWin]::SWP_NOMOVE -bor [CapWin]::SWP_NOSIZE -bor [CapWin]::SWP_SHOWWINDOW)
}

Write-Output "Saved $OutPath ($w x $h)"
