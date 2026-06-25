using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Perch;

/// Listens for clipboard updates while the host window is active and raises
/// <see cref="ClipboardChanged"/>. Used to surface a "Copied" toast when the
/// user copies from a terminal pane — the Microsoft.Terminal.Wpf control emits
/// no managed event for selection-copy, so we detect it at the clipboard level
/// and gate on window-foreground so other apps' copies don't fire toasts.
internal sealed class ClipboardWatcher : IDisposable
{
    private readonly Window _window;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _registered;

    public event Action? ClipboardChanged;

    public ClipboardWatcher(Window window) { _window = window; }

    public void Attach()
    {
        // Window may not yet have an HWND when called from ctor — defer if so.
        var helper = new WindowInteropHelper(_window);
        if (helper.Handle == IntPtr.Zero)
        {
            _window.SourceInitialized += (_, _) => Attach();
            return;
        }
        if (_registered) return;

        _hwnd = helper.Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        if (AddClipboardFormatListener(_hwnd)) _registered = true;
        else Log.Error("ClipboardWatcher.Attach", new InvalidOperationException(
            $"AddClipboardFormatListener failed: {Marshal.GetLastWin32Error()}"));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && GetForegroundWindow() == _hwnd)
            ClipboardChanged?.Invoke();
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered && _hwnd != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_hwnd);
            _registered = false;
        }
        _source?.RemoveHook(WndProc);
    }

    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
