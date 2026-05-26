using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;

namespace CmuxWin;

public partial class MainWindow : FluentWindow
{
    // Virtual-host name used to serve the bundled web content. WebView2 maps
    // requests to https://cmux.local/* to a folder under our app dir; this
    // means inside the page, `fetch('/api/...')`-style relative URLs Just
    // Work and we never need a local HTTP server.
    private const string VirtualHost = "cmux.local";

    private readonly string _webRoot;

    public MainWindow()
    {
        InitializeComponent();

        // wwwroot is staged next to the exe by the csproj. In development
        // before the web bundle exists, fall back to a stub so the window
        // still opens with a clear "missing assets" message.
        _webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

        Loaded += async (_, _) => await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            // Anchor WebView2's per-app user data dir under %APPDATA%\cmux-win
            // so we live next to errors.log / sessions.json instead of
            // scattering EBWebView caches in the install dir (the WPF build
            // had this side-effect — see %APPDATA%\cmux\CmuxWin.exe.WebView2).
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "cmux-win", "WebView2");
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;

            // Map https://cmux.local/* -> ./wwwroot/* (read-only). We use
            // HTTPS instead of HTTP so xterm.js + addons load under a secure
            // context (some WebGL/clipboard features need it).
            if (Directory.Exists(_webRoot))
            {
                core.SetVirtualHostNameToFolderMapping(
                    VirtualHost, _webRoot, CoreWebView2HostResourceAccessKind.Allow);
            }

            // Quiet down the parts of Edge we don't want for an app surface.
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = true;          // keep until 1.0
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            // Lets the page mark elements with CSS `app-region: drag` as
            // draggable window areas (Fluent title bar pattern). Without
            // this, the only way to move the window would be a thin border.
            core.Settings.IsNonClientRegionSupportEnabled = true;

            // Page → host messages land here. Each message is a JSON string;
            // the host parses { "type": "...", ... } and dispatches.
            core.WebMessageReceived += OnWebMessage;

            if (Directory.Exists(_webRoot))
            {
                core.Navigate($"https://{VirtualHost}/index.html");
            }
            else
            {
                // Bootstrap stub so a fresh clone-and-run still produces a
                // window instead of silently failing. Once src/web/ is built
                // into wwwroot/ this branch is dead.
                core.NavigateToString(BootstrapHtml(_webRoot));
            }
        }
        catch (Exception ex)
        {
            Log.Error("WebView2.Init", ex);
            System.Windows.MessageBox.Show($"WebView2 failed to initialize:\n\n{ex.Message}",
                "cmux", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Phase 1: log and ignore. Once we wire panes/ConPTY/IPC, this
        // dispatches by message type to the right handler.
        try
        {
            var msg = e.TryGetWebMessageAsString();
            Log.Info("Web.msg", msg);
        }
        catch (Exception ex) { Log.Error("Web.OnMessage", ex); }
    }

    private static string BootstrapHtml(string expected) => $@"<!doctype html>
<html><head><meta charset='utf-8'><title>cmux</title>
<style>
  html, body {{ height: 100%; margin: 0;
    font-family: 'Segoe UI Variable Text', 'Segoe UI', sans-serif;
    background: transparent; color: #cdd6f4; }}
  .stub {{ display: flex; align-items: center; justify-content: center;
    height: 100%; flex-direction: column; gap: 12px; }}
  code {{ background: rgba(255,255,255,0.06); padding: 2px 6px;
    border-radius: 4px; font-family: 'Cascadia Mono', Consolas, monospace; }}
</style></head>
<body><div class='stub'>
  <div>Web bundle not found.</div>
  <div>Expected: <code>{System.Net.WebUtility.HtmlEncode(expected)}</code></div>
  <div>Run <code>npm run build</code> in <code>src/web/</code>.</div>
</div></body></html>";
}
