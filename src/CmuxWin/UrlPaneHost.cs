using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace CmuxWin;

/// Hosts a single WebView2 instance as a direct Win32 child of the main
/// window — no WPF Window wrapper in between. Replaces the old
/// UrlPaneWindow approach (WPF Window + SetParent + chrome-bit stripping)
/// which fought with WPF's internal layout: WPF kept resizing the embedded
/// WebView2 to its own model size, so our Win32 MoveWindow/SetWindowPos
/// calls had no visible effect after the first create.
///
/// CoreWebView2Controller takes a raw parent HWND and manages its own
/// child HWND inside it. We set Bounds (a Rect in parent-client pixels)
/// directly on the controller — no WPF layout pass involved, so the size
/// sticks. Bounds is the standard API for "where the WebView2 lives";
/// it's what Microsoft's own samples use for nested WebView2s.
internal sealed class UrlPaneHost
{
    public CoreWebView2Controller? Controller { get; private set; }
    public string CurrentUrl { get; private set; }

    private readonly IntPtr _parentHwnd;
    private Rectangle _pendingBounds;
    private bool _disposed;
    private bool _pendingVisible = true;

    public event Action<string>? DocumentTitleChanged;

    public UrlPaneHost(CoreWebView2Environment env, IntPtr parentHwnd, string url, Rectangle bounds)
    {
        _parentHwnd = parentHwnd;
        CurrentUrl = url;
        _pendingBounds = bounds;
        _ = InitAsync(env, url);
    }

    private async Task InitAsync(CoreWebView2Environment env, string url)
    {
        try
        {
            // Snapshot existing WebView2 child HWNDs BEFORE creating the
            // new controller so we can find the new one afterward by
            // diffing the lists.
            var beforeHwnds = new System.Collections.Generic.HashSet<IntPtr>(EnumWebView2Children(_parentHwnd));

            Log.Info("UrlPaneHost.Init.begin", $"url={url}");
            Controller = await env.CreateCoreWebView2ControllerAsync(_parentHwnd);
            if (_disposed) { Controller.Close(); return; }
            Controller.Bounds = _pendingBounds;
            Controller.IsVisible = _pendingVisible;
            Controller.DefaultBackgroundColor = Color.FromArgb(0x1F, 0x1F, 0x1F);
            Controller.CoreWebView2.DocumentTitleChanged += (_, _) =>
            {
                var t = Controller.CoreWebView2.DocumentTitle;
                if (!string.IsNullOrWhiteSpace(t)) DocumentTitleChanged?.Invoke(t);
            };
            Controller.CoreWebView2.Navigate(url);

            // Find the newly-created WebView2 HWND (diff vs. snapshot) and
            // bring it to the top of z-order. WPF's HwndHost for the main
            // WebView2 tries to keep itself topmost; without an explicit
            // BringWindowToTop, the new URL WebView2 sits BEHIND the main
            // one and never renders visibly.
            await Task.Yield();
            var afterHwnds = EnumWebView2Children(_parentHwnd);
            foreach (var h in afterHwnds)
            {
                if (beforeHwnds.Contains(h)) continue;
                Log.Info("UrlPaneHost.Init.ztop", $"new hwnd=0x{h.ToInt64():X}");
                SetWindowPos(h, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }
        catch (Exception ex) { Log.Error("UrlPaneHost.Init", ex); }
    }

    // EnumChildWindows on the parent HWND, returning HWNDs whose class name
    // suggests they're WebView2 (Chrome-derived). Used to identify the
    // newly-created controller's HWND so we can bring it to z-top.
    private static System.Collections.Generic.List<IntPtr> EnumWebView2Children(IntPtr parent)
    {
        var list = new System.Collections.Generic.List<IntPtr>();
        EnumChildWindows(parent, (h, _) =>
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            var cls = sb.ToString();
            if (cls.StartsWith("Chrome_") || cls.Contains("WebView2"))
                list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOP = new(0);
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// Move + resize the WebView2 within the parent HWND. Coords are in
    /// device pixels relative to the parent's client area. Cheap — Bounds
    /// is the standard Microsoft-blessed setter for WebView2 positioning.
    public void SetBounds(int x, int y, int w, int h)
    {
        _pendingBounds = new Rectangle(x, y, w, h);
        if (Controller != null) Controller.Bounds = _pendingBounds;
    }

    public void NavigateIfChanged(string url)
    {
        if (CurrentUrl == url) return;
        CurrentUrl = url;
        try { Controller?.CoreWebView2?.Navigate(url); }
        catch (Exception ex) { Log.Error("UrlPaneHost.Navigate", ex); }
    }

    public void SetVisible(bool visible)
    {
        _pendingVisible = visible;
        if (Controller != null) Controller.IsVisible = visible;
    }

    public void Close()
    {
        _disposed = true;
        try { Controller?.Close(); } catch { }
        Controller = null;
    }
}
