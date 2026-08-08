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

    /// pbpaste: no AppKit binding needed, and the call sites (activation,
    /// page-ready, board paste) are rare enough that a subprocess is fine.
    public string? ReadClipboardText()
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/pbpaste")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return text;
        }
        catch (Exception ex) { Log.Error("MacHost.Clipboard", ex); return null; }
    }

    public (byte[]? Png, string? Text)? ReadClipboardForBoard()
    {
        // Image paste lands once an AppKit NSPasteboard binding exists; text
        // covers the common path today.
        var text = ReadClipboardText();
        return text == null ? null : (null, string.IsNullOrEmpty(text) ? null : text);
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
