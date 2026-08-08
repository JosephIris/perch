using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Perch;

/// A WKNavigationDelegate built at runtime with the ObjC runtime API — one
/// shared delegate instance serves every URL-pane WKWebView; callbacks look
/// the owning host up by webview pointer. Gives the mac host the two events
/// the Windows host gets from WebView2: the document title (pane
/// auto-naming) and navigation failures (the "couldn't open that file"
/// overlay). Callbacks arrive on the AppKit main thread; Core's UrlPanes
/// marshals to the app thread when it subscribes.
internal static class MacNavDelegate
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [DllImport(Lib)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
    [DllImport(Lib)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Lib)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    private static readonly ConcurrentDictionary<IntPtr, MacUrlPaneHost> Hosts = new();
    private static IntPtr _instance;

    /// Register the class once and return the shared delegate instance.
    /// Main thread only.
    public static IntPtr Instance()
    {
        if (_instance != IntPtr.Zero) return _instance;

        var cls = objc_allocateClassPair(Objc.Cls("NSObject"), "PerchNavDelegate", 0);
        if (cls == IntPtr.Zero)
        {
            Log.Error("MacNav.register", new Exception("objc_allocateClassPair returned null"));
            return IntPtr.Zero;
        }
        unsafe
        {
            class_addMethod(cls, Objc.Sel("webView:didFinishNavigation:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidFinish, "v@:@@");
            class_addMethod(cls, Objc.Sel("webView:didFailNavigation:withError:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidFail, "v@:@@@");
            class_addMethod(cls, Objc.Sel("webView:didFailProvisionalNavigation:withError:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidFail, "v@:@@@");
        }
        objc_registerClassPair(cls);
        _instance = Objc.Send(Objc.Send(cls, Objc.Sel("alloc")), Objc.Sel("init"));
        return _instance;
    }

    public static void Track(IntPtr webView, MacUrlPaneHost host) => Hosts[webView] = host;
    public static void Untrack(IntPtr webView) => Hosts.TryRemove(webView, out _);

    [UnmanagedCallersOnly]
    private static void DidFinish(IntPtr self, IntPtr sel, IntPtr webView, IntPtr navigation)
    {
        try
        {
            if (!Hosts.TryGetValue(webView, out var host)) return;
            // WKWebView's `title` often still reads empty at didFinish; the
            // host polls it briefly instead of standing up title KVO.
            host.ReadTitleSoon();
        }
        catch (Exception ex) { Log.Error("MacNav.finish", ex); }
    }

    [UnmanagedCallersOnly]
    private static void DidFail(IntPtr self, IntPtr sel, IntPtr webView, IntPtr navigation, IntPtr error)
    {
        try
        {
            if (!Hosts.TryGetValue(webView, out var host)) return;
            var desc = error == IntPtr.Zero
                ? null
                : NsToString(Objc.Send(error, Objc.Sel("localizedDescription")));
            host.RaiseFailed(string.IsNullOrWhiteSpace(desc) ? "navigation failed" : desc!);
        }
        catch (Exception ex) { Log.Error("MacNav.fail", ex); }
    }

    private static string? NsToString(IntPtr ns)
    {
        if (ns == IntPtr.Zero) return null;
        var utf8 = Objc.Send(ns, Objc.Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }
}
