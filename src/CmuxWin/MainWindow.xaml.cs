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
                case "pane.split":      OnPaneSplit(root); break;
                case "pane.close":      OnPaneClose(root); break;
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
        // Spawning is now deferred until the page reports each pane's real
        // size via the first pane.resize. Spawning at 80x24 then resizing
        // 50ms later made PowerShell emit its banner at the bootstrap size,
        // then clear-and-redraw on the resize -- the user never saw the
        // banner. By waiting for the page's measured cols/rows we hand
        // PowerShell its final size up front.
        EnsureActivePane();
        PushState();
    }

    /// Keep _activePaneId pointing at a real leaf in the active session.
    /// Doesn't spawn PTYs.
    private void EnsureActivePane()
    {
        var s = ActiveSession();
        if (s == null) return;
        if (_activePaneId == null || !AllLeaves(s.Root).Any(p => p.Id == _activePaneId))
            _activePaneId = FirstLeaf(s.Root)?.Id;
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

    private void SpawnPty(Session sess, PaneNode pane, int cols = 80, int rows = 24)
    {
        // Idempotency guard. If we somehow reach SpawnPty for a pane that
        // already has a backing ConPty, leaking it would leave an orphan
        // shell + give the page two byte streams for one xterm (output
        // interleaved beyond recognition). Log + bail; callers shouldn't
        // hit this but defensive is cheap.
        if (_ptys.ContainsKey(pane.Id))
        {
            Log.Info($"Pane.spawn.dup pane={pane.Id:N} -- already has a PTY, skipping");
            return;
        }
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
            var pty = ConPty.Start(startCmd, cols: cols, rows: rows, cwd: cwd);
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
        var cols = root.TryGetProperty("cols", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
        var rows = root.TryGetProperty("rows", out var r) && r.TryGetInt32(out var rv) ? rv : 0;

        // Don't act on degenerate measurements -- the page sends one before
        // CSS Grid has finished laying out the pane on first paint, and a
        // resize-to-tiny followed by resize-to-real makes PowerShell clear
        // the screen between the two (banner + prompt evicted).
        if (cols < 5 || rows < 3)
        {
            Log.Info($"Pane.resize.skip pane={id:N} cols={cols} rows={rows}");
            return;
        }

        // Lazy spawn: first valid pane.resize for a pane creates its
        // ConPty at the page's measured size, so PowerShell's banner is
        // laid out at the final dimensions and never has to be cleared.
        if (!_ptys.TryGetValue(id, out var pty))
        {
            var sess = OwningSession(id);
            var pane = sess == null ? null : AllLeaves(sess.Root).FirstOrDefault(p => p.Id == id);
            if (sess != null && pane != null)
            {
                Log.Info($"Pane.resize.spawn pane={id:N} cols={cols} rows={rows}");
                SpawnPty(sess, pane, cols, rows);
            }
            return;
        }
        pty.Resize(cols, rows);
    }

    private void OnSessionNew(JsonElement _)
    {
        var s = _store.AddNew();
        AutoName(s.Root, 1);
        _store.ActiveSessionId = s.Id;
        // The new session's root leaf is the active pane. PTY spawns
        // lazily on first pane.resize from the page (sized correctly).
        _activePaneId = s.Root.Id;
        _store.Save();
        PushState();
    }

    private void OnSessionSelect(JsonElement root)
    {
        if (!TryGuid(root, "id", out var id)) return;
        var sess = _store.Sessions.FirstOrDefault(s => s.Id == id);
        if (sess == null) return;
        _store.ActiveSessionId = id;
        // PTYs for the selected session's panes spawn lazily on first
        // pane.resize. Just point _activePaneId at a real leaf.
        _activePaneId = FirstLeaf(sess.Root)?.Id;
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
        EnsureActivePane();
        _store.Save();
        PushState();
    }

    private void OnPaneFocus(JsonElement root)
    {
        // Stage 3b: focus shifts the active-pane marker so split-right /
        // close-pane act on the right tile.
        if (!TryGuid(root, "paneId", out var id)) return;
        if (_activePaneId != id)
        {
            _activePaneId = id;
            PushState();
        }
    }

    // ---- Pane split / close ----------------------------------------------

    private Guid? _activePaneId;

    private void OnPaneSplit(JsonElement root)
    {
        if (!TryGuid(root, "paneId", out var id)) return;
        var dirStr = root.TryGetProperty("dir", out var d) ? d.GetString() : "right";
        var orient = dirStr == "down" ? SplitOrientation.Horizontal : SplitOrientation.Vertical;
        var sess = OwningSession(id);
        if (sess == null) return;
        var newPane = new PaneNode();
        var replacement = SplitImpl(sess.Root, id, orient, newPane);
        if (replacement == null) return;
        sess.Root = replacement;
        AutoName(sess.Root, 1);
        // PTY spawns lazily on first pane.resize from the page.
        _activePaneId = newPane.Id;
        _store.Save();
        PushState();
    }

    private void OnPaneClose(JsonElement root)
    {
        if (!TryGuid(root, "paneId", out var id)) return;
        var sess = OwningSession(id);
        if (sess == null) return;
        // Closing the only leaf in a session = close the session.
        if (sess.Root.IsLeaf && sess.Root.Id == id)
        {
            OnSessionClose(JsonDocument.Parse(
                $"{{\"id\":\"{sess.Id:D}\"}}").RootElement);
            return;
        }
        var newRoot = CloseAndCollapse(sess.Root, id);
        if (newRoot == null) return;
        DestroyPty(id);
        sess.Root = newRoot;
        AutoName(sess.Root, 1);
        // Active pane: prefer the first remaining leaf in the same session.
        _activePaneId = FirstLeaf(sess.Root)?.Id;
        _store.Save();
        PushState();
    }

    // Returns the session that owns the given pane id, or null.
    private Session? OwningSession(Guid paneId) =>
        _store.Sessions.FirstOrDefault(s => AllLeaves(s.Root).Any(p => p.Id == paneId));

    // Tree mutations. SplitImpl wraps the matching leaf in a new split node
    // with the original leaf + a fresh sibling as its two children, returning
    // the (possibly new) root. Mutates `node` in place when descending into
    // splits so the caller doesn't have to rebuild upper nodes.
    private static PaneNode? SplitImpl(PaneNode node, Guid paneId, SplitOrientation dir, PaneNode newSibling)
    {
        if (node.IsLeaf)
        {
            if (node.Id != paneId) return null;
            return new PaneNode
            {
                Split = dir,
                Children = new List<PaneNode> { node, newSibling },
            };
        }
        for (int i = 0; i < node.Children.Count; i++)
        {
            var rep = SplitImpl(node.Children[i], paneId, dir, newSibling);
            if (rep != null) { node.Children[i] = rep; return node; }
        }
        return null;
    }

    // CloseAndCollapse removes the leaf with paneId from the subtree rooted
    // at `node`. If a split is left with only one child, the split is
    // replaced by that child (the parent then unwraps recursively too).
    // Returns the replacement node, or null if the whole subtree disappeared.
    private static PaneNode? CloseAndCollapse(PaneNode node, Guid paneId)
    {
        if (node.IsLeaf) return node.Id == paneId ? null : node;
        var newChildren = new List<PaneNode>();
        foreach (var c in node.Children)
        {
            var rc = CloseAndCollapse(c, paneId);
            if (rc != null) newChildren.Add(rc);
        }
        if (newChildren.Count == 0) return null;
        if (newChildren.Count == 1) return newChildren[0];   // collapse
        node.Children = newChildren;
        return node;
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
                activePaneId    = _activePaneId?.ToString("D") ?? "",
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
            // Stage 3b verbs. The pane.* page verbs already take {paneId,...}
            // so we just forward the JsonElement through.
            case "pane.split":      OnPaneSplit(root); break;
            case "pane.close":      OnPaneClose(root); break;
            case "pane.split-active":
                // Convenience for the harness: targets the active pane so
                // the script doesn't have to look up its id from disk.
                if (_activePaneId is Guid ap)
                {
                    var dir = root.TryGetProperty("dir", out var d) ? d.GetString() : "right";
                    var fakeRoot = JsonDocument.Parse($"{{\"paneId\":\"{ap:D}\",\"dir\":\"{dir}\"}}").RootElement;
                    OnPaneSplit(fakeRoot);
                }
                break;
            case "pane.close-active":
                if (_activePaneId is Guid acp)
                {
                    var fakeRoot = JsonDocument.Parse($"{{\"paneId\":\"{acp:D}\"}}").RootElement;
                    OnPaneClose(fakeRoot);
                }
                break;
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
