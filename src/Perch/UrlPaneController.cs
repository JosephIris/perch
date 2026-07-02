using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Perch;

/// Owns the per-URL-pane WebView2 lifecycle. The page emits urlpane.layout
/// (with rect in WebView2-client DIPs) for each URL leaf; we create a
/// UrlPaneWindow on first sight, reparent its HWND to be a true child of
/// the main window via Win32 SetParent, and position it via MoveWindow in
/// parent-client device pixels. After reparenting, Windows handles
/// move/resize/maximize natively — no event hooks needed here.
///
/// Lives outside MainWindow.xaml.cs to keep that file focused on web-bridge
/// dispatch and session management. The only dependencies are the main
/// Window (for HWND + DPI) and the main WebView2 control (so we can ask
/// where it sits inside the window).
internal sealed class UrlPaneController
{
    private sealed class Entry
    {
        public required UrlPaneHost Host;
        public required string Url;
        public double X, Y, W, H;   // client-space DIPs (last reported)
    }

    private readonly Dictionary<Guid, Entry> _panes = new();
    private readonly Window _owner;
    private readonly FrameworkElement _webHost;
    private readonly Microsoft.Web.WebView2.Wpf.WebView2 _mainWebView;
    private CoreWebView2Environment? _env;

    /// Raised when the WebView2's <title> changes. Host wires this to
    /// ApplyAutoTitle so the pane name can auto-rename to the website title.
    /// Dispatcher.BeginInvoke is the caller's responsibility — we fire on
    /// the UI thread already.
    public event Action<Guid, string>? AutoTitleRequested;

    public UrlPaneController(Window owner, Microsoft.Web.WebView2.Wpf.WebView2 mainWebView)
    {
        _owner = owner;
        _webHost = mainWebView;
        _mainWebView = mainWebView;
    }

    public void HideAll() { foreach (var e in _panes.Values) e.Host.SetVisible(false); }
    public void ShowAll() { foreach (var e in _panes.Values) e.Host.SetVisible(true); }
    public bool HasPanes => _panes.Count > 0;

    /// Handle the page's urlpane.layout message. Creates a new UrlPaneWindow
    /// on first call for a paneId; subsequent calls reposition + resize.
    public void OnLayout(UrlPaneLayoutMsg msg)
    {
        var id = msg.PaneId;
        var url = msg.Url;
        if (string.IsNullOrEmpty(url)) return;
        var (x, y, w, h) = (msg.X, msg.Y, msg.W, msg.H);

        var (px, py, pw, ph) = DipsToPixels(x, y, w, h);
        var topOff = WebTopOffsetInPixels();
        var bounds = new Rectangle(px, py + topOff, pw, ph);

        if (!_panes.TryGetValue(id, out var entry))
        {
            Log.Info("UrlPane.create", $"pane={id:N} url={url} bounds={bounds}");
            _env ??= _mainWebView.CoreWebView2?.Environment;
            if (_env == null)
            {
                Log.Info("UrlPane.create.deferred", "main WebView2 env not ready yet");
                _ = DeferredCreateAsync(id, url!, bounds);
                return;
            }
            var mainHwnd = new WindowInteropHelper(_owner).Handle;
            var host = new UrlPaneHost(_env, mainHwnd, url!, bounds);
            var paneId = id;
            host.DocumentTitleChanged += (title) =>
                _owner.Dispatcher.BeginInvoke(() => AutoTitleRequested?.Invoke(paneId, title));
            entry = new Entry { Host = host, Url = url!, X = x, Y = y, W = w, H = h };
            _panes[id] = entry;
        }
        else
        {
            entry.X = x; entry.Y = y; entry.W = w; entry.H = h;
            entry.Host.SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            if (entry.Url != url) { entry.Host.NavigateIfChanged(url!); entry.Url = url!; }
        }
    }

    private async Task DeferredCreateAsync(Guid id, string url, Rectangle bounds)
    {
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            _env ??= _mainWebView.CoreWebView2?.Environment;
            if (_env == null) continue;
            var mainHwnd = new WindowInteropHelper(_owner).Handle;
            var host = new UrlPaneHost(_env, mainHwnd, url, bounds);
            var paneId = id;
            host.DocumentTitleChanged += (title) =>
                _owner.Dispatcher.BeginInvoke(() => AutoTitleRequested?.Invoke(paneId, title));
            _panes[id] = new Entry { Host = host, Url = url };
            return;
        }
    }

    /// Handle the page's urlpane.dispose message — close the child window.
    public void OnDispose(PaneRef msg)
    {
        if (!_panes.TryGetValue(msg.PaneId, out var entry)) return;
        try { entry.Host.Close(); } catch { }
        _panes.Remove(msg.PaneId);
    }

    /// Page rect is in WPF DIPs relative to the WebView2's content area.
    /// MoveWindow expects device pixels relative to the parent HWND's
    /// client area. Convert via the per-monitor DPI of the main window.
    private (int x, int y, int w, int h) DipsToPixels(double x, double y, double w, double h)
    {
        var dpi = VisualTreeHelper.GetDpi(_owner);
        return ((int)Math.Round(x * dpi.DpiScaleX),
                (int)Math.Round(y * dpi.DpiScaleY),
                (int)Math.Round(w * dpi.DpiScaleX),
                (int)Math.Round(h * dpi.DpiScaleY));
    }

    /// Height (in device pixels) of the WPF chrome above the WebView2 —
    /// i.e. the TitleBar. Layout messages come in WebView2-client coords;
    /// reparented child windows live in main-window-client coords. The
    /// offset bridges the two.
    private int WebTopOffsetInPixels()
    {
        try
        {
            var t = _webHost.TransformToAncestor(_owner).Transform(new System.Windows.Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(_owner);
            return (int)Math.Round(t.Y * dpi.DpiScaleY);
        }
        catch { return 0; }
    }

}
