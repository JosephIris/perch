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

        /// The page's intent for this pane: visible when its session's stage is
        /// the active one, hidden when switched away from. Independent of the
        /// modal-suppress override below — a pane is only actually shown when
        /// BOTH say so (see Apply).
        public bool DesiredVisible = true;
    }

    private readonly Dictionary<Guid, Entry> _panes = new();

    /// Panes whose create is waiting on the main WebView2's environment. A pane
    /// sits here between the first layout message and the moment the env shows
    /// up, and layout messages keep arriving that whole time (ResizeObserver
    /// fires on every reflow). Without this set, each of those messages started
    /// its OWN DeferredCreateAsync, so a pane could end up with several stacked
    /// WebView2s — and the last one to win _panes[id] orphaned the rest as
    /// undisposable child HWNDs. It also records a dispose that lands mid-wait,
    /// so the loop can bail instead of resurrecting a closed pane.
    private readonly HashSet<Guid> _pending = new();

    /// Panes we've already told the page about a policy rejection for. Keeps one
    /// refused URL from re-firing on every layout message.
    private readonly HashSet<Guid> _rejected = new();

    /// Set while a full-viewport DOM modal is up. A native web-pane HWND paints
    /// above the host's HTML, so a modal can't cover it — we hide every pane
    /// instead (airspace fix). Composes with per-pane DesiredVisible so closing
    /// the modal restores each pane to its STAGE state, not blindly to visible.
    private bool _suppressed;
    private readonly Window _owner;
    private readonly FrameworkElement _webHost;
    private readonly Microsoft.Web.WebView2.Wpf.WebView2 _mainWebView;
    private CoreWebView2Environment? _env;

    /// Raised when the WebView2's <title> changes. Host wires this to
    /// ApplyAutoTitle so the pane name can auto-rename to the website title.
    /// Dispatcher.BeginInvoke is the caller's responsibility — we fire on
    /// the UI thread already.
    public event Action<Guid, string>? AutoTitleRequested;

    /// Raised when a pane's URL fails WebUrlPolicy. The host forwards it to the
    /// page so the pane renders "can't open this address" instead of the empty
    /// placeholder that reads as a blank page.
    public event Action<Guid, string>? UrlPaneRejected;

    /// Raised when a pane's navigation completes unsuccessfully. WebView2 paints
    /// its own error page for most web failures, but a missing file:// target
    /// renders as an empty document, so the page needs to be able to say so.
    public event Action<Guid, string>? UrlPaneFailed;

    public UrlPaneController(Window owner, Microsoft.Web.WebView2.Wpf.WebView2 mainWebView)
    {
        _owner = owner;
        _webHost = mainWebView;
        _mainWebView = mainWebView;
    }

    /// Modal opened (true) / closed (false). Re-applies every pane's effective
    /// visibility; off-stage panes stay hidden because their DesiredVisible is
    /// already false.
    public void SetSuppressed(bool on)
    {
        _suppressed = on;
        foreach (var e in _panes.Values) Apply(e);
    }

    /// Page intent for one pane, driven by stage switches: visible=false when
    /// its session is switched away from (the WebView2 is HIDDEN, not closed, so
    /// returning is instant and doesn't reload), visible=true on return.
    public void SetVisible(Guid paneId, bool visible)
    {
        if (!_panes.TryGetValue(paneId, out var e)) return;
        e.DesiredVisible = visible;
        Apply(e);
    }

    private void Apply(Entry e) => e.Host.SetVisible(e.DesiredVisible && !_suppressed);

    public bool HasPanes => _panes.Count > 0;

    /// The URL a pane is currently pointed at, or null if we don't know it.
    public string? UrlOf(Guid paneId) => _panes.TryGetValue(paneId, out var e) ? e.Url : null;

    /// Handle the page's urlpane.layout message. Creates a new UrlPaneWindow
    /// on first call for a paneId; subsequent calls reposition + resize.
    public void OnLayout(UrlPaneLayoutMsg msg)
    {
        var id = msg.PaneId;
        var url = msg.Url;
        if (string.IsNullOrEmpty(url)) return;
        // Defense in depth (audit issue #1, item 2): the page filters with the
        // same policy (web-url.ts), but the host must never create or
        // re-navigate a native WebView2 pane to a scheme outside it
        // (javascript:, data:, a file:// to an .exe, ...) even if a malformed
        // message slips through. Every layout message — first-sight create and
        // subsequent re-navigate — funnels through here.
        //
        // Rejection is REPORTED, not swallowed. A refused URL leaves the page's
        // placeholder div with no WebView2 behind it, which looks exactly like a
        // blank page; UrlPaneRejected lets the pane say why instead.
        if (!WebUrlPolicy.IsAllowed(url))
        {
            // Once per pane, not once per layout message: a rejected pane keeps
            // reporting its rect (ResizeObserver fires on every reflow, and a
            // window drag fires it continuously), and re-posting the same error
            // on each would be a message storm for no added information.
            if (_rejected.Add(id))
            {
                Log.Info("UrlPane.reject", $"pane={id:N} url rejected by policy");
                UrlPaneRejected?.Invoke(id, url!);
            }
            return;
        }
        _rejected.Remove(id);   // a later message may carry a URL we do accept
        var (x, y, w, h) = (msg.X, msg.Y, msg.W, msg.H);

        var (px, py, pw, ph) = DipsToPixels(x, y, w, h);
        var topOff = WebTopOffsetInPixels();
        var bounds = new Rectangle(px, py + topOff, pw, ph);

        if (!_panes.TryGetValue(id, out var entry))
        {
            if (_pending.Contains(id)) return;   // create already in flight
            Log.Info("UrlPane.create", $"pane={id:N} url={url} bounds={bounds}");
            _env ??= _mainWebView.CoreWebView2?.Environment;
            if (_env == null)
            {
                Log.Info("UrlPane.create.deferred", "main WebView2 env not ready yet");
                _pending.Add(id);
                _ = DeferredCreateAsync(id, url!, bounds);
                return;
            }
            entry = new Entry { Host = CreateHost(id, url!, bounds), Url = url!, X = x, Y = y, W = w, H = h };
            _panes[id] = entry;
            Apply(entry);   // respect an open modal at create time
        }
        else
        {
            entry.X = x; entry.Y = y; entry.W = w; entry.H = h;
            entry.Host.SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            if (entry.Url != url) { entry.Host.NavigateIfChanged(url!); entry.Url = url!; }
        }
    }

    /// Build a UrlPaneHost for `id` and hook its events. Both create paths
    /// (immediate and deferred) go through here so a pane created during
    /// startup gets exactly the same wiring as one created later — the deferred
    /// copy used to be a hand-maintained duplicate that quietly lacked whatever
    /// the immediate path had gained since.
    private UrlPaneHost CreateHost(Guid id, string url, Rectangle bounds)
    {
        var mainHwnd = new WindowInteropHelper(_owner).Handle;
        var host = new UrlPaneHost(_env!, mainHwnd, url, bounds);
        host.DocumentTitleChanged += (title) =>
            _owner.Dispatcher.BeginInvoke(() => AutoTitleRequested?.Invoke(id, title));
        host.NavigationFailed += (status) =>
            _owner.Dispatcher.BeginInvoke(() => UrlPaneFailed?.Invoke(id, status));
        return host;
    }

    private async Task DeferredCreateAsync(Guid id, string url, Rectangle bounds)
    {
        try
        {
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(100);
                // The pane was closed while we waited — creating now would leave
                // a WebView2 nobody has a handle to.
                if (!_pending.Contains(id)) return;
                _env ??= _mainWebView.CoreWebView2?.Environment;
                if (_env == null) continue;
                var deferredEntry = new Entry { Host = CreateHost(id, url, bounds), Url = url };
                _panes[id] = deferredEntry;
                Apply(deferredEntry);   // respect an open modal at create time
                return;
            }
            Log.Info("UrlPane.create.timeout", $"pane={id:N} env never became ready");
        }
        finally { _pending.Remove(id); }
    }

    /// Handle the page's urlpane.dispose message — close the child window.
    public void OnDispose(PaneRef msg)
    {
        // Clearing _pending first cancels an in-flight deferred create; without
        // it, a pane closed during startup came back as an orphan HWND.
        _pending.Remove(msg.PaneId);
        _rejected.Remove(msg.PaneId);
        if (!_panes.TryGetValue(msg.PaneId, out var entry)) return;
        try { entry.Host.Close(); } catch { }
        _panes.Remove(msg.PaneId);
    }

    /// Drop every child window without waiting for page messages. Used when
    /// the shared browser process died: the hosts are already dead, this just
    /// tears down their HWNDs and forgets the (stale) environment.
    public void CloseAll()
    {
        foreach (var e in _panes.Values) { try { e.Host.Close(); } catch { } }
        _panes.Clear();
        _pending.Clear();
        _rejected.Clear();
        _env = null;
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
