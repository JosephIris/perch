# Test harness for the toast + URL Ctrl+click features.
[CmdletBinding()]
param(
    [Parameter()][string]$ExePath = 'C:\Users\josep\dev-projects\cmux-win\src\CmuxWin\bin\Debug\net8.0-windows\win10-x64\CmuxWin.exe',
    [Parameter()][string]$LogPath = "$env:APPDATA\cmux-win\errors.log",
    [Parameter()][string]$ShotPath = 'C:\tmp\cmux-test.png'
)

Add-Type -AssemblyName System.Drawing | Out-Null
Add-Type -AssemblyName System.Windows.Forms | Out-Null
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class W {
    public delegate bool EnumProc(IntPtr h, IntPtr p);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flag);
    [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int cb);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public int type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT {
        public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT {
        public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo;
    }
}
'@ -ErrorAction SilentlyContinue

function Force-Foreground($hwnd) {
    [W]::ShowWindow($hwnd, 9) | Out-Null
    $myTid = [W]::GetCurrentThreadId()
    $fgHwnd = [W]::GetForegroundWindow()
    $fgOpid = 0; $fgTid = [W]::GetWindowThreadProcessId($fgHwnd, [ref]$fgOpid)
    [W]::AttachThreadInput($myTid, $fgTid, $true) | Out-Null
    [W]::BringWindowToTop($hwnd) | Out-Null
    [W]::SetForegroundWindow($hwnd) | Out-Null
    [W]::AttachThreadInput($myTid, $fgTid, $false) | Out-Null
    Start-Sleep -Milliseconds 250
}

function Enumerate-ProcessHwnds($targetPid) {
    $top = New-Object 'System.Collections.Generic.List[IntPtr]'
    $cb = [W+EnumProc] { param($h, $p)
        $opid = 0
        [W]::GetWindowThreadProcessId($h, [ref]$opid) | Out-Null
        if ($opid -eq $targetPid -and [W]::IsWindowVisible($h)) { $top.Add($h) }
        return $true
    }
    [W]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
    return $top
}

function Get-HwndInfo($h) {
    $sb = New-Object System.Text.StringBuilder 256
    [W]::GetClassName($h, $sb, 256) | Out-Null
    $r = New-Object W+RECT
    [W]::GetWindowRect($h, [ref]$r) | Out-Null
    $parent = [W]::GetParent($h)
    return [PSCustomObject]@{
        Hwnd = $h
        Class = $sb.ToString()
        L = $r.L; T = $r.T; R = $r.R; B = $r.B
        W = $r.R - $r.L; H = $r.B - $r.T
        Parent = $parent
    }
}

function Dump-HwndTree($rootHwnd, $depth = 0) {
    $info = Get-HwndInfo $rootHwnd
    $pad = "  " * $depth
    Write-Output ("{0}hwnd={1} cls={2} pos=({3},{4}) size={5}x{6} parent={7}" -f $pad, $info.Hwnd, $info.Class, $info.L, $info.T, $info.W, $info.H, $info.Parent)
    $kids = New-Object 'System.Collections.Generic.List[IntPtr]'
    $cb = [W+EnumProc] { param($h,$p); if ([W]::GetParent($h) -eq $rootHwnd) { $kids.Add($h) }; return $true }
    [W]::EnumChildWindows($rootHwnd, $cb, [IntPtr]::Zero) | Out-Null
    foreach ($k in $kids) { Dump-HwndTree $k ($depth + 1) }
}

function Send-MouseClickWithCtrl($screenX, $screenY) {
    [W]::SetCursorPos($screenX, $screenY) | Out-Null
    Start-Sleep -Milliseconds 100
    $inputs = @()
    $kdown = New-Object W+INPUT; $kdown.type = 1   # INPUT_KEYBOARD
    $kdown.u.ki.wVk = 0x11                          # VK_CONTROL
    $inputs += $kdown

    $mdown = New-Object W+INPUT; $mdown.type = 0   # INPUT_MOUSE
    $mdown.u.mi.dwFlags = 0x0002                    # MOUSEEVENTF_LEFTDOWN
    $inputs += $mdown

    $mup = New-Object W+INPUT; $mup.type = 0
    $mup.u.mi.dwFlags = 0x0004                       # MOUSEEVENTF_LEFTUP
    $inputs += $mup

    $kup = New-Object W+INPUT; $kup.type = 1
    $kup.u.ki.wVk = 0x11
    $kup.u.ki.dwFlags = 0x0002                       # KEYEVENTF_KEYUP
    $inputs += $kup

    $arr = [W+INPUT[]]$inputs
    $cb = [System.Runtime.InteropServices.Marshal]::SizeOf([Type][W+INPUT])
    [W]::SendInput([uint32]$arr.Count, $arr, $cb) | Out-Null
}

# --- Setup ---
if (Test-Path $LogPath) { Remove-Item $LogPath -Force }
Get-Process -Name CmuxWin -EA SilentlyContinue | Where-Object { $_.Path -like '*Debug*' } | Stop-Process -Force
Start-Sleep -Milliseconds 500

# --- Launch ---
$p = Start-Process -PassThru -FilePath $ExePath
Start-Sleep -Seconds 3
if ($p.HasExited) { throw "dev exe exited code $($p.ExitCode)" }
$hwnd = $p.MainWindowHandle
Write-Output "Launched pid=$($p.Id) hwnd=$hwnd"

# --- Find the terminal HWND ---
Force-Foreground $hwnd
$top = Enumerate-ProcessHwnds $p.Id
$termHwnd = [IntPtr]::Zero
foreach ($h in $top) {
    $cb = [W+EnumProc] {
        param($child,$lp)
        $sb = New-Object System.Text.StringBuilder 64
        [W]::GetClassName($child, $sb, 64) | Out-Null
        if ($sb.ToString() -eq 'HwndTerminalClass') { $script:termHwnd = $child }
        return $true
    }
    [W]::EnumChildWindows($h, $cb, [IntPtr]::Zero) | Out-Null
}
Write-Output "Terminal HWND: $termHwnd"

# --- Test 1: clipboard / toast ---
Set-Clipboard -Value ("cmux-test-" + (Get-Random))
Start-Sleep -Milliseconds 400

# --- Capture mid-toast ---
& 'C:\Users\josep\dev-projects\cmux-win\scripts\capture-window.ps1' -ProcessId $p.Id -OutPath $ShotPath -TopMostFirst $false | Out-Null
Write-Output "Screenshot: $ShotPath"

# --- Test 2: post a URL to the buffer, then synthesize selection + right-click ---
Start-Sleep -Milliseconds 1700  # let toast fade
Force-Foreground $hwnd
Start-Sleep -Milliseconds 200

if ($termHwnd -ne [IntPtr]::Zero) {
    # Inject the URL via WM_CHAR straight to the terminal HWND (bypasses focus).
    $line = 'echo https://example.com/foo'
    foreach ($ch in $line.ToCharArray()) {
        [W]::PostMessage($termHwnd, 0x0102, [IntPtr][int]$ch, [IntPtr]1) | Out-Null
    }
    [W]::PostMessage($termHwnd, 0x0102, [IntPtr]13, [IntPtr]1) | Out-Null
    Start-Sleep -Milliseconds 1200

    # Synthesize a drag-select across the URL on the echo output line, then
    # right-click — same as a user dragging across the URL and right-clicking.
    $termInfo = Get-HwndInfo $termHwnd
    $cellW = $termInfo.W / 80.0
    $cellH = $termInfo.H / 29.0
    # The URL output line ("https://example.com/foo") is visible row 4 in
    # cmd's layout. Span columns 0..22 (URL length).
    $rowY = [int]($cellH * 4 + $cellH/2)
    $col0X = [int]($cellW * 0 + 2)
    $col1X = [int]($cellW * 22)

    $lp0 = [IntPtr](($rowY -shl 16) -bor ($col0X -band 0xFFFF))
    $lp1 = [IntPtr](($rowY -shl 16) -bor ($col1X -band 0xFFFF))
    [W]::PostMessage($termHwnd, 0x0201, [IntPtr]0x0001, $lp0) | Out-Null  # WM_LBUTTONDOWN
    Start-Sleep -Milliseconds 50
    [W]::PostMessage($termHwnd, 0x0200, [IntPtr]0x0001, $lp1) | Out-Null  # WM_MOUSEMOVE drag
    Start-Sleep -Milliseconds 50
    [W]::PostMessage($termHwnd, 0x0202, [IntPtr]0,      $lp1) | Out-Null  # WM_LBUTTONUP
    Start-Sleep -Milliseconds 300

    # Right-click at mid-URL
    $rcX = [int](($col0X + $col1X) / 2)
    $lpRC = [IntPtr](($rowY -shl 16) -bor ($rcX -band 0xFFFF))
    [W]::PostMessage($termHwnd, 0x0204, [IntPtr]0, $lpRC) | Out-Null  # WM_RBUTTONDOWN
    Start-Sleep -Milliseconds 50
    [W]::PostMessage($termHwnd, 0x0205, [IntPtr]0, $lpRC) | Out-Null  # WM_RBUTTONUP
    Start-Sleep -Milliseconds 600

    & 'C:\Users\josep\dev-projects\cmux-win\scripts\capture-window.ps1' -ProcessId $p.Id -OutPath 'C:\tmp\cmux-test-rclick.png' -TopMostFirst $false | Out-Null
    Write-Output "Right-click screenshot: C:\tmp\cmux-test-rclick.png"
}

# --- Dump entire HWND tree for the cmux process ---
Write-Output "`n=== TOP-LEVEL HWNDs ($($p.Id)) ==="
foreach ($h in $top) {
    Write-Output ""
    Dump-HwndTree $h
}

# --- Dump log ---
Write-Output "`n=== LOG ==="
if (Test-Path $LogPath) { Get-Content $LogPath } else { Write-Output "(no log)" }

# --- Cleanup ---
Stop-Process -Id $p.Id -EA SilentlyContinue
