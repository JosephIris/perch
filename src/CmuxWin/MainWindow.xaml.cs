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

    // Per-pane named-pipe server listening on \\.\pipe\cmux\<paneId>.
    // Agents inside the pane talk to us via the cmux CLI ("cmux status
    // working") which dials this pipe. Lifecycle mirrors _ptys exactly.
    private readonly Dictionary<Guid, CmuxIpcServer> _paneIpc = new();

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
            // URL-pane controller owns the per-URL-pane WebView2 lifecycle.
            // Wired after WebView2 init so Web.TransformToAncestor returns
            // valid coords inside the controller.
            _urlPaneCtrl = new UrlPaneController(this, Web);
            _urlPaneCtrl.AutoTitleRequested += (paneId, title) =>
                ApplyAutoTitle(paneId, title);
            // On every main-window size change (interactive drag,
            // maximize, restore, snap), tell the page to re-emit each URL
            // pane's rect. forceRefit() in url-pane.ts invalidates the
            // rect cache so the IPC always fires, even when the page
            // thinks size hasn't changed yet (which can happen if the
            // CSS reflow lags behind the HWND resize by a frame).
            SizeChanged += (_, _) =>
            {
                if (_urlPaneCtrl?.HasPanes == true)
                    try { Web.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"ui.urlpane.relayout\"}"); } catch { }
            };
            if (ControlIpcServer.IsEnabled)
            {
                _control = new ControlIpcServer(Dispatcher, OnControlVerb);
                _control.Start();
            }
        };
        Closed += (_, _) =>
        {
            _control?.Dispose();
            foreach (var ipc in _paneIpc.Values) { try { ipc.Dispose(); } catch { } }
            _paneIpc.Clear();
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

    // ---- WPF chrome → webview commands ------------------------------------
    // The WPF-side title bar carries the sidebar toggle so it stays
    // clickable when the webview hides the sidebar. We don't track the
    // collapse state here — we just hand the verb off and let the page
    // flip its own class. See main.ts's "ui.sidebar.toggle" handler.

    private void OnSidebarToggleClick(object sender, System.Windows.RoutedEventArgs e)
    {
        Web.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"ui.sidebar.toggle\"}");
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
                case "pane.rename":     OnPaneRename(root); break;
                case "pane.recolor":    OnPaneRecolor(root); break;
                case "pane.cwd":        OnPaneCwd(root); break;
                case "urlpane.layout":  OnUrlPaneLayout(root); break;
                case "urlpane.dispose": OnUrlPaneDispose(root); break;
                case "session.new":     OnSessionNew(root); break;
                case "session.select":  OnSessionSelect(root); break;
                case "session.rename":  OnSessionRename(root); break;
                case "session.close":   OnSessionClose(root); break;
                case "pane.focus":      OnPaneFocus(root); break;
                case "url.open":        OnUrlOpen(root); break;
                case "prefs.set":       OnPrefsSet(root); break;
                case "settings.request": OnSettingsRequest(); break;
                case "settings.save":    OnSettingsSave(root); break;
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
            pty.Exited += (_, code) =>
            {
                // Drop the dead PTY + IPC so a subsequent pane.resize naturally
                // respawns into the same paneId. Without this the resize handler
                // sees a stale _ptys entry and just calls Resize on a dead pty.
                Dispatcher.BeginInvoke(() => DestroyPty(paneId));
                PostPaneExit(paneId, code);
            };
            _ptys[paneId] = pty;

            // Agent IPC: per-pane named pipe \\.\pipe\cmux\<paneId>. The
            // pane's shell inherits CMUX_PIPE pointing here, so `cmux
            // status working` from inside the shell lands at OnStatus.
            var ipc = new CmuxIpcServer(paneId, Dispatcher);
            ipc.OnStatus += msg => OnAgentStatus(sess, paneId, msg);
            ipc.OnNotify += msg => OnAgentNotify(sess, paneId, msg);
            ipc.OnMeta   += msg => OnAgentMeta(sess, paneId, msg);
            ipc.OnGitBaseline += msg => OnGitBaseline(sess, paneId, msg);
            ipc.Start();
            _paneIpc[paneId] = ipc;

            Log.Info("Pane.spawn", $"pane={paneId:N} pid={pty.ProcessId} shell={baseShell} cmd={startCmd}");
        }
        catch (Exception ex)
        {
            Log.Error($"Pane.spawn {pane.Id:N}", ex);
            PostHostError($"failed to spawn pane: {ex.Message}");
        }
    }

    private void DestroyPty(Guid paneId)
    {
        if (_paneIpc.Remove(paneId, out var ipc)) { try { ipc.Dispose(); } catch { } }
        if (!_ptys.TryGetValue(paneId, out var pty)) return;
        _ptys.Remove(paneId);
        try { pty.Dispose(); } catch { }
        lock (_ptyBytesReceived) _ptyBytesReceived.Remove(paneId);
    }

    // ---- Agent IPC handlers (cmux status / notify / meta) ----------------

    private static AgentState ParseAgentState(string? s) => s switch
    {
        "working" => AgentState.Working,
        "waiting" => AgentState.Waiting,
        "done"    => AgentState.Done,
        _         => AgentState.Idle,
    };

    private static NotificationLevel ParseLevel(string? s) => s switch
    {
        "success" => NotificationLevel.Success,
        "warn"    => NotificationLevel.Warn,
        "warning" => NotificationLevel.Warn,
        "error"   => NotificationLevel.Error,
        _         => NotificationLevel.Info,
    };

    // Agent IPC writes per-pane state now — each pane in a session carries
    // its own AgentState / Branch / Ports / Notification, so 3-5 parallel
    // agents per repo don't thrash one shared row. The sidebar aggregates
    // by computing "most urgent" across panes; the pane header shows its
    // own state inline.

    private static PaneNode? FindPane(Session sess, Guid paneId)
        => AllLeaves(sess.Root).FirstOrDefault(p => p.Id == paneId);

    private void OnAgentStatus(Session sess, Guid paneId, StatusMessage msg)
    {
        var pane = FindPane(sess, paneId);
        if (pane == null) return;
        var prev = pane.AgentState;
        pane.AgentState = ParseAgentState(msg.State);
        pane.ActivityDetail = msg.Detail ?? "";
        // Attention nudge: any → Waiting transition flashes the taskbar
        // (only when our window isn't already foreground). One place to
        // raise the signal so it works for both Claude-via-hooks and any
        // other agent calling `cmux status waiting` directly.
        if (prev != AgentState.Waiting && pane.AgentState == AgentState.Waiting)
        {
            FlashAttention();
        }
        // Refresh the cc-session commit counter on every state change
        // (cheap if no baseline is set, otherwise one `git rev-list`).
        _ = RefreshCommitCountAsync(pane);
        PushState();
    }

    // Baseline received from the cc HookHandler on session-start. An empty
    // sha clears the counter (session-end). Triggers an immediate count
    // refresh so the chip shows "+0 commits" right away instead of waiting
    // for the next state transition.
    private void OnGitBaseline(Session sess, Guid paneId, GitBaselineMessage msg)
    {
        var pane = FindPane(sess, paneId);
        if (pane == null) return;
        pane.CommitBaseline = msg.Sha ?? "";
        if (string.IsNullOrEmpty(pane.CommitBaseline))
        {
            pane.CommitCount = 0;
            PushState();
            return;
        }
        _ = RefreshCommitCountAsync(pane);
    }

    private async System.Threading.Tasks.Task RefreshCommitCountAsync(PaneNode pane)
    {
        if (string.IsNullOrEmpty(pane.CommitBaseline)) return;
        if (!_paneCwd.TryGetValue(pane.Id, out var cwd) || string.IsNullOrEmpty(cwd)) return;
        var count = await GitProc.CommitsSinceAsync(pane.CommitBaseline, cwd);
        if (count is not int n) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (pane.CommitCount != n) { pane.CommitCount = n; PushState(); }
        });
    }

    private void OnAgentNotify(Session sess, Guid paneId, NotifyMessage msg)
    {
        var pane = FindPane(sess, paneId);
        if (pane == null) return;
        pane.NotificationText = msg.Text ?? "";
        pane.NotificationLevel = ParseLevel(msg.Level);
        PushState();
        PostToast(msg.Text ?? "", msg.Level);
    }

    private void OnAgentMeta(Session sess, Guid paneId, MetaMessage msg)
    {
        var pane = FindPane(sess, paneId);
        if (pane == null) return;
        if (msg.Branch != null) pane.Branch = msg.Branch;
        if (msg.Ports != null) pane.Ports = msg.Ports;
        // Cwd stays at session level for now — it seeds the default cwd
        // for new panes. Per-pane cwd is implicit in the shell process.
        if (!string.IsNullOrWhiteSpace(msg.Cwd)) sess.Cwd = msg.Cwd!;
        PushState();
    }

    // FlashWindowEx P/Invoke for the taskbar-attention nudge. Defined inline
    // here because it's the only place in the app that needs it; if more
    // surfaces need to flash we can promote to a NativeMethods helper.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FLASHWINFO {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }
    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;     // stop flashing when foreground
    private void FlashAttention()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var fi = new FLASHWINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                uCount = 5,
                dwTimeout = 0,
            };
            FlashWindowEx(ref fi);
        }
        catch (Exception ex) { Log.Error("FlashAttention", ex); }
    }

    private void PostToast(string text, string? level)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type = "toast",
                    text,
                    level = string.IsNullOrEmpty(level) ? "info" : level,
                });
                Web.CoreWebView2?.PostWebMessageAsJson(payload);
            }
            catch (Exception ex) { Log.Error("PostToast", ex); }
        });
    }

    // ---- Page → host handlers --------------------------------------------

    private void OnPaneIn(JsonElement root)
    {
        if (!TryGuid(root, "paneId", out var id)) return;
        if (!_ptys.TryGetValue(id, out var pty)) return;
        if (!root.TryGetProperty("b64", out var b64El)) return;
        try { pty.Write(Convert.FromBase64String(b64El.GetString() ?? "")); }
        catch (Exception ex) { Log.Error("Pane.In", ex); }

        // Clear stale 'waiting' as soon as the user types: the most common
        // case is Claude's "needs your permission" notification — the hook
        // fires `status=waiting` on the prompt, but Claude doesn't always
        // fire a follow-up status (`pre-tool-use` etc.) until the next tool
        // call, so the waiting pill lingers after the user has clearly
        // already responded. Any keystroke into the pane is unambiguous
        // proof the user is no longer being waited on; flip to working and
        // let the agent's next real status overwrite us. Working keeps the
        // visual breadcrumb "something is running" instead of dropping to
        // idle which would imply the agent is dormant.
        var sess = OwningSession(id);
        if (sess == null) return;
        var pane = FindPane(sess, id);
        if (pane != null && pane.AgentState == AgentState.Waiting)
        {
            pane.AgentState = AgentState.Working;
            pane.NotificationText = "";
            PushState();
        }
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

    private void OnSessionNew(JsonElement root)
    {
        var s = _store.AddNew();
        // Optional shell command line — when present (e.g. the page's
        // "new session with shell X", or the stability harness varying
        // shells) the session spawns that shell instead of the default.
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("shell", out var sh) &&
            sh.ValueKind == JsonValueKind.String)
        {
            var shell = sh.GetString();
            if (!string.IsNullOrWhiteSpace(shell)) s.Shell = shell;
        }
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
        s.IsAutoTitle = false;     // user committed a name — never auto-overwrite
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

    // User changed a preference from the page (Ctrl +/- font size). Persist
    // immediately so the value survives even a hard crash; no need to
    // re-push state since the page already applied the change locally.
    private void OnPrefsSet(JsonElement root)
    {
        var dirty = false;
        if (root.TryGetProperty("fontSize", out var fs) && fs.TryGetInt32(out var n))
        {
            // Clamp on the host side too — the page already clamps, but
            // an out-of-band IPC sender shouldn't be able to poke garbage
            // into Settings.json.
            var clamped = Math.Max(9, Math.Min(32, n));
            if (_settings.FontSize != clamped)
            {
                _settings.FontSize = clamped;
                dirty = true;
            }
        }
        if (dirty) _settings.Save();
    }

    // Page opened the settings dialog and wants current values + the list
    // of shells we can offer. Detected-shell enumeration touches the disk
    // / PATH so we don't ship it on every state push — only on request.
    private void OnSettingsRequest()
    {
        try
        {
            var shells = Shell.DetectedShells()
                .Select(s => new { name = s.Name, cmd = s.CommandLine })
                .ToArray();
            var payload = new
            {
                type = "settings.data",
                shells,
                defaultShell = _settings.DefaultShell,
                defaultCwd = _settings.DefaultCwd,
                defaultCwdResolved = _settings.ResolveDefaultCwd(),
                fontSize = _settings.FontSize,
            };
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch (Exception ex) { Log.Error("OnSettingsRequest", ex); }
    }

    // Page saved the settings dialog. Each field is optional — only
    // overwrite the keys present in the message. Shell/cwd take effect on
    // the next session spawn (lazy); fontSize re-pushes state so live
    // panes pick it up via the prefs ferry in PushState.
    private void OnSettingsSave(JsonElement root)
    {
        var dirty = false;
        var fontChanged = false;
        if (root.TryGetProperty("defaultShell", out var sh) && sh.ValueKind == JsonValueKind.String)
        {
            var v = sh.GetString() ?? "";
            if (_settings.DefaultShell != v) { _settings.DefaultShell = v; dirty = true; }
        }
        if (root.TryGetProperty("defaultCwd", out var cwd) && cwd.ValueKind == JsonValueKind.String)
        {
            var v = cwd.GetString() ?? "";
            if (_settings.DefaultCwd != v) { _settings.DefaultCwd = v; dirty = true; }
        }
        if (root.TryGetProperty("fontSize", out var fs) && fs.TryGetInt32(out var n))
        {
            var clamped = Math.Max(9, Math.Min(32, n));
            if (_settings.FontSize != clamped) { _settings.FontSize = clamped; dirty = true; fontChanged = true; }
        }
        if (dirty) _settings.Save();
        // Re-push so the font size propagates to live panes (no-op for
        // shell/cwd, which only matter at next spawn — but cheap).
        if (fontChanged) PushState();
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
        // When `url` is present the new leaf is a webview pane (iframe) —
        // the page renders an iframe for leaves whose Url is non-null
        // instead of an xterm. Otherwise the new leaf is a normal PTY
        // pane and the PTY spawns lazily on first pane.resize.
        var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
        // Pick a color not used by any other pane (across all sessions).
        // Falls back to round-robin once all six are taken. See
        // SessionStore.PickUnusedColor for the strategy.
        var newPane = new PaneNode
        {
            Url = string.IsNullOrEmpty(url) ? null : url,
            ColorIndex = _store.PickUnusedColor(),
        };
        var replacement = SplitImpl(sess.Root, id, orient, newPane);
        if (replacement == null) return;
        sess.Root = replacement;
        AutoName(sess.Root, 1);
        _activePaneId = newPane.Id;
        _store.Save();
        PushState();
    }

    // Open a URL in the OS default browser. Shell-execute via Process.Start
    // is the canonical Win32 way — the OS uses the user's configured
    // protocol handler (Edge / Chrome / Firefox). Validate scheme so we
    // can't be tricked into launching arbitrary `file://` or `cmd://`
    // schemes from terminal output.
    private void OnUrlOpen(JsonElement root)
    {
        if (!root.TryGetProperty("url", out var u)) return;
        var url = u.GetString();
        if (string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            Log.Info("url.open.rejected", $"scheme={uri.Scheme}");
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("url.open", ex);
        }
    }

    // Pane rename / recolor — both persist via SessionStore so feature
    // names ("simulator-fix", "kanban-integration") and color tags survive
    // restarts. AutoName() won't overwrite a user-set name because it only
    // fills in when Name is null/empty.
    private void OnPaneRename(JsonElement root)
    {
        if (!TryGuid(root, "paneId", out var id)) return;
        if (!root.TryGetProperty("name", out var n)) return;
        var name = (n.GetString() ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return;
        var sess = OwningSession(id);
        if (sess == null) return;
        var pane = AllLeaves(sess.Root).FirstOrDefault(p => p.Id == id);
        if (pane == null) return;
        pane.Name = name;
        pane.IsAutoName = false;    // user committed a name — never auto-overwrite
        _store.Save();
        PushState();
    }

    private void OnPaneRecolor(JsonElement root)
    {
        if (!TryGuid(root, "paneId", out var id)) return;
        if (!root.TryGetProperty("colorIndex", out var c)) return;
        if (!c.TryGetInt32(out var idx)) return;
        // Palette has 6 colors; wrap user input safely.
        idx = ((idx % 6) + 6) % 6;
        var sess = OwningSession(id);
        if (sess == null) return;
        var pane = AllLeaves(sess.Root).FirstOrDefault(p => p.Id == id);
        if (pane == null) return;
        pane.ColorIndex = idx;
        _store.Save();
        PushState();
    }

    // OSC 7 from the pane's shell — give us the cwd, we figure out the
    // branch. Cached per pane so we don't shell-out to git on every prompt
    // redraw (PowerShell fires OSC 7 on every Enter, even when cwd hasn't
    // changed). Branch update pushes state when it actually changes.
    private readonly Dictionary<Guid, string> _paneCwd = new();
    private void OnPaneCwd(JsonElement root)
    {
        if (!TryGuid(root, "paneId", out var id)) return;
        if (!root.TryGetProperty("cwd", out var c)) return;
        var cwd = c.GetString();
        if (string.IsNullOrEmpty(cwd)) return;
        if (_paneCwd.TryGetValue(id, out var prev) && prev == cwd) return;
        _paneCwd[id] = cwd;
        var sess = OwningSession(id);
        if (sess == null) return;
        var pane = AllLeaves(sess.Root).FirstOrDefault(p => p.Id == id);
        if (pane == null) return;
        // Resolve the branch off-thread — git can take 50–200ms on a big
        // repo and we don't want to stall the message pump. Also try to
        // auto-name the session by the repo basename (so "tab per repo"
        // works without manual rename) — but only when the title still
        // looks like our default ("main" / "session" / "session N").
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var branch  = await GitProc.BranchAsync(cwd!);
            var repoTop = sess.IsAutoTitle ? await GitProc.TopLevelAsync(cwd!) : null;
            await Dispatcher.InvokeAsync(() =>
            {
                var dirty = false;
                if (branch != null && pane.Branch != branch) { pane.Branch = branch; dirty = true; }
                if (!string.IsNullOrEmpty(repoTop) && sess.IsAutoTitle)
                {
                    var name = System.IO.Path.GetFileName(repoTop!.TrimEnd('/', '\\'));
                    if (!string.IsNullOrEmpty(name) && sess.Title != name)
                    {
                        sess.Title = name;
                        _store.Save();
                        dirty = true;
                    }
                }
                if (dirty) PushState();
            });
        });
    }

    // Git helpers moved to GitProc.cs — pure static, no MainWindow state.

    private UrlPaneController? _urlPaneCtrl;

    // Win32 message constants kept here for future use. We previously
    // hooked WM_ENTERSIZEMOVE/EXITSIZEMOVE to hide URL panes during drag
    // but the show timing race made the WebView2 reappear at a stale
    // position. Simpler approach: rely on SizeChanged → ui.urlpane.relayout
    // → page re-emits rect → host MoveTo. forceRefit() invalidates the
    // cache so the rect is always re-sent.
    private IntPtr MainWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        => IntPtr.Zero;

    // -------------------------------------------------------------------
    // URL-pane WebView2 overlay management.
    //
    // The webview-side UrlPane is a thin placeholder div that reports its
    // bounding rect on every layout change. We use that rect to position
    // a real WebView2 control on the WPF Canvas overlay so URL panes
    // aren't subject to iframe restrictions (X-Frame-Options, CSP) — they
    // get a full browser instance instead.
    //
    // Lifecycle:
    //   urlpane.layout (first time) → instantiate WebView2 + navigate
    //   urlpane.layout (subsequent)  → reposition + resize
    //   urlpane.dispose              → tear down
    //   session swap / pane close    → dispose all that are no longer in tree

    // URL-pane lifecycle is owned by UrlPaneController — see that file for
    // the SetParent + DIP→pixel math + WebView2 ownership. MainWindow only
    // forwards the two dispatch messages + the auto-title callback.

    private void OnUrlPaneLayout(JsonElement root)  => _urlPaneCtrl?.OnLayout(root);
    private void OnUrlPaneDispose(JsonElement root) => _urlPaneCtrl?.OnDispose(root);

    /// Rename a pane to the website's <title> — but only if the user
    /// hasn't already manually renamed it (IsAutoName guard).
    private void ApplyAutoTitle(Guid paneId, string title)
    {
        var sess = OwningSession(paneId);
        if (sess == null) return;
        var pane = AllLeaves(sess.Root).FirstOrDefault(p => p.Id == paneId);
        if (pane == null) return;
        if (!pane.IsAutoName) return;     // user committed a name
        // Trim absurdly long titles (some sites set 200+ char titles).
        if (title.Length > 60) title = title.Substring(0, 60).TrimEnd() + "…";
        if (pane.Name == title) return;
        pane.Name = title;
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
    //
    // FLAT-SPLIT behavior: when the new split is in the SAME direction as
    // the parent we descended through, we flatten — the parent absorbs the
    // newly-wrapped split's children directly. So splitting pane-2 to the
    // right inside an already-vertical split yields three flat siblings
    // (pane-1 | pane-2 | pane-3 each at 1fr) instead of nested
    // (pane-1 | (pane-2 | pane-3) with the new pane taking half of pane-2).
    // Same applies to horizontal splits ("down" inside an already-down
    // split). flex: 1 1 0 on .split children then gives even sizing for
    // any pane count.
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
            if (rep == null) continue;
            // Flatten: if the replacement is a split in the same direction
            // as us, splice its children into our children list at the
            // same index instead of nesting it.
            if (rep.Split == node.Split)
            {
                node.Children.RemoveAt(i);
                node.Children.InsertRange(i, rep.Children);
            }
            else
            {
                node.Children[i] = rep;
            }
            return node;
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
                // User prefs ferried with every state push. Cheap (two
                // ints in JSON) and means the page never has to ask. Used
                // by Workspace.applyPrefs to set the default size of new
                // panes after a split.
                prefs = new { fontSize = _settings.FontSize },
                sessions = _store.Sessions.Select(s =>
                {
                    // Aggregate per-pane state to the session row. Most-urgent
                    // wins: Waiting > Working > Done > Idle. The first pane
                    // with the winning state also lends its activity detail
                    // and notification (so the sidebar shows the one that
                    // wants attention).
                    var leaves = AllLeaves(s.Root).ToArray();
                    var aggState = AggregateState(leaves);
                    var attentionPane = leaves.FirstOrDefault(p => p.AgentState == aggState)
                                     ?? leaves.FirstOrDefault();
                    var anyNotify = leaves.FirstOrDefault(p => p.HasNotification);
                    var paneCount = leaves.Length;
                    var waitingCount = leaves.Count(p => p.AgentState == AgentState.Waiting);
                    var workingCount = leaves.Count(p => p.AgentState == AgentState.Working);
                    return new
                    {
                        id    = s.Id.ToString("D"),
                        title = s.Title,
                        shell = s.DisplayShell,
                        rootPane = ProjectPane(s.Root),
                        agentState = StateToString(aggState),
                        activityDetail = attentionPane?.ActivityDetail ?? "",
                        // Branch + ports aggregate by union; user typically
                        // has one branch per pane (one per worktree).
                        branch = leaves.Select(p => p.Branch).FirstOrDefault(b => !string.IsNullOrEmpty(b)) ?? "",
                        ports  = leaves.SelectMany(p => p.Ports).Distinct().ToArray(),
                        notification = anyNotify == null ? null : new
                        {
                            text  = anyNotify.NotificationText,
                            level = LevelToString(anyNotify.NotificationLevel),
                        },
                        // Pane breakdown so the sidebar can say "3 panes · 1 waiting".
                        paneCount,
                        waitingCount,
                        workingCount,
                    };
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
                colorIndex = node.ColorIndex,
                // Per-pane state — shows up in the pane header so each
                // pane's agent status is visible at a glance, no clicking
                // through the sidebar to figure out which one needs you.
                agentState = StateToString(node.AgentState),
                activityDetail = node.ActivityDetail,
                branch = node.Branch,
                ports  = node.Ports,
                /* Commits made since cc session-start (HEAD baseline). 0 when
                 * no session is active. Surfaces as "+N commits" chip in the
                 * pane header so the user can see at a glance how much work
                 * the agent has actually landed. */
                commitCount = node.CommitCount,
                notification = string.IsNullOrEmpty(node.NotificationText) ? null : new
                {
                    text  = node.NotificationText,
                    level = LevelToString(node.NotificationLevel),
                },
            };
        return new
        {
            kind = "split",
            orientation = node.Split == SplitOrientation.Horizontal ? "h" : "v",
            children = node.Children.Select(ProjectPane).ToArray(),
        };
    }

    /// Most-urgent state across panes. Drives the session row indicator.
    /// Order: Waiting > Working > Done > Idle.
    private static AgentState AggregateState(IEnumerable<PaneNode> leaves)
    {
        var seen = AgentState.Idle;
        foreach (var p in leaves)
        {
            if (p.AgentState == AgentState.Waiting) return AgentState.Waiting;
            if (p.AgentState == AgentState.Working) seen = AgentState.Working;
            else if (p.AgentState == AgentState.Done && seen != AgentState.Working) seen = AgentState.Done;
        }
        return seen;
    }
    private static string StateToString(AgentState s) => s switch
    {
        AgentState.Working => "working",
        AgentState.Waiting => "waiting",
        AgentState.Done    => "done",
        _                  => "idle",
    };
    private static string LevelToString(NotificationLevel l) => l switch
    {
        NotificationLevel.Success => "success",
        NotificationLevel.Warn    => "warn",
        NotificationLevel.Error   => "error",
        _                         => "info",
    };

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
                // the script doesn't have to look up its id from disk. An
                // optional --url makes the new leaf a URL (WebView2) pane,
                // so the stability harness can exercise that lifecycle.
                if (_activePaneId is Guid ap)
                {
                    var dir = root.TryGetProperty("dir", out var d) ? d.GetString() : "right";
                    var url = root.TryGetProperty("url", out var uu) ? uu.GetString() : null;
                    var urlPart = string.IsNullOrEmpty(url)
                        ? ""
                        : $",\"url\":{JsonSerializer.Serialize(url)}";
                    var fakeRoot = JsonDocument.Parse(
                        $"{{\"paneId\":\"{ap:D}\",\"dir\":\"{dir}\"{urlPart}}}").RootElement;
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
            case "prefs.set":
                // Mirror the page → host wire: cmux test passes flags as
                // strings, so parse fontSize defensively. OnPrefsSet then
                // does its own clamp + save.
                {
                    if (root.TryGetProperty("fontSize", out var fs) &&
                        int.TryParse(fs.GetString(), out var n))
                    {
                        var fakeRoot = JsonDocument.Parse($"{{\"fontSize\":{n}}}").RootElement;
                        OnPrefsSet(fakeRoot);
                    }
                }
                break;
            case "pane.simulate-input":
                // Synthesize a `pane.in` arrival for the active pane so
                // tests can drive OnPaneIn without a real keystroke and
                // without WebView2 input simulation. The point is to
                // exercise the side-effects of OnPaneIn (e.g. "clear stale
                // waiting") not the PTY write itself — but we ship the
                // bytes too so the path stays realistic.
                if (_activePaneId is Guid sap)
                {
                    var text = root.TryGetProperty("text", out var t) ? (t.GetString() ?? "x") : "x";
                    var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
                    var fakeRoot = JsonDocument.Parse(
                        $"{{\"paneId\":\"{sap:D}\",\"b64\":\"{b64}\"}}").RootElement;
                    OnPaneIn(fakeRoot);
                }
                break;
            case "ui.open-settings":
                Web.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"ui.open-settings\"}");
                break;
            case "settings.save":
                // Mirror the page → host settings.save wire so the harness
                // can exercise persistence without DOM interaction. cmux
                // test passes flags as strings; OnSettingsSave reads the
                // same property names. fontSize comes through as a string,
                // so re-wrap it as a number for OnSettingsSave's TryGetInt32.
                {
                    var shell = root.TryGetProperty("defaultShell", out var sv) ? sv.GetString() : null;
                    var cwd   = root.TryGetProperty("defaultCwd", out var cv) ? cv.GetString() : null;
                    var fsStr = root.TryGetProperty("fontSize", out var fv) ? fv.GetString() : null;
                    var sb = new System.Text.StringBuilder("{");
                    var parts = new List<string>();
                    if (shell != null) parts.Add($"\"defaultShell\":{JsonSerializer.Serialize(shell)}");
                    if (cwd != null)   parts.Add($"\"defaultCwd\":{JsonSerializer.Serialize(cwd)}");
                    if (fsStr != null && int.TryParse(fsStr, out var fsn)) parts.Add($"\"fontSize\":{fsn}");
                    sb.Append(string.Join(",", parts)).Append('}');
                    OnSettingsSave(JsonDocument.Parse(sb.ToString()).RootElement);
                }
                break;
            case "state.dump":
                // Dump the current per-pane state as a single Log.Info line
                // so tests can grep errors.log for assertions. Format is
                // intentionally machine-readable: STATE_DUMP{json}.
                {
                    var snap = _store.Sessions.Select(s => new
                    {
                        id = s.Id.ToString("D"),
                        active = _store.ActiveSessionId == s.Id,
                        panes = AllLeaves(s.Root).Select(p => new
                        {
                            id = p.Id.ToString("D"),
                            name = p.Name,
                            agentState = StateToString(p.AgentState),
                            notification = p.NotificationText,
                        }).ToArray(),
                    }).ToArray();
                    var prefs = new
                    {
                        fontSize = _settings.FontSize,
                        defaultShell = _settings.DefaultShell,
                        defaultCwd = _settings.DefaultCwd,
                    };
                    var dump = new { sessions = snap, prefs };
                    Log.Info("StateDump", "STATE_DUMP" + JsonSerializer.Serialize(dump));
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
