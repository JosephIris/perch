using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Photino.NET;

namespace Perch;

/// The mac mechanics behind Core's UrlPanes: one WKWebView added as a
/// subview of the window's content view per URL pane. No HWND airspace
/// wars here — AppKit composites siblings normally — but the views still
/// paint above the page's HTML, so Core's suppress/visible logic applies
/// unchanged.
///
/// Coordinates: page CSS px are AppKit points 1:1 (the main webview fills
/// the content view, including under the transparent title bar). AppKit's
/// y-axis is bottom-up, so frames are flipped against the content view's
/// current height on every SetBounds; window resizes re-emit layout via
/// ui.urlpane.relayout, same as Windows.
internal sealed class MacUrlPaneHostFactory : IUrlPaneHostFactory
{
    private readonly PhotinoWindow _window;

    public MacUrlPaneHostFactory(PhotinoWindow window) => _window = window;

    public Task<IUrlPaneHost?> CreateAsync(Guid paneId, string url, double x, double y, double w, double h)
    {
        var tcs = new TaskCompletionSource<IUrlPaneHost?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _window.Invoke(() =>
            {
                try
                {
                    var host = new MacUrlPaneHost(_window);
                    host.Init(url, x, y, w, h);
                    tcs.SetResult(host);
                }
                catch (Exception ex)
                {
                    Log.Error("MacUrlPane.create", ex);
                    tcs.SetResult(null);
                }
            });
        }
        catch (Exception ex) { Log.Error("MacUrlPane.invoke", ex); tcs.TrySetResult(null); }
        return tcs.Task;
    }

    public void Reset() { }   // no shared engine state to forget
}

internal sealed class MacUrlPaneHost : IUrlPaneHost
{
    private readonly PhotinoWindow _window;
    private IntPtr _webView;

    // Title/failed need a WKNavigationDelegate (an ObjC class of our own) —
    // deferred; Core treats both events as optional.
    public event Action<string>? DocumentTitleChanged { add { } remove { } }
    public event Action<string>? NavigationFailed { add { } remove { } }

    public MacUrlPaneHost(PhotinoWindow window) => _window = window;

    /// Must run on the AppKit main thread (factory invokes us there).
    public void Init(string url, double x, double y, double w, double h)
    {
        var config = Objc.New("WKWebViewConfiguration");
        var frame = Flip(x, y, w, h);
        var webView = Objc.Send(
            Objc.Send(Objc.Cls("WKWebView"), Objc.Sel("alloc")),
            Objc.Sel("initWithFrame:configuration:"), frame, config);
        if (webView == IntPtr.Zero) throw new InvalidOperationException("WKWebView init failed");
        Objc.SendVoid(config, Objc.Sel("release"));   // the webview retains it
        _webView = webView;
        Objc.SendVoid(ContentView(), Objc.Sel("addSubview:"), webView);
        Navigate(url);
    }

    public void SetBounds(double x, double y, double w, double h)
        => _window.Invoke(() =>
        {
            if (_webView == IntPtr.Zero) return;
            Objc.SendVoidRect(_webView, Objc.Sel("setFrame:"), Flip(x, y, w, h));
        });

    public void SetVisible(bool visible)
        => _window.Invoke(() =>
        {
            if (_webView == IntPtr.Zero) return;
            Objc.SendVoidBool(_webView, Objc.Sel("setHidden:"), !visible);
        });

    public void NavigateIfChanged(string url) => _window.Invoke(() => Navigate(url));

    public void Close()
        => _window.Invoke(() =>
        {
            if (_webView == IntPtr.Zero) return;
            Objc.SendVoid(_webView, Objc.Sel("removeFromSuperview"));
            Objc.SendVoid(_webView, Objc.Sel("release"));
            _webView = IntPtr.Zero;
        });

    private void Navigate(string url)
    {
        if (_webView == IntPtr.Zero) return;
        var nsUrl = Objc.Send(Objc.Cls("NSURL"), Objc.Sel("URLWithString:"), Objc.NSString(url));
        if (nsUrl == IntPtr.Zero) return;
        var request = Objc.Send(Objc.Cls("NSURLRequest"), Objc.Sel("requestWithURL:"), nsUrl);
        Objc.SendVoid(_webView, Objc.Sel("loadRequest:"), request);
    }

    private IntPtr ContentView()
    {
        var app = Objc.Send(Objc.Cls("NSApplication"), Objc.Sel("sharedApplication"));
        var windows = Objc.Send(app, Objc.Sel("windows"));
        var nsWindow = Objc.Send(windows, Objc.Sel("firstObject"));
        return Objc.Send(nsWindow, Objc.Sel("contentView"));
    }

    /// Page CSS rect (top-left origin) → AppKit frame (bottom-left origin)
    /// in the content view's coordinate space.
    private Objc.CGRect Flip(double x, double y, double w, double h)
    {
        var bounds = Objc.SendRect(ContentView(), Objc.Sel("bounds"));
        return new Objc.CGRect { X = x, Y = bounds.H - y - h, W = w, H = h };
    }
}

/// Minimal objc_msgSend surface for the WKWebView dance. Struct args and
/// returns follow the arm64/x64 ABI via normal P/Invoke marshalling (CGRect
/// is 4 doubles — an HFA in registers going in, an sret pointer coming out).
internal static class Objc
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect { public double X, Y, W, H; }

    [DllImport(Lib, EntryPoint = "objc_getClass")]
    public static extern IntPtr Cls(string name);
    [DllImport(Lib, EntryPoint = "sel_registerName")]
    public static extern IntPtr Sel(string name);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr recv, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr recv, IntPtr sel, IntPtr arg);
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr recv, IntPtr sel, CGRect rect, IntPtr arg);
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(IntPtr recv, IntPtr sel, IntPtr arg);
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(IntPtr recv, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern void SendVoidBool(IntPtr recv, IntPtr sel, bool arg);
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern void SendVoidRect(IntPtr recv, IntPtr sel, CGRect rect);
    // NOTE arm64-only as written: x64 struct returns >16 bytes must go
    // through objc_msgSend_stret instead. The mac build targets osx-arm64.
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern CGRect SendRect(IntPtr recv, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendStr(IntPtr recv, IntPtr sel, string utf8);

    public static IntPtr NSString(string s) =>
        SendStr(Cls("NSString"), Sel("stringWithUTF8String:"), s);

    public static IntPtr New(string cls) =>
        Send(Send(Cls(cls), Sel("alloc")), Sel("init"));
}
