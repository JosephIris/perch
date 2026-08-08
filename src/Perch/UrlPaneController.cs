using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Perch;

/// The Windows mechanics behind Core's UrlPanes: WebView2 controllers
/// parented to the main HWND (see UrlPaneHost for the SetParent + z-order
/// story). The registry/policy layer that used to live here moved to
/// Perch.Core/UrlPanes.cs so the mac host shares it.
internal sealed class WinUrlPaneHostFactory : IUrlPaneHostFactory
{
    private readonly Window _owner;
    private readonly Func<Microsoft.Web.WebView2.Wpf.WebView2?> _mainWebView;
    private CoreWebView2Environment? _env;

    /// `mainWebView` is a getter because the control is replaced wholesale
    /// after a browser-process crash — the factory must always read the
    /// live one.
    public WinUrlPaneHostFactory(Window owner, Func<Microsoft.Web.WebView2.Wpf.WebView2?> mainWebView)
    {
        _owner = owner;
        _mainWebView = mainWebView;
    }

    public async Task<IUrlPaneHost?> CreateAsync(Guid paneId, string url, double x, double y, double w, double h)
    {
        // Wait for the main WebView2's environment — a pane whose first
        // layout message arrives during startup beats it by a few hundred ms.
        for (var i = 0; i < 30; i++)
        {
            _env ??= _mainWebView()?.CoreWebView2?.Environment;
            if (_env != null) break;
            await Task.Delay(100);
        }
        if (_env == null) return null;

        var host = new WinUrlPaneHost(_owner, _mainWebView);
        await host.InitAsync(_env, url, x, y, w, h);
        return host;
    }

    public void Reset() => _env = null;
}

/// One WebView2 child over the page. Converts page CSS px (WPF DIPs) to
/// device pixels in main-window-client coords — the page rect is relative
/// to the webview's content area, the child HWND lives below the WPF title
/// bar, so the offset bridges the two.
internal sealed class WinUrlPaneHost : IUrlPaneHost
{
    private readonly Window _owner;
    private readonly Func<Microsoft.Web.WebView2.Wpf.WebView2?> _webHost;
    private UrlPaneHost? _inner;

    public event Action<string>? DocumentTitleChanged;
    public event Action<string>? NavigationFailed;

    public WinUrlPaneHost(Window owner, Func<Microsoft.Web.WebView2.Wpf.WebView2?> webHost)
    {
        _owner = owner;
        _webHost = webHost;
    }

    public async Task InitAsync(CoreWebView2Environment env, string url, double x, double y, double w, double h)
    {
        var mainHwnd = new WindowInteropHelper(_owner).Handle;
        _inner = new UrlPaneHost(env, mainHwnd, url, ToDeviceBounds(x, y, w, h));
        _inner.DocumentTitleChanged += t => DocumentTitleChanged?.Invoke(t);
        _inner.NavigationFailed += s => NavigationFailed?.Invoke(s);
        await Task.CompletedTask;
    }

    public void SetBounds(double x, double y, double w, double h)
    {
        var b = ToDeviceBounds(x, y, w, h);
        _inner?.SetBounds(b.X, b.Y, b.Width, b.Height);
    }

    public void SetVisible(bool visible) => _inner?.SetVisible(visible);
    public void NavigateIfChanged(string url) => _inner?.NavigateIfChanged(url);
    public void Close() { try { _inner?.Close(); } catch { } }

    private Rectangle ToDeviceBounds(double x, double y, double w, double h)
    {
        var dpi = VisualTreeHelper.GetDpi(_owner);
        var topOff = 0;
        try
        {
            var el = _webHost();
            if (el != null)
            {
                var t = el.TransformToAncestor(_owner).Transform(new System.Windows.Point(0, 0));
                topOff = (int)Math.Round(t.Y * dpi.DpiScaleY);
            }
        }
        catch { }
        return new Rectangle(
            (int)Math.Round(x * dpi.DpiScaleX),
            (int)Math.Round(y * dpi.DpiScaleY) + topOff,
            (int)Math.Round(w * dpi.DpiScaleX),
            (int)Math.Round(h * dpi.DpiScaleY));
    }
}
