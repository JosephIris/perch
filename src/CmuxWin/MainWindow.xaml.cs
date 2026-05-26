using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private readonly SessionStore _store;

    // One ConPty per leaf pane. Lifetime: created on first activation of a
    // session (lazy spawn) so closed-but-persisted sessions don't fork a
    // shell at startup; disposed when the pane is removed or the session
    // closes.
    private readonly Dictionary<Guid, ConPty> _ptys = new();

    // Bytes-received-since-launch per pane id. Surface for `pty.snapshot`
    // and the test harness; the page doesn't see this.
    private readonly Dictionary<Guid, long> _ptyBytesReceived = new();

    private ControlIpcServer? _control;

    public MainWindow()
    {
        InitializeComponent();
        _webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        _settings = Settings.Load();
        _store = SessionStore.Load();
        EnsurePaneNames();
        // Persist immediately on first launch so external tools (the cmux
        // CLI, test harnesses) can read pane ids and pipe paths from disk
        // before the user does anything.
        _store.Save();

        Loaded += async (_, _) =>
        {
            await InitWebViewAsync();
            if (ControlIpcServer.IsEnabled)
            {
                _control = new ControlIpcServer(Dispatcher, OnControlVerb);
                _control.Start();
            }
        };
        Closed += (_, _) =>
        {
            _control?.Dispose();
            foreach (var pty in _ptys.Values) { try { pty.Dispose(); } catch { } }
            _ptys.Clear();
            _store.Save();
        };
    }

    private void EnsurePaneNames()
    {
        // PaneNode.Name doubles as the human-readable address for `cmux
        // focus/send/open`. Auto-assign pane-N for leaves missing a name.
        foreach (var s in _store.Sessions) AutoName(s.Root, n: 1);
    }
    private int AutoName(PaneNode node, int n)
    {
        if (node.IsLeaf)
        {
            if (string.IsNullOrEmpty(node.Name)) node.Name = $"pane-{n}";
            return n + 1;
        }
        foreach (var c in node.Children) n = AutoName(c, n);
        return n;
    }

    // ---- WebView2 init ----------------------------------------------------

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
                core.SetVirtualHostNameToFolderMapping(
                    VirtualHost, _webRoot, CoreWebView2HostResourceAccessKind.Allow);

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsNonClientRegionSupportEnabled = true;
            core.WebMessageReceived += OnWebMessage;

            if (Directory.Exists(_webRoot))
                core.Navigate($"https://{VirtualHost}/index.html");
            else
                core.NavigateToString(BootstrapHtml(_webRoot));
        }
        catch (Exception ex)
        {
            Log.Error("WebView2.Init", ex);
            System.Windows.MessageBox.Show($"WebView2 failed to initialize:\n\n{ex.Message}",
                "cmux", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    // ---- Page bridge ------------------------------------------------------

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
                case "ready":           OnPageReady(); break;
                case "pane.in":         OnPaneIn(root); break;
                case "pane.resize":     OnPaneResize(root); break;
                case "session.new":     OnSessionNew(root); break;
                case "session.select":  OnSessionSelect(root); break;
                case "session.rename":  OnSessionRename(root); break;
                case "session.close":   OnSessionClose(root); break;
                case "pane.focus":      OnPaneFocus(root); break;
                default:
                    Log.Info("Web.msg.unknown", $"type={type}");
                    break;
            }
        }
        catch (JsonException ex) { Log.Error("Web.OnMessage.json", ex); }
        catch (Exception ex)     { Log.Error("Web.OnMessage", ex); }
    }

    // ---- Lifecycle: page becomes ready -----------------------------------

    private void OnPageReady()
    {
        // Make sure the active session's pane has a backing ConPty so the
        // page sees output immediately. Other sessions stay cold until
        // selected (saves N PowerShells running in the background).
        EnsureActivePaneSpawned();
        PushState();
    }

    private void EnsureActivePaneSpawned()
    {
        var s = ActiveSession();
        if (s == null) return;
        var leaf = FirstLeaf(s.Root);
        if (leaf == null) return;
        if (_ptys.ContainsKey(leaf.Id)) return;
        SpawnPty(s, leaf);
    }

    private Session? ActiveSession()
    {
        if (_store.ActiveSessionId is Guid id)
            return _store.Sessions.FirstOrDefault(s => s.Id == id);
        return _store.Sessions.FirstOrDefault();
    }

    private static PaneNode? FirstLeaf(PaneNode node)
    {
        if (node.IsLeaf) return node;
        foreach (var c in node.Children)
            if (FirstLeaf(c) is PaneNode leaf) return leaf;
        return null;
    }

    // ---- ConPty spawn / teardown -----------------------------------------

    private void SpawnPty(Session sess, PaneNode pane)
    {
        try
        {
            var baseShell = string.IsNullOrEmpty(sess.Shell)
                ? Shell.DefaultCommandLine(_settings.DefaultShell)
                : sess.Shell;
            var cwd = !string.IsNullOrWhiteSpace(sess.Cwd) && Directory.Exists(sess.Cwd)
                ? sess.Cwd
                : _settings.ResolveDefaultCwd();
            // Shell.BuildStartupCommandLine injects CMUX_PIPE / CMUX_PANE_ID
            // env vars per-pane so agents inside the shell can call back
            // into our IPC layer (stage 4 reactivates that pipe).
            var startCmd = Shell.BuildStartupCommandLine(baseShell, cwd, pane.Id);
            var pty = ConPty.Start(startCmd, cols: 80, rows: 24, cwd: cwd);
            var paneId = pane.Id;
            pty.OutputReceived += (_, bytes) =>
            {
                lock (_ptyBytesReceived)
                    _ptyBytesReceived[paneId] = (_ptyBytesReceived.TryGetValue(paneId, out var n) ? n : 0) + bytes.Length;
                PostPaneOut(paneId, bytes);
            };
            pty.Exited += (_, code) => PostPaneExit(paneId, code);
            _ptys[paneId] = pty;
            Log.Info("Pane.spawn", $"pane={paneId:N} pid={pty.ProcessId} shell={baseShell}");
        }
        catch (Exception ex)
        {
            Log.Error($"Pane.spawn {pane.Id:N}", ex);
            PostHostError($"failed to spawn pane: {ex.Message}");
        }
    }

    private void DestroyPty(Guid paneId)
    {
        if (!_ptys.TryGetValue(paneId, out var pty)) return;
        _ptys.Remove(paneId);
        try { pty.Dispose(); } catch { }
        lock (_ptyBytesReceived) _ptyBytesReceived.Remove(paneId);
    }

    // ---- Page → host handlers --------------------------------------------

    private void OnPaneIn(JsonElement root)
    {
        if (!TryGuid(root, "paneId", out var id)) return;
        if (!_ptys.TryGetValue(id, out var pty)) return;
        if (!root.TryGetProperty("b64", out var b64El)) return;
        try { pty.Write(Convert.FromBase64String(b64El.GetString() ?? "")); }
        catch (Exception ex) { Log.Error("Pane.In", ex); }
    }

    private void OnPaneResize(JsonElement root)
    {
        if (!TryGuid(root, "paneId", out var id)) return;
        if (!_ptys.TryGetValue(id, out var pty)) return;
        var cols = root.TryGetProperty("cols", out var c) && c.TryGetInt32(out var cv) ? cv : 80;
        var rows = root.TryGetProperty("rows", out var r) && r.TryGetInt32(out var rv) ? rv : 24;
        pty.Resize(cols, rows);
    }

    private void OnSessionNew(JsonElement _)
    {
        var s = _store.AddNew();
        AutoName(s.Root, 1);
        _store.ActiveSessionId = s.Id;
        SpawnPty(s, s.Root);
        _store.Save();
        PushState();
    }

    private void OnSessionSelect(JsonElement root)
    {
        if (!TryGuid(root, "id", out var id)) return;
        if (_store.Sessions.All(s => s.Id != id)) return;
        _store.ActiveSessionId = id;
        EnsureActivePaneSpawned();
        _store.Save();
        PushState();
    }

    private void OnSessionRename(JsonElement root)
    {
        if (!TryGuid(root, "id", out var id)) return;
        if (!root.TryGetProperty("title", out var t)) return;
        var s = _store.Sessions.FirstOrDefault(x => x.Id == id);
        if (s == null) return;
        s.Title = t.GetString() ?? s.Title;
        _store.Save();
        PushState();
    }

    private void OnSessionClose(JsonElement root)
    {
        if (!TryGuid(root, "id", out var id)) return;
        var sess = _store.Sessions.FirstOrDefault(x => x.Id == id);
        if (sess == null) return;
        // Tear down every PTY owned by this session.
        foreach (var leaf in AllLeaves(sess.Root).ToList()) DestroyPty(leaf.Id);
        _store.Remove(sess);
        EnsureActivePaneSpawned();
        _store.Save();
        PushState();
    }

    private void OnPaneFocus(JsonElement root)
    {
        // Stage 3a: only one pane per session, focus is implied by active
        // session. Stage 3b uses this when there are multiple panes inside
        // a session.
        if (!TryGuid(root, "paneId", out _)) return;
        // no-op for now
    }

    private static IEnumerable<PaneNode> AllLeaves(PaneNode node)
    {
        if (node.IsLeaf) { yield return node; yield break; }
        foreach (var c in node.Children)
            foreach (var leaf in AllLeaves(c)) yield return leaf;
    }

    // ---- Host → page push ------------------------------------------------

    private void PushState()
    {
        try
        {
            var snap = new
            {
                type = "state",
                activeSessionId = _store.ActiveSessionId?.ToString("D") ?? "",
                sessions = _store.Sessions.Select(s => new
                {
                    id    = s.Id.ToString("D"),
                    title = s.Title,
                    shell = s.DisplayShell,
                    rootPane = ProjectPane(s.Root),
                }).ToArray(),
            };
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(snap));
        }
        catch (Exception ex) { Log.Error("PushState", ex); }
    }

    private static object ProjectPane(PaneNode node)
    {
        if (node.IsLeaf)
            return new
            {
                kind = "leaf",
                paneId = node.Id.ToString("D"),
                name = node.Name ?? "pane",
                url = node.Url,
            };
        return new
        {
            kind = "split",
            orientation = node.Split == SplitOrientation.Horizontal ? "h" : "v",
            children = node.Children.Select(ProjectPane).ToArray(),
        };
    }

    private void PostPaneOut(Guid paneId, ReadOnlyMemory<byte> bytes)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type   = "pane.out",
                    paneId = paneId.ToString("D"),
                    b64    = Convert.ToBase64String(bytes.Span),
                });
                Web.CoreWebView2?.PostWebMessageAsJson(payload);
            }
            catch (Exception ex) { Log.Error("PostPaneOut", ex); }
        });
    }

    private void PostPaneExit(Guid paneId, int code)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type   = "pane.exit",
                    paneId = paneId.ToString("D"),
                    code,
                });
                Web.CoreWebView2?.PostWebMessageAsJson(payload);
            }
            catch (Exception ex) { Log.Error("PostPaneExit", ex); }
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

    // ---- Control pipe verbs (test harness) -------------------------------

    private void OnControlVerb(string verb, JsonElement root)
    {
        switch (verb)
        {
            case "pty.send":
                // Stage 2 compat: targets the active session's first leaf.
                {
                    var leaf = ActiveSession() is Session s ? FirstLeaf(s.Root) : null;
                    if (leaf != null && _ptys.TryGetValue(leaf.Id, out var pty) &&
                        root.TryGetProperty("text", out var t))
                    {
                        pty.Write(System.Text.Encoding.UTF8.GetBytes(t.GetString() ?? ""));
                    }
                }
                break;
            case "pty.snapshot":
                {
                    var leaf = ActiveSession() is Session s ? FirstLeaf(s.Root) : null;
                    if (leaf != null)
                    {
                        long n;
                        lock (_ptyBytesReceived) _ptyBytesReceived.TryGetValue(leaf.Id, out n);
                        Log.Info("Pty.snapshot", $"bytes={n} pid={(_ptys.TryGetValue(leaf.Id, out var p) ? p.ProcessId : 0)}");
                    }
                }
                break;
            case "session.new":     OnSessionNew(root); break;
            case "session.select":  OnSessionSelect(root); break;
            case "session.close":   OnSessionClose(root); break;
            default:
                Log.Info($"ControlIpc.unknown verb={verb}");
                break;
        }
    }

    // ---- Helpers ---------------------------------------------------------

    private static bool TryGuid(JsonElement root, string prop, out Guid id)
    {
        id = Guid.Empty;
        if (!root.TryGetProperty(prop, out var el)) return false;
        var s = el.GetString();
        return Guid.TryParse(s, out id);
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
