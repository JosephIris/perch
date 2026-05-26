using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;

namespace CmuxWin;

public partial class MainWindow : FluentWindow
{
    private const string VirtualHost = "cmux.local";

    private readonly string _webRoot;
    private readonly Settings _settings;

    // Stage 2 single-pane scaffolding. Stage 3 promotes this to a per-pane
    // dictionary keyed by the pane id from SessionStore.
    private ConPty? _pty;
    private const string DefaultPaneId = "default";

    // Total bytes the PTY has emitted since launch. Read by the control-pipe
    // 'pty.snapshot' verb so the test harness can confirm output is flowing
    // (banner + prompt) and that a sent command produced more output.
    private long _ptyBytesReceived;

    private ControlIpcServer? _control;

    public MainWindow()
    {
        InitializeComponent();
        _webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        _settings = Settings.Load();
        Loaded += async (_, _) =>
        {
            await InitWebViewAsync();
            // Off by default. Test harness sets CMUX_ENABLE_TEST_IPC before
            // launching so it can drive the PTY without synthesizing keys.
            if (ControlIpcServer.IsEnabled)
            {
                _control = new ControlIpcServer(Dispatcher, OnControlVerb);
                _control.Start();
            }
        };
        Closed += (_, _) =>
        {
            _control?.Dispose();
            _pty?.Dispose();
        };
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "cmux-win", "WebView2");
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;

            if (Directory.Exists(_webRoot))
            {
                core.SetVirtualHostNameToFolderMapping(
                    VirtualHost, _webRoot, CoreWebView2HostResourceAccessKind.Allow);
            }

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsNonClientRegionSupportEnabled = true;

            core.WebMessageReceived += OnWebMessage;

            if (Directory.Exists(_webRoot))
            {
                core.Navigate($"https://{VirtualHost}/index.html");
            }
            else
            {
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

    // ---- Bridge ------------------------------------------------------------

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try { raw = e.TryGetWebMessageAsString(); }
        catch (Exception ex) { Log.Error("Web.OnMessage.read", ex); return; }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t)) return;
            var type = t.GetString();
            switch (type)
            {
                case "ready":     OnPageReady(root); break;
                case "pty.in":    OnPtyIn(root); break;
                case "pty.resize":OnPtyResize(root); break;
                default:
                    Log.Info("Web.msg.unknown", $"type={type}");
                    break;
            }
        }
        catch (JsonException ex) { Log.Error("Web.OnMessage.json", ex); }
        catch (Exception ex)     { Log.Error("Web.OnMessage", ex); }
    }

    private void OnPageReady(JsonElement _)
    {
        // Stage 2: spawn a single default shell for the single xterm.
        // Stage 3 replaces this with per-session pane lifecycle.
        if (_pty != null) return;
        try
        {
            var shellCommand = Shell.DefaultCommandLine(_settings.DefaultShell);
            var cwd = _settings.ResolveDefaultCwd();
            // 80x24 is fine as a bootstrap; the page sends a resize as soon
            // as FitAddon computes the real dimensions.
            _pty = ConPty.Start(shellCommand, cols: 80, rows: 24, cwd: cwd);
            _pty.OutputReceived += (_, bytes) =>
            {
                System.Threading.Interlocked.Add(ref _ptyBytesReceived, bytes.Length);
                PostPtyOut(bytes);
            };
            _pty.Exited += (_, code) => PostPtyExit(code);
            Log.Info("ConPty.Start", $"pid={_pty.ProcessId} cmd={shellCommand}");
        }
        catch (Exception ex)
        {
            Log.Error("ConPty.Start", ex);
            PostHostError($"failed to spawn shell: {ex.Message}");
        }
    }

    // ---- Control pipe verbs (test harness) --------------------------------

    private void OnControlVerb(string verb, JsonElement root)
    {
        switch (verb)
        {
            case "pty.send":
                if (_pty != null && root.TryGetProperty("text", out var t))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(t.GetString() ?? "");
                    _pty.Write(bytes);
                }
                break;
            case "pty.snapshot":
                // Log so the test can grep instead of needing a duplex pipe.
                Log.Info("Pty.snapshot",
                    $"bytes={System.Threading.Interlocked.Read(ref _ptyBytesReceived)} pid={_pty?.ProcessId ?? 0}");
                break;
            default:
                Log.Info($"ControlIpc.unknown verb={verb}");
                break;
        }
    }

    private void OnPtyIn(JsonElement root)
    {
        if (_pty == null) return;
        if (!root.TryGetProperty("b64", out var b64El)) return;
        var b64 = b64El.GetString();
        if (string.IsNullOrEmpty(b64)) return;
        try
        {
            var bytes = Convert.FromBase64String(b64);
            _pty.Write(bytes);
        }
        catch (Exception ex) { Log.Error("Pty.In", ex); }
    }

    private void OnPtyResize(JsonElement root)
    {
        if (_pty == null) return;
        var cols = root.TryGetProperty("cols", out var c) && c.TryGetInt32(out var cv) ? cv : 80;
        var rows = root.TryGetProperty("rows", out var r) && r.TryGetInt32(out var rv) ? rv : 24;
        _pty.Resize(cols, rows);
    }

    // ---- Host -> page ------------------------------------------------------

    private void PostPtyOut(ReadOnlyMemory<byte> bytes)
    {
        // Marshal to UI thread because CoreWebView2 calls must come from the
        // thread that created it. The reader thread is the only caller.
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type = "pty.out",
                    paneId = DefaultPaneId,
                    pid = _pty?.ProcessId ?? 0,
                    b64 = Convert.ToBase64String(bytes.Span),
                });
                Web.CoreWebView2?.PostWebMessageAsJson(payload);
            }
            catch (Exception ex) { Log.Error("PostPtyOut", ex); }
        });
    }

    private void PostPtyExit(int code)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type = "pty.exit",
                    paneId = DefaultPaneId,
                    code,
                });
                Web.CoreWebView2?.PostWebMessageAsJson(payload);
            }
            catch (Exception ex) { Log.Error("PostPtyExit", ex); }
        });
    }

    private void PostHostError(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var payload = JsonSerializer.Serialize(new { type = "host.error", message });
                Web.CoreWebView2?.PostWebMessageAsJson(payload);
            }
            catch (Exception ex) { Log.Error("PostHostError", ex); }
        });
    }

    // ---- Bootstrap stub when wwwroot is missing ---------------------------

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
