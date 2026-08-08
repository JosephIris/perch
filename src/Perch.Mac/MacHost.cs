using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Photino.NET;

namespace Perch;

/// The mac host shell: a Photino window (WKWebView) plus the native services
/// AppController asks for. The web bundle is served over loopback by
/// StaticServer; JSON messages ride Photino's window.external bridge (see
/// the Photino branch in src/web/src/bridge.ts).
internal sealed class MacHost : IWebViewHost, IWindowHost
{
    private readonly PhotinoWindow _window;
    private readonly StaticServer _server;
    private readonly AppDispatcher _ui;
    private readonly string _webRoot;

    public MacHost(PhotinoWindow window, StaticServer server, AppDispatcher ui, string webRoot)
    {
        _window = window;
        _server = server;
        _ui = ui;
        _webRoot = webRoot;
        // Photino raises this on its own thread; app state lives on the app
        // dispatcher, so marshal before touching the controller.
        _window.RegisterWebMessageReceivedHandler((_, msg) =>
            _ui.Post(() => MessageReceived?.Invoke(msg)));
    }

    // ---- IWebViewHost -----------------------------------------------------

    public string WebRoot => _webRoot;

    public event Action<string>? MessageReceived;

    // WKWebView content-process death isn't surfaced by Photino today; the
    // crash-recovery policy in AppController simply never fires on mac.
    public event Action<WebViewFailure>? ProcessFailed { add { } remove { } }

    public Task<bool> InitAsync() => Task.FromResult(true);

    public void PostJson(string json)
    {
        try { _window.SendWebMessage(json); }
        // Posts before the native window exists are dropped by contract
        // (IWebViewHost.PostJson) — the page re-syncs everything at `ready`.
        catch (ApplicationException) { }
        catch (Exception ex) { Log.Error("MacHost.Post", ex); }
    }

    public void NavigateToApp(bool disableWebgl)
        => _window.Load($"{_server.BaseUrl}/index.html" + (disableWebgl ? "?nowebgl=1" : ""));

    public void NavigateToString(string html) => _window.LoadRawString(html);

    public void Reload() => NavigateToApp(disableWebgl: false);

    public Task RecreateAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    // ---- IWindowHost ------------------------------------------------------

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr ObjcGetClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr SelRegisterName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjcMsgSend(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern long ObjcMsgSendLong(IntPtr receiver, IntPtr selector, long arg);

    private const long NSCriticalRequest = 0;
    private const long NSInformationalRequest = 10;

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjcMsgSendStr(IntPtr receiver, IntPtr selector, string arg);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjcMsgSendPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern ulong ObjcMsgSendULong(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void ObjcMsgSendVoidULong(IntPtr receiver, IntPtr selector, ulong arg);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void ObjcMsgSendVoidBool(IntPtr receiver, IntPtr selector, bool arg);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void ObjcMsgSendVoidLong(IntPtr receiver, IntPtr selector, long arg);

    private const ulong NSWindowStyleMaskFullSizeContentView = 1UL << 15;
    private const long NSWindowTitleHidden = 1;

    /// Native-window dressing, the mac analogue of the WPF host's
    /// ExtendsContentIntoTitleBar + Mica-dark chrome:
    ///  - dark appearance app-wide (the chrome is dark-first; without this
    ///    AppKit paints a light title bar over the dark page)
    ///  - full-size content view + transparent title bar + hidden title, so
    ///    the page extends under the top strip and the traffic lights float
    ///    over it (the strip stays native-draggable). The page leaves a top
    ///    inset for it — see html.host-photino rules in the web bundle.
    /// Call on the Photino thread once the window exists.
    public void ApplyMacChrome()
    {
        try
        {
            _window.Invoke(() =>
            {
                var name = ObjcMsgSendStr(ObjcGetClass("NSString"),
                    SelRegisterName("stringWithUTF8String:"), "NSAppearanceNameDarkAqua");
                var appearance = ObjcMsgSendPtr(ObjcGetClass("NSAppearance"),
                    SelRegisterName("appearanceNamed:"), name);
                var app = ObjcMsgSend(ObjcGetClass("NSApplication"), SelRegisterName("sharedApplication"));
                if (app != IntPtr.Zero && appearance != IntPtr.Zero)
                    ObjcMsgSendPtr(app, SelRegisterName("setAppearance:"), appearance);

                // PhotinoWindow.WindowHandle is Windows-only; reach the
                // NSWindow through NSApp.windows.firstObject instead (one
                // window per process today).
                var windows = ObjcMsgSend(app, SelRegisterName("windows"));
                var nsWindow = windows == IntPtr.Zero
                    ? IntPtr.Zero
                    : ObjcMsgSend(windows, SelRegisterName("firstObject"));
                if (nsWindow == IntPtr.Zero) return;
                var mask = ObjcMsgSendULong(nsWindow, SelRegisterName("styleMask"));
                ObjcMsgSendVoidULong(nsWindow, SelRegisterName("setStyleMask:"),
                    mask | NSWindowStyleMaskFullSizeContentView);
                ObjcMsgSendVoidBool(nsWindow, SelRegisterName("setTitlebarAppearsTransparent:"), true);
                ObjcMsgSendVoidLong(nsWindow, SelRegisterName("setTitleVisibility:"), NSWindowTitleHidden);
            });
        }
        catch (Exception ex) { Log.Error("MacHost.Chrome", ex); }
    }

    /// Dock-icon bounce — the mac analogue of the taskbar flash. Loud
    /// (critical) bounces until the app is foregrounded; gentle bounces once.
    /// AppKit ignores the request entirely when the app is already active,
    /// which matches the Windows impl's foreground check for free.
    public void FlashAttention(bool loud)
    {
        try
        {
            _window.Invoke(() =>
            {
                var app = ObjcMsgSend(ObjcGetClass("NSApplication"), SelRegisterName("sharedApplication"));
                if (app != IntPtr.Zero)
                    ObjcMsgSendLong(app, SelRegisterName("requestUserAttention:"),
                        loud ? NSCriticalRequest : NSInformationalRequest);
            });
        }
        catch (Exception ex) { Log.Error("MacHost.Flash", ex); }
    }

    // ---- Clipboard (NSPasteboard) ----------------------------------------
    // macOS has no clipboard-change notification; the watcher polls
    // changeCount (an integer bump per copy — cheap) and fires on change.

    private System.Threading.Timer? _clipTimer;
    private long _clipCount = -1;

    public void StartClipboardWatcher(Action onChange)
    {
        _clipTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                var count = Objc.SendLong(Pasteboard(), Objc.Sel("changeCount"));
                if (_clipCount != -1 && count != _clipCount) onChange();
                _clipCount = count;
            }
            catch (Exception ex) { Log.Error("MacHost.ClipWatch", ex); }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private static IntPtr Pasteboard()
        => Objc.Send(Objc.Cls("NSPasteboard"), Objc.Sel("generalPasteboard"));

    public string? ReadClipboardText()
    {
        try
        {
            var ns = Objc.Send(Pasteboard(), Objc.Sel("stringForType:"),
                Objc.NSString("public.utf8-plain-text"));
            if (ns == IntPtr.Zero) return "";
            var utf8 = Objc.Send(ns, Objc.Sel("UTF8String"));
            return utf8 == IntPtr.Zero ? "" : (Marshal.PtrToStringUTF8(utf8) ?? "");
        }
        catch (Exception ex) { Log.Error("MacHost.Clipboard", ex); return null; }
    }

    public (byte[]? Png, string? Text)? ReadClipboardForBoard()
    {
        try
        {
            // Picture first — a copied image often ships a file path or HTML
            // alongside it, and the picture is what the user meant. PNG when
            // offered directly; TIFF (the AppKit lingua franca) converted
            // through sips otherwise.
            var png = ReadClipboardData("public.png");
            if (png == null)
            {
                var tiff = ReadClipboardData("public.tiff");
                if (tiff != null) png = TiffToPng(tiff);
            }
            if (png != null) return (png, null);
            var text = ReadClipboardText();
            return (null, string.IsNullOrEmpty(text) ? null : text);
        }
        catch (Exception ex) { Log.Error("MacHost.ClipBoard", ex); return null; }
    }

    private static byte[]? ReadClipboardData(string type)
    {
        var data = Objc.Send(Pasteboard(), Objc.Sel("dataForType:"), Objc.NSString(type));
        if (data == IntPtr.Zero) return null;
        var len = Objc.SendLong(data, Objc.Sel("length"));
        if (len <= 0 || len > 64 * 1024 * 1024) return null;
        var bytes = Objc.Send(data, Objc.Sel("bytes"));
        if (bytes == IntPtr.Zero) return null;
        var buf = new byte[len];
        Marshal.Copy(bytes, buf, 0, (int)len);
        return buf;
    }

    private static byte[]? TiffToPng(byte[] tiff)
    {
        var tmpIn = Path.Combine(Path.GetTempPath(), $"perch-clip-{Guid.NewGuid():N}.tiff");
        var tmpOut = Path.ChangeExtension(tmpIn, ".png");
        try
        {
            File.WriteAllBytes(tmpIn, tiff);
            var psi = new ProcessStartInfo("/usr/bin/sips")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (var a in new[] { "-s", "format", "png", tmpIn, "--out", tmpOut })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10000) || p.ExitCode != 0 || !File.Exists(tmpOut)) return null;
            return File.ReadAllBytes(tmpOut);
        }
        catch { return null; }
        finally
        {
            try { File.Delete(tmpIn); } catch { }
            try { File.Delete(tmpOut); } catch { }
        }
    }

    public async Task<string?> PickFolderAsync(string? initialDir)
    {
        var res = await OsaScriptAsync(
            "POSIX path of (choose folder with prompt \"Add project\")");
        return res;
    }

    public async Task<string[]?> PickFilesAsync(string? initialDir)
    {
        // `choose file ... multiple selections allowed` returns a comma-
        // separated alias list; emit one POSIX path per line instead so the
        // split is unambiguous.
        var res = await OsaScriptAsync(
            "set fs to choose file with prompt \"Add a file to the board\" with multiple selections allowed\n" +
            "set out to \"\"\n" +
            "repeat with f in fs\n" +
            "  set out to out & POSIX path of f & \"\\n\"\n" +
            "end repeat\n" +
            "return out");
        return res?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// Run an AppleScript and return its trimmed stdout; null on cancel/error
    /// (a cancelled `choose` exits non-zero, which is exactly "user said no").
    private static async Task<string?> OsaScriptAsync(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);
            using var p = Process.Start(psi)!;
            var text = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) return null;
            text = text.Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) { Log.Error("MacHost.osascript", ex); return null; }
    }
}
