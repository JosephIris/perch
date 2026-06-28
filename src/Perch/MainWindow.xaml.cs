using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;

namespace Perch;

public partial class MainWindow : FluentWindow
{
    private const string VirtualHost = "perch.local";

    private readonly string _webRoot;
    private readonly Settings _settings;
    private readonly SessionStore _store;

    // One ConPty per leaf pane. Lifetime: created on first activation of a
    // session (lazy spawn) so closed-but-persisted sessions don't fork a
    // shell at startup; disposed when the pane is removed or the session
    // closes.
    private readonly Dictionary<Guid, ConPty> _ptys = new();

    // Per-pane named-pipe server listening on \\.\pipe\perch\<paneId>.
    // Agents inside the pane talk to us via the perch CLI ("perch status
    // working") which dials this pipe. Lifecycle mirrors _ptys exactly.
    private readonly Dictionary<Guid, PerchIpcServer> _paneIpc = new();

    // Bytes-received-since-launch per pane id. Surface for `pty.snapshot`
    // and the test harness; the page doesn't see this.
    private readonly Dictionary<Guid, long> _ptyBytesReceived = new();

    // Last PTY-output timestamp (Stopwatch ticks) per pane id, written from
    // the ConPty read thread and read by the idle watchdog on the UI thread —
    // hence ConcurrentDictionary. Drives the Working→Done demotion: an agent
    // pane that's actually working redraws its spinner ~1/sec, so a few
    // seconds of total silence reliably means the turn ended (and recovers us
    // when a Stop hook was dropped). See OnIdleWatchdogTick.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> _ptyLastOutputTicks = new();

    // How long a Working pane must be output-silent before the watchdog treats
    // its turn as finished and demotes it to Done. Long enough to ride out
    // brief pauses between an agent's spinner frames, short enough that a
    // dropped Stop self-heals quickly.
    private static readonly long IdleDemoteTicks = (long)(8.0 * System.Diagnostics.Stopwatch.Frequency);

    private System.Windows.Threading.DispatcherTimer? _idleWatchdog;

    private ControlIpcServer? _control;

    // Watches the OS clipboard so we can push its text to the page (see
    // SyncClipboardToWeb), making right-click paste synchronous instead of a
    // stall-prone navigator.clipboard.readText() in the webview.
    private ClipboardWatcher? _clipWatch;

    // Above this, we don't pre-cache clipboard text to the page — a giant
    // cross-app copy would be ferried on every clipboard change for a paste
    // that may never happen. The page falls back to readText() for the rare
    // oversize case. UTF-16 chars; ~2 MB.
    private const int MaxCachedClipboardChars = 1_000_000;

    // ---- Agent-session resume (claude --resume <id>) ---------------------
    // Panes armed to inject `claude --resume <id>` on their NEXT spawn. Drained
    // one-shot in SpawnPty so a later manual re-split never auto-launches an
    // agent. Populated when the user accepts the launch prompt or restores a
    // closed session.
    private readonly HashSet<Guid> _armedResumePanes = new();
    // True between launch and the user's answer to the one-time "Resume N
    // Claude sessions?" prompt. While pending, a resumable pane's lazy spawn is
    // parked in _deferredSpawns so the prompt actually gates the first agent
    // launch instead of racing it.
    private bool _resumeDecisionPending;
    private readonly Dictionary<Guid, (int cols, int rows)> _deferredSpawns = new();
    // Panes shown in the restore-progress lightbox → whether the pane has
    // reported "alive again" (its resumed session-start hook fired). Empty when
    // no restore is in flight. _restoreTimeout force-completes a batch whose
    // panes never come back so the lightbox can't hang.
    private readonly Dictionary<Guid, bool> _restoreBatch = new();
    private System.Windows.Threading.DispatcherTimer? _restoreTimeout;

    public MainWindow()
    {
        InitializeComponent();
        _webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        _settings = Settings.Load();
        _store = SessionStore.Load();
        EnsurePaneNames();
        // Persist immediately on first launch so external tools (the perch
        // CLI, test harnesses) can read pane ids and pipe paths from disk
        // before the user does anything.
        _store.Save();
        // Arm agent-session resume: if any persisted pane carries a saved
        // Claude session id and the user hasn't disabled it, hold those panes'
        // spawns until the one-time prompt (sent from OnPageReady) is answered.
        // Set here — before the page can send its first pane.resize — so the
        // deferral in OnPaneResize is in effect from the very first measure.
        if (_settings.ResumeAgentsOnLaunch && AllResumablePanes().Any())
            _resumeDecisionPending = true;

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
            // Keep the page's clipboard cache fresh: on every clipboard change
            // while we're foreground (covers copies made inside Perch) and on
            // window activation (covers copies made in another app before
            // switching back). The initial sync happens at page-ready.
            _clipWatch = new ClipboardWatcher(this);
            _clipWatch.ClipboardChanged += SyncClipboardToWeb;
            _clipWatch.Attach();
            Activated += (_, _) => SyncClipboardToWeb();

            if (ControlIpcServer.IsEnabled)
            {
                _control = new ControlIpcServer(Dispatcher, OnControlVerb);
                _control.Start();
            }
            // Idle watchdog: 1Hz sweep that demotes output-silent Working panes
            // to Done (and re-promotes its own guesses when output resumes), so
            // a missed Stop hook can't pin a pane on "working" forever.
            _idleWatchdog = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _idleWatchdog.Tick += OnIdleWatchdogTick;
            _idleWatchdog.Start();
        };
        Closed += (_, _) =>
        {
            _idleWatchdog?.Stop();
            _clipWatch?.Dispose();
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
        // PaneNode.Name doubles as the human-readable address for `perch
        // focus/send/open`. Auto-assign pane-N for leaves missing a name.
        foreach (var s in _store.Sessions) AutoName(s.Root);
    }
    // Next "pane-N" = highest existing N + 1, NOT a positional index. Positional
    // numbering collided after a close+split: close pane-1, the split collapses
    // and the survivor keeps "pane-2", then the next split's walker counts the
    // survivor as position 1 and assigns the new pane "pane-2" too — two panes,
    // same name. Scanning for the max keeps every auto name unique.
    private static void AutoName(PaneNode root)
    {
        int max = 0;
        foreach (var leaf in AllLeaves(root))
        {
            var m = Regex.Match(leaf.Name ?? "", @"^pane-(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var k) && k > max) max = k;
        }
        foreach (var leaf in AllLeaves(root))
            if (string.IsNullOrEmpty(leaf.Name)) leaf.Name = $"pane-{++max}";
    }

    // ---- WebView2 init ----------------------------------------------------

    private async Task InitWebViewAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                AppPaths.DataRoot,
                "perch", "WebView2");
            Directory.CreateDirectory(userDataFolder);

            // In test mode (PERCH_ENABLE_TEST_IPC) disable Chromium's
            // background/occlusion throttling. Harnesses park the window
            // off-screen so churn stays off the user's display; without these
            // flags Chromium would throttle rAF/timers (and pause rendering on
            // occlusion), which both stops the lazy PTY spawn and invalidates
            // any renderer-performance measurement. No effect on a normal run.
            CoreWebView2EnvironmentOptions? options = null;
            if (ControlIpcServer.IsEnabled)
                options = new CoreWebView2EnvironmentOptions(additionalBrowserArguments:
                    "--disable-renderer-backgrounding " +
                    "--disable-background-timer-throttling " +
                    "--disable-backgrounding-occluded-windows " +
                    "--disable-features=CalculateNativeWinOcclusion");

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: options);
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
                "Perch", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
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
                case "pane.ack":        OnPaneAck(root); break;
                case "pane.resize":     OnPaneResize(root); break;
                case "render.pong":     OnRenderPong(root); break;
                case "pane.split":      OnPaneSplit(root); break;
                case "pane.close":      OnPaneClose(root); break;
                case "pane.resizeSplit": OnPaneResizeSplit(root); break;
                case "pane.move":       OnPaneMove(root); break;
                case "pane.moveDir":    OnPaneMoveDir(root); break;
                case "pane.rename":     OnPaneRename(root); break;
                case "pane.recolor":    OnPaneRecolor(root); break;
                case "pane.cwd":        OnPaneCwd(root); break;
                case "urlpane.layout":  OnUrlPaneLayout(root); break;
                case "urlpane.dispose": OnUrlPaneDispose(root); break;
                case "session.new":     OnSessionNew(root); break;
                case "session.select":  OnSessionSelect(root); break;
                case "session.rename":  OnSessionRename(root); break;
                case "session.close":   OnSessionClose(root); break;
                case "session.restore": OnSessionRestore(root); break;
                case "session.purge":   OnSessionPurge(root); break;
                case "resume.decision": OnResumeDecision(root); break;
                case "pane.focus":      OnPaneFocus(root); break;
                case "url.open":        OnUrlOpen(root); break;
                case "prefs.set":       OnPrefsSet(root); break;
                case "settings.request": OnSettingsRequest(); break;
                case "settings.save":    OnSettingsSave(root); break;
                case "onboarding.seen":  OnOnboardingSeen(); break;
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
        // Seed the page's clipboard cache now that it can receive messages, so
        // the first right-click paste is synchronous without waiting for a
        // clipboard change or window re-activation.
        SyncClipboardToWeb();
        // If we held back any resumable panes (see the constructor), ask the
        // user once whether to reopen those Claude sessions. The answer
        // (resume.decision) releases the parked spawns.
        if (_resumeDecisionPending) PostResumePrompt();
    }

    /// Every (session, leaf) pair that can ACTUALLY `claude --resume` — carries
    /// a saved session id AND has a transcript on disk for it. The transcript
    /// check is what stops us from firing `claude --resume <id>` for a session
    /// Claude never persisted (started then closed before a turn), which errors
    /// "No conversation found" and drops a red line in the pane. Spans all
    /// sessions so the launch prompt's count reflects everything resumable.
    private IEnumerable<(Session sess, PaneNode pane)> AllResumablePanes() =>
        _store.Sessions.SelectMany(s => AllLeaves(s.Root).Select(p => (sess: s, pane: p)))
            .Where(t => !string.IsNullOrEmpty(t.pane.ClaudeSessionId)
                        && TranscriptExists(t.pane.ClaudeSessionId!, ResolvePaneCwd(t.sess, t.pane)));

    /// The cwd a pane spawns in: its own persisted cwd, then the session cwd,
    /// then the configured default. Single source so the resume pre-flight and
    /// SpawnPty agree on where the agent runs.
    private string ResolvePaneCwd(Session sess, PaneNode pane) =>
        FirstExistingDir(pane.Cwd, sess.Cwd) ?? _settings.ResolveDefaultCwd();

    /// Best-effort check that Claude has a saved transcript for this session id
    /// under the given cwd, so we only `--resume` something that exists.
    /// Claude stores transcripts at
    /// <c>~/.claude/projects/&lt;sanitized-cwd&gt;/&lt;id&gt;.jsonl</c>. We try the
    /// cwd-scoped path first, then fall back to matching the file anywhere under
    /// projects (the sanitization rule can drift across Claude versions). On ANY
    /// uncertainty — projects dir missing, IO error — we return true so the
    /// check never blocks a genuine resume; it only suppresses the clearly-absent
    /// case.
    private static bool TranscriptExists(string sessionId, string cwd)
    {
        try
        {
            // Claude's config root is ~/.claude unless CLAUDE_CONFIG_DIR overrides
            // it — honor the same override so we look where Claude actually wrote.
            var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            var baseDir = string.IsNullOrWhiteSpace(configDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                : configDir;
            var projects = Path.Combine(baseDir, "projects");
            if (!Directory.Exists(projects)) return true; // unknown layout — don't block
            var scoped = Path.Combine(projects, SanitizeCwd(cwd), sessionId + ".jsonl");
            if (File.Exists(scoped)) return true;
            return Directory.EnumerateFiles(projects, sessionId + ".jsonl", SearchOption.AllDirectories).Any();
        }
        catch { return true; }
    }

    /// Claude's project-dir key: path separators and the drive colon become '-'
    /// (e.g. C:\Users\josep\dev-projects\cmux-win → C--Users-josep-dev-projects-cmux-win).
    private static string SanitizeCwd(string cwd)
    {
        var sb = new System.Text.StringBuilder(cwd.Length);
        foreach (var ch in cwd) sb.Append(ch is '\\' or '/' or ':' ? '-' : ch);
        return sb.ToString();
    }

    /// One-time "Resume N Claude sessions?" prompt. The page renders the dialog
    /// and replies with resume.decision {accept}.
    private void PostResumePrompt()
    {
        var resumable = AllResumablePanes().ToList();
        var sessionCount = resumable.Select(t => t.sess.Id).Distinct().Count();
        var payload = new
        {
            type = "resume.prompt",
            paneCount = resumable.Count,
            sessionCount,
        };
        try { Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload)); }
        catch (Exception ex) { Log.Error("PostResumePrompt", ex); }
    }

    /// User answered the launch resume prompt. Accept → arm every resumable
    /// pane and open the progress lightbox for the ones we deferred (the
    /// visible session's). Either way, release every parked spawn.
    private void OnResumeDecision(JsonElement root)
    {
        var accept = root.TryGetProperty("accept", out var a) &&
                     (a.ValueKind == JsonValueKind.True ||
                      (a.ValueKind == JsonValueKind.String && a.GetString() == "true"));
        _resumeDecisionPending = false;
        if (accept)
        {
            foreach (var (_, pane) in AllResumablePanes())
                _armedResumePanes.Add(pane.Id);
            // The lightbox tracks the panes we're bringing back right now.
            BeginRestoreProgress(_deferredSpawns.Keys.ToList());
        }
        // Release the parked spawns — resuming (armed) or bare (not armed).
        foreach (var kv in _deferredSpawns.ToList())
        {
            var sess = OwningSession(kv.Key);
            var pane = sess == null ? null : AllLeaves(sess.Root).FirstOrDefault(p => p.Id == kv.Key);
            if (sess != null && pane != null)
                SpawnPty(sess, pane, kv.Value.cols, kv.Value.rows);
        }
        _deferredSpawns.Clear();
    }

    // ---- Restore-progress lightbox (host side) ---------------------------
    // The page shows a sleek per-pane progress modal while resumed agents come
    // back up. Host drives it: restore.begin lists the panes, restore.progress
    // flips each row, restore.done closes it. "Ready" = the pane's resumed
    // session-start hook fired (OnAgentSession). A timer force-completes a
    // batch whose panes never report back so the modal can't hang.

    /// Open the lightbox for the given panes (only those with a saved session
    /// id — the rest aren't resuming and have nothing to show).
    private void BeginRestoreProgress(List<Guid> paneIds)
    {
        CompleteRestoreBatch(force: true);  // close any prior batch first
        var panes = new List<object>();
        foreach (var id in paneIds)
        {
            var sess = OwningSession(id);
            var pane = sess == null ? null : AllLeaves(sess.Root).FirstOrDefault(p => p.Id == id);
            if (pane == null || string.IsNullOrEmpty(pane.ClaudeSessionId)) continue;
            _restoreBatch[id] = false;
            panes.Add(new
            {
                paneId = id.ToString("D"),
                name = pane.Name ?? "pane",
                sessionTitle = sess!.Title,
            });
        }
        if (panes.Count == 0) return;
        try
        {
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(
                new { type = "restore.begin", panes = panes.ToArray() }));
        }
        catch (Exception ex) { Log.Error("BeginRestoreProgress", ex); }
        // Safety net: a pane that never re-fires session-start (resume failed,
        // stale id, plain shell) shouldn't pin the lightbox open.
        _restoreTimeout?.Stop();
        _restoreTimeout = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(12),
        };
        _restoreTimeout.Tick += (_, __) => CompleteRestoreBatch(force: true);
        _restoreTimeout.Start();
    }

    /// SpawnPty just injected `claude --resume` for this pane — flip its row to
    /// the active "resuming" spinner (no-op if it isn't in the batch).
    private void NoteRestorePaneResuming(Guid paneId)
    {
        if (_restoreBatch.ContainsKey(paneId)) PostRestoreProgress(paneId, "resuming");
    }

    /// The pane's resumed agent reported in (session-start hook). Mark its row
    /// done; when the whole batch is back, close the lightbox.
    private void MarkRestorePaneReady(Guid paneId)
    {
        if (!_restoreBatch.TryGetValue(paneId, out var done) || done) return;
        _restoreBatch[paneId] = true;
        PostRestoreProgress(paneId, "ready");
        if (_restoreBatch.Values.All(v => v)) CompleteRestoreBatch(force: false);
    }

    private void CompleteRestoreBatch(bool force)
    {
        _restoreTimeout?.Stop();
        _restoreTimeout = null;
        if (_restoreBatch.Count == 0) return;
        if (force)
            foreach (var id in _restoreBatch.Keys.ToList())
                if (!_restoreBatch[id]) PostRestoreProgress(id, "ready");
        _restoreBatch.Clear();
        try { Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "restore.done" })); }
        catch (Exception ex) { Log.Error("CompleteRestoreBatch", ex); }
    }

    private void PostRestoreProgress(Guid paneId, string state)
    {
        try
        {
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(
                new { type = "restore.progress", paneId = paneId.ToString("D"), state }));
        }
        catch (Exception ex) { Log.Error("PostRestoreProgress", ex); }
    }

    /// Read the OS clipboard and push its text to the page so right-click paste
    /// reads it synchronously instead of awaiting navigator.clipboard.readText()
    /// (see clipboard.ts). Always runs on the UI thread — the callers are the
    /// clipboard-change hook, window Activated, and page-ready — where
    /// System.Windows.Clipboard is usable. Oversize text is dropped to "" so a
    /// huge cross-app copy isn't ferried on every clipboard change; the page
    /// falls back to readText() for that rare case.
    private void SyncClipboardToWeb()
    {
        string text;
        try { text = System.Windows.Clipboard.GetText(); }
        catch (Exception ex) { Log.Error("Clipboard.Read", ex); return; }
        if (text.Length > MaxCachedClipboardChars) text = "";

        try
        {
            var payload = JsonSerializer.Serialize(new { type = "clipboard.text", text });
            Web.CoreWebView2?.PostWebMessageAsJson(payload);
        }
        catch (Exception ex) { Log.Error("Clipboard.Push", ex); }
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

    /// First of the candidate paths that names an existing directory, or null.
    /// Used to pick a pane's spawn cwd: per-pane cwd, then session cwd, then
    /// (caller) the configured default. A stale path (worktree deleted, drive
    /// unmounted) is skipped rather than failing the spawn.
    private static string? FirstExistingDir(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            try { if (Directory.Exists(c)) return c; } catch { }
        }
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
            // Per-pane cwd wins (the dir THIS pane last cd'd to, persisted via
            // OSC 7), then the session-level cwd, then the configured default.
            // This is what makes a restored pane reopen where the user left it.
            var cwd = ResolvePaneCwd(sess, pane);
            // Agent-session resume: if this pane is armed (the user accepted the
            // launch prompt or restored a closed session) and carries a saved
            // Claude session id, launch straight back into the conversation.
            // Drained one-shot so a later manual re-split of this pane never
            // auto-relaunches the agent.
            string? initialCommand = null;
            if (_armedResumePanes.Remove(pane.Id) && !string.IsNullOrEmpty(pane.ClaudeSessionId))
            {
                initialCommand = $"claude --resume {pane.ClaudeSessionId}";
                NoteRestorePaneResuming(pane.Id);
            }
            // Shell.BuildStartupCommandLine injects PERCH_PIPE / PERCH_PANE_ID
            // env vars per-pane so agents inside the shell can call back
            // into our IPC layer (stage 4 reactivates that pipe).
            var startCmd = Shell.BuildStartupCommandLine(baseShell, cwd, pane.Id, initialCommand);
            var pty = ConPty.Start(startCmd, cols: cols, rows: rows, cwd: cwd);
            var paneId = pane.Id;
            pty.OutputReceived += (_, bytes) =>
            {
                lock (_ptyBytesReceived)
                    _ptyBytesReceived[paneId] = (_ptyBytesReceived.TryGetValue(paneId, out var n) ? n : 0) + bytes.Length;
                _ptyLastOutputTicks[paneId] = System.Diagnostics.Stopwatch.GetTimestamp();
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

            // Agent IPC: per-pane named pipe \\.\pipe\perch\<paneId>. The
            // pane's shell inherits PERCH_PIPE pointing here, so `perch
            // status working` from inside the shell lands at OnStatus.
            var ipc = new PerchIpcServer(paneId, Dispatcher);
            ipc.OnStatus += msg => OnAgentStatus(sess, paneId, msg);
            ipc.OnNotify += msg => OnAgentNotify(sess, paneId, msg);
            ipc.OnMeta   += msg => OnAgentMeta(sess, paneId, msg);
            ipc.OnGitBaseline += msg => OnGitBaseline(sess, paneId, msg);
            ipc.OnTitle  += msg => OnAgentTitle(sess, paneId, msg);
            ipc.OnNameReset += msg => OnNameReset(sess, paneId, msg);
            ipc.OnAgent  += msg => OnAgentType(sess, paneId, msg);
            ipc.OnSession += msg => OnAgentSession(sess, paneId, msg);
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
        _ptyLastOutputTicks.TryRemove(paneId, out _);
    }

    // ---- Agent IPC handlers (perch status / notify / meta) ----------------

    private static AgentState ParseAgentState(string? s) => s switch
    {
        "working"    => AgentState.Working,
        "done"       => AgentState.Done,
        "waiting"    => AgentState.Waiting,
        "permission" => AgentState.Permission,
        _            => AgentState.Idle,
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
        var newState  = ParseAgentState(msg.State);
        var newDetail = msg.Detail ?? "";
        // Coalesce no-op repeats. PostToolUse fires once per tool — many/sec
        // during agentic work — and almost all are working→working with no
        // detail change. If the authoritative state and detail are unchanged
        // (and the pane isn't a watchdog guess we'd want to confirm), there's
        // nothing to repaint and no commit count to refresh, so bail before the
        // git + PushState cost. The real permission→working edge still passes.
        if (newState == prev && newDetail == pane.ActivityDetail && !pane.StateInferred)
            return;
        pane.AgentState = newState;
        // Authoritative: an agent hook (Stop, prompt-submit, notification…)
        // is ground truth, so clear the watchdog's "inferred" mark. This is
        // what stops a real Stop-hook "done" from being re-promoted to
        // "working" by later background output.
        pane.StateInferred = false;
        pane.ActivityDetail = newDetail;
        // Turn-start clock for "working · 2m": stamp when a pane ENTERS working
        // from a non-working state; clear whenever it leaves. A working→working
        // edge (a new tool mid-turn) keeps the original start.
        if (newState == AgentState.Working)
        {
            if (prev != AgentState.Working)
                pane.TurnStartUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        else
        {
            pane.TurnStartUnixMs = 0;
        }
        // Turn-end clock for "finished · 2m ago": stamp the moment a pane ENTERS
        // Done from any other state. The page ticks relative-ago against it on
        // done rows, so the "your move" age stays live between pushes. Mirror of
        // the turn-start stamp above; the watchdog Working→Done path stamps too.
        if (newState == AgentState.Done && prev != AgentState.Done)
            pane.DoneAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Attention nudge: any transition INTO an attention state (waiting
        // for feedback, or blocked on permission) flashes the taskbar (only
        // when our window isn't already foreground). One place to raise the
        // signal so it works for both Claude-via-hooks and any other agent
        // calling `perch status waiting|permission` directly.
        static bool IsAttention(AgentState st) =>
            st is AgentState.Waiting or AgentState.Permission;
        if (!IsAttention(prev) && IsAttention(pane.AgentState))
            FlashAttention();                                    // loud: blocked / wants feedback
        else if (prev != AgentState.Done && pane.AgentState == AgentState.Done)
            FlashDoneGentle();                                   // calm: turn just finished
        // Refresh the cc-session git signals (commits / diff size / unpushed)
        // on every state change. Cheap if no baseline is set; otherwise a few
        // concurrent plumbing commands off-thread.
        _ = RefreshGitStatsAsync(pane);
        PushState();
    }

    // Idle watchdog tick (1Hz, UI thread). Agent state is otherwise purely
    // edge-triggered off hooks, so a single dropped Stop pins a pane on
    // "working" forever. This reconciles from a level signal — PTY output
    // silence — to make "working" non-terminal:
    //   • Working + silent ≥ threshold  → Done (marked inferred). A working
    //     agent redraws its spinner ~1/sec, so sustained silence means the
    //     turn actually ended.
    //   • Done(inferred) + output resumed → back to Working. Covers the false
    //     positive where a long, silent tool call (a quiet build) looked done;
    //     when it prints again we walk it back. A real Stop-hook Done is NOT
    //     inferred, so genuine turn-ends are never re-promoted by stray output.
    // Only Working/Done(inferred) panes are touched — Idle shells, Waiting and
    // Permission are left exactly as the agent reported them.
    private void OnIdleWatchdogTick(object? sender, EventArgs e)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var changed = false;
        foreach (var sess in _store.Sessions)
        {
            foreach (var pane in AllLeaves(sess.Root))
            {
                // No output seen yet (just spawned) → treat as not-silent so we
                // don't demote a pane that hasn't had a chance to draw.
                var silent = _ptyLastOutputTicks.TryGetValue(pane.Id, out var last)
                             && (now - last) >= IdleDemoteTicks;

                if (pane.AgentState == AgentState.Working && silent)
                {
                    pane.AgentState = AgentState.Done;
                    pane.StateInferred = true;
                    pane.TurnStartUnixMs = 0;            // left working → no elapsed
                    pane.DoneAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();  // turn-end clock for "finished · Xm ago"
                    changed = true;
                    Log.Info("IdleWatchdog", $"pane={pane.Id:N} working->done (output-silent)");
                }
                else if (pane.AgentState == AgentState.Done && pane.StateInferred && !silent)
                {
                    pane.AgentState = AgentState.Working;
                    // Stays inferred — it's still a watchdog guess until a hook
                    // says otherwise. Restart the turn clock for the new spell.
                    pane.TurnStartUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    changed = true;
                    Log.Info("IdleWatchdog", $"pane={pane.Id:N} done->working (output resumed)");
                }
            }
        }
        if (changed) PushState();
    }

    // Auto-name a terminal pane from the agent's first prompt — "capture
    // what's happening" from the content of the first message. The FIRST
    // prompt of each Claude session wins: we name the pane then drop
    // AllowAutoName so later prompts in the same session don't churn the
    // label. A new session (relaunch / `/clear`) re-arms AllowAutoName via
    // OnNameReset, so the new first message re-titles. A user double-click
    // rename sets IsUserNamed and locks the label permanently.
    private void OnAgentTitle(Session sess, Guid paneId, TitleMessage msg)
    {
        var pane = FindPane(sess, paneId);
        if (pane == null) return;
        if (pane.IsWebView) return;        // URL panes name from <title>
        if (pane.IsUserNamed) return;      // user committed a name — never touch
        if (!pane.AllowAutoName) return;   // already named this session
        var name = CleanPaneTitle(msg.Text);
        if (string.IsNullOrEmpty(name)) return;
        // Keep the full prompt for the header hover tooltip even when the
        // label is a 40-char cut of it.
        pane.NamePrompt = msg.Text?.Trim();
        pane.AllowAutoName = false;        // first message of this session defines it
        if (pane.Name != name)
        {
            pane.Name = name;
        }
        _store.Save();
        PushState();
    }

    // Agent type for the pane (Claude Code / codex / shell). Sent by the
    // agent's session-start hook; "" on session-end. Drives the header badge.
    private void OnAgentType(Session sess, Guid paneId, AgentMessage msg)
    {
        var pane = FindPane(sess, paneId);
        if (pane == null) return;
        var next = msg.Name ?? "";
        if (pane.AgentType == next) return;
        pane.AgentType = next;
        PushState();
    }

    // Claude reported its session id (session-start hook). Persist it on the
    // pane so a relaunch can `claude --resume <id>`. Overwrite on every
    // session-start (the latest conversation is the one worth resuming); never
    // cleared on session-end. If this pane is mid-restore (we just injected a
    // resume command and are waiting for claude to come back up), this hook
    // firing is the authoritative "it's alive again" signal for the progress
    // lightbox.
    private void OnAgentSession(Session sess, Guid paneId, SessionMessage msg)
    {
        var pane = FindPane(sess, paneId);
        if (pane == null) return;
        var id = string.IsNullOrWhiteSpace(msg.Id) ? null : msg.Id;
        if (id != null && pane.ClaudeSessionId != id)
        {
            pane.ClaudeSessionId = id;
            _store.Save();
        }
        MarkRestorePaneReady(paneId);
    }

    // New Claude session in a terminal pane (fresh launch after ctrl+c twice,
    // or `/clear`) re-arms auto-naming so the next first prompt re-titles the
    // pane to the new task. We don't wipe the current label here — it stays
    // until the next prompt replaces it. Skipped for user-named panes and for
    // "resume" (a resumed session keeps its established label).
    private void OnNameReset(Session sess, Guid paneId, NameResetMessage msg)
    {
        var pane = FindPane(sess, paneId);
        if (pane == null) return;
        if (pane.IsWebView) return;
        if (pane.IsUserNamed) return;
        if (string.Equals(msg.Source, "resume", StringComparison.OrdinalIgnoreCase)) return;
        if (pane.AllowAutoName) return;    // already armed; nothing to do
        pane.AllowAutoName = true;
        _store.Save();
    }

    // Normalize a free-text prompt into a short pane label: collapse every
    // whitespace run (newlines, tabs, repeats) to a single space, trim, and
    // cap at a tab-sized length with an ellipsis.
    private static string CleanPaneTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = Regex.Replace(raw.Trim(), @"\s+", " ");
        const int max = 40;
        if (s.Length > max) s = s.Substring(0, max).TrimEnd() + "…";
        return s;
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
            pane.LinesAdded = pane.LinesDeleted = pane.FilesChanged = pane.Ahead = 0;
            PushState();
            return;
        }
        _ = RefreshGitStatsAsync(pane);
    }

    private async System.Threading.Tasks.Task RefreshGitStatsAsync(PaneNode pane)
    {
        if (string.IsNullOrEmpty(pane.CommitBaseline)) return;
        if (!_paneCwd.TryGetValue(pane.Id, out var cwd) || string.IsNullOrEmpty(cwd)) return;
        // Run the git queries concurrently off the UI thread — they're
        // independent and each is a fast plumbing command.
        var countT = GitProc.CommitsSinceAsync(pane.CommitBaseline, cwd);
        var diffT  = GitProc.DiffStatsAsync(pane.CommitBaseline, cwd);
        var aheadT = GitProc.AheadAsync(cwd);
        await System.Threading.Tasks.Task.WhenAll(countT, diffT, aheadT);
        var count = await countT;
        var diff  = await diffT;
        var ahead = await aheadT;
        await Dispatcher.InvokeAsync(() =>
        {
            var changed = false;
            if (count is int n && pane.CommitCount != n) { pane.CommitCount = n; changed = true; }
            if (diff is (int files, int added, int deleted))
            {
                if (pane.FilesChanged != files)  { pane.FilesChanged = files;  changed = true; }
                if (pane.LinesAdded   != added)  { pane.LinesAdded   = added;  changed = true; }
                if (pane.LinesDeleted != deleted){ pane.LinesDeleted = deleted; changed = true; }
            }
            if (ahead is int a && pane.Ahead != a) { pane.Ahead = a; changed = true; }
            if (changed) PushState();
        });
    }

    private void OnAgentNotify(Session sess, Guid paneId, NotifyMessage msg)
    {
        var pane = FindPane(sess, paneId);
        if (pane == null) return;
        pane.NotificationText = msg.Text ?? "";
        pane.NotificationLevel = ParseLevel(msg.Level);
        PushState();
        PostToast(msg.Text ?? "", msg.Level, paneId);
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
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FLASHWINFO {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }
    private const uint FLASHW_TRAY = 2;           // taskbar button only (no caption)
    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;     // stop flashing when foreground

    // Loud: agent is blocked on you / wants feedback. Caption + taskbar, flashing
    // until the window is foregrounded.
    private void FlashAttention() => Flash(FLASHW_ALL | FLASHW_TIMERNOFG, count: 5);

    // Calm: a turn just finished (→ Done). One brief taskbar-button blink — a
    // glance-worthy "an agent freed up" ping without the nagging caption flash
    // of a real attention state. Skipped when we're already foreground (there's
    // nothing to draw the eye back to). Only fired from the authoritative Stop
    // hook, never the idle watchdog's inferred Done, so a silent build that
    // momentarily looks done doesn't ping you.
    private void FlashDoneGentle()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || GetForegroundWindow() == hwnd) return;
        Flash(FLASHW_TRAY, count: 1);
    }

    private void Flash(uint flags, uint count)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var fi = new FLASHWINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = flags,
                uCount = count,
                dwTimeout = 0,
            };
            FlashWindowEx(ref fi);
        }
        catch (Exception ex) { Log.Error("Flash", ex); }
    }

    private void PostToast(string text, string? level, Guid paneId)
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
                    // Which pane fired it — the page anchors the toast to that
                    // pane's bottom-center (falls back to window-centered when
                    // the pane isn't in the visible session). "D" format to
                    // match the leaf paneIds sent in state / pane.out.
                    paneId = paneId.ToString("D"),
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

        // Waiting / Permission are deliberately STICKY across keystrokes.
        // These states mean "the agent needs you", and they must persist
        // until the agent reports ACTUAL progress — a real `working` status
        // from the prompt-submit / pre-tool-use hooks (OnAgentStatus). The
        // old behavior flipped Waiting/Permission → Working on the first
        // keypress, which meant a pane could lose its attention marker the
        // instant you started typing (even an unrelated command), so a pane
        // that still needed you would quietly drop off the radar and you'd
        // miss it. Routine input/output no longer clears the attention state;
        // only the agent's own next-turn signal does.
    }

    // The page acks each xterm write once it's drained; we shrink that
    // pane's PTY backpressure backlog so the reader can resume. See the
    // flow-control block in ConPty for why this exists.
    private void OnPaneAck(JsonElement root)
    {
        if (!TryGuid(root, "paneId", out var id)) return;
        if (!root.TryGetProperty("bytes", out var b) || !b.TryGetInt64(out var n)) return;
        if (_ptys.TryGetValue(id, out var pty)) pty.Ack(n);
    }

    // Renderer-responsiveness probe (test-only). The control pipe fires
    // render.ping; we round-trip it through the page's main thread and log
    // the latency. Under the old fire-and-forget output path a flooded
    // renderer makes this round-trip take seconds (the same thread that
    // would process a keystroke is buried in the write backlog); with flow
    // control it stays in the low-ms range. See scripts/test-perf-flow.ps1.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _pingSent = new();
    private void OnRenderPong(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var ie) || !ie.TryGetInt32(out var id)) return;
        if (!_pingSent.TryRemove(id, out var ts)) return;
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1000.0
                 / System.Diagnostics.Stopwatch.Frequency;
        Log.Info("RenderPong", $"RENDER_PONG id={id} ms={ms:F1}");
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
                // While the launch resume prompt is unanswered, park a resumable
                // pane's spawn so the prompt gates the first `claude --resume`
                // instead of racing it. Non-resumable panes spawn immediately.
                if (_resumeDecisionPending && !string.IsNullOrEmpty(pane.ClaudeSessionId))
                {
                    Log.Info($"Pane.resize.defer pane={id:N} (awaiting resume decision)");
                    _deferredSpawns[id] = (cols, rows);
                    return;
                }
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
        AutoName(s.Root);
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
        _store.Remove(sess);   // archives to Recently closed (not deleted)
        EnsureActivePane();
        _store.Save();
        PushState();
    }

    // Bring a closed session back from "Recently closed". Restores its layout
    // in the original directories and — gated by ResumeAgentsOnLaunch — arms
    // its Claude panes to `claude --resume`, driving the progress lightbox.
    private void OnSessionRestore(JsonElement root)
    {
        if (!TryGuid(root, "id", out var id)) return;
        var sess = _store.Restore(id);
        if (sess == null) return;
        _activePaneId = FirstLeaf(sess.Root)?.Id;
        if (_settings.ResumeAgentsOnLaunch)
        {
            // Only arm panes whose transcript actually exists — a saved id with
            // no on-disk conversation would just error "No conversation found".
            var resumable = AllLeaves(sess.Root)
                .Where(p => !string.IsNullOrEmpty(p.ClaudeSessionId)
                            && TranscriptExists(p.ClaudeSessionId!, ResolvePaneCwd(sess, p)))
                .ToList();
            foreach (var p in resumable) _armedResumePanes.Add(p.Id);
            // Open the lightbox before PushState so it's tracking these panes
            // by the time their spawns (and resumed hooks) fire.
            BeginRestoreProgress(resumable.Select(p => p.Id).ToList());
        }
        _store.Save();
        PushState();
    }

    // Permanently drop a session from "Recently closed".
    private void OnSessionPurge(JsonElement root)
    {
        if (!TryGuid(root, "id", out var id)) return;
        if (_store.Purge(id)) { _store.Save(); PushState(); }
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

    // Page dismissed the first-launch onboarding lightbox — remember it so we
    // don't auto-open it again. The "Show welcome" button in Settings reopens
    // it client-side without touching this flag, so it stays available.
    private void OnOnboardingSeen()
    {
        if (_settings.OnboardingSeen) return;
        _settings.OnboardingSeen = true;
        _settings.Save();
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
        AutoName(sess.Root);
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
        pane.IsUserNamed = true;    // and the agent must never re-title it
        pane.AllowAutoName = false;
        pane.NamePrompt = null;     // drop the prompt tooltip — label is now the user's
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
        // Persist the per-pane cwd so a restart/respawn reopens this pane in the
        // same directory (and `claude --resume` runs in the right project dir).
        // Gated above on an actual change, so this only writes on real cd's.
        if (pane.Cwd != cwd) { pane.Cwd = cwd; _store.Save(); }
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
        AutoName(sess.Root);
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
            // The new wrapper takes the leaf's slot in the parent split, so it
            // inherits the leaf's Weight; inside, the leaf and its new sibling
            // split that slot evenly (both 1.0). When nothing's been resized
            // every Weight is 1.0, so this is identical to the old behavior.
            var wrapper = new PaneNode
            {
                Split = dir,
                Weight = node.Weight,
                Children = new List<PaneNode> { node, newSibling },
            };
            node.Weight = 1.0;
            return wrapper;
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

    // ---- Resize: rewrite a split's child weights -------------------------
    //
    // The web drives this when the user drags a split gutter. `splitId`
    // addresses the split node; `weights` is the new flex-grow weight per
    // child, in order. The web sends throttled intermediate updates during
    // the drag (final:false) — we apply those in memory so a mid-drag DOM
    // rebuild reads fresh weights, but we only flush to disk on the final
    // (mouseup) message. No PushState: the web already applied the live
    // layout, and treeSignature ignores Weight so a push would just re-
    // confirm the same shape.
    private void OnPaneResizeSplit(JsonElement root)
    {
        if (!TryGuid(root, "splitId", out var splitId)) return;
        if (!root.TryGetProperty("weights", out var w) || w.ValueKind != JsonValueKind.Array)
            return;
        var weights = new List<double>();
        foreach (var e in w.EnumerateArray())
        {
            double val = e.ValueKind == JsonValueKind.Number
                ? e.GetDouble()
                : (double.TryParse(e.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.NaN);
            if (double.IsNaN(val) || val <= 0) return;   // reject malformed payloads
            weights.Add(val);
        }

        PaneNode? split = null;
        foreach (var s in _store.Sessions)
        {
            var n = FindNode(s.Root, splitId);
            if (n != null && !n.IsLeaf) { split = n; break; }
        }
        if (split == null || split.Children.Count != weights.Count) return;
        for (int i = 0; i < weights.Count; i++) split.Children[i].Weight = weights[i];

        bool final = !(root.TryGetProperty("final", out var f) && f.ValueKind == JsonValueKind.False);
        if (final) _store.Save();
    }

    // ---- Move: relocate a pane within its session ------------------------
    //
    // The web drives this on a header drag-and-drop. `src` is the dragged
    // leaf, `target` the pane it was dropped on, `edge` the drop zone:
    //   left/right  → place src beside target in a Vertical split
    //   top/bottom  → place src beside target in a Horizontal split
    //   center      → swap src and target in place
    // Within-session only (the drop targets are the active session's panes).
    private void OnPaneMove(JsonElement root)
    {
        if (!TryGuid(root, "src", out var srcId)) return;
        if (!TryGuid(root, "target", out var tgtId)) return;
        if (srcId == tgtId) return;
        var edge = root.TryGetProperty("edge", out var ee) ? ee.GetString() : null;
        if (string.IsNullOrEmpty(edge)) return;

        var sess = OwningSession(srcId);
        if (sess == null || OwningSession(tgtId) != sess) return;

        if (edge == "center")
        {
            if (!SwapNodes(sess.Root, srcId, tgtId)) return;
        }
        else
        {
            var srcNode = FindNode(sess.Root, srcId);
            if (srcNode == null) return;
            var orient = (edge == "left" || edge == "right")
                ? SplitOrientation.Vertical
                : SplitOrientation.Horizontal;
            var before = edge == "left" || edge == "top";
            // Detach src (collapsing any split it leaves single-childed). The
            // target node survives — collapse only removes empty / one-child
            // splits and target != src — so we can still find it afterward.
            var detached = CloseAndCollapse(sess.Root, srcId);
            if (detached == null) return;
            srcNode.Weight = 1.0;     // join the target's slot evenly
            var rep = InsertBesideImpl(detached, tgtId, srcNode, orient, before);
            if (rep == null) return;  // target vanished (shouldn't happen)
            sess.Root = rep;
        }

        AutoName(sess.Root);
        _activePaneId = srcId;        // keep the moved pane focused
        _store.Save();
        PushState();
    }

    // Keyboard move (Ctrl+Shift+arrows): shift the active pane one slot within
    // its DIRECT parent split. left/right act on a Vertical (side-by-side)
    // split, up/down on a Horizontal (stacked) split; a perpendicular
    // direction or an edge position is a no-op. Swaps the pane with its
    // adjacent sibling (which may be a whole subtree), so the pane keeps its
    // identity + state and its Weight travels with it.
    private void OnPaneMoveDir(JsonElement root)
    {
        if (!TryGuid(root, "paneId", out var id)) return;
        var dir = root.TryGetProperty("dir", out var dd) ? dd.GetString() : null;
        if (string.IsNullOrEmpty(dir)) return;
        var sess = OwningSession(id);
        if (sess == null) return;
        if (!FindParent(sess.Root, id, out var parent, out var idx)) return; // root leaf: nowhere to go
        var wantVertical = dir is "left" or "right";
        var parentIsVertical = parent.Split == SplitOrientation.Vertical;
        if (wantVertical != parentIsVertical) return;   // direction is across this split's axis
        var target = dir is "left" or "up" ? idx - 1 : idx + 1;
        if (target < 0 || target >= parent.Children.Count) return; // already at the edge
        (parent.Children[idx], parent.Children[target]) = (parent.Children[target], parent.Children[idx]);
        _activePaneId = id;
        _store.Save();
        PushState();
    }

    // Find any node (leaf OR split) by id within a subtree.
    private static PaneNode? FindNode(PaneNode node, Guid id)
    {
        if (node.Id == id) return node;
        if (node.IsLeaf) return null;
        foreach (var c in node.Children)
        {
            var f = FindNode(c, id);
            if (f != null) return f;
        }
        return null;
    }

    // Find the split that directly contains `id` and the child's index.
    private static bool FindParent(PaneNode node, Guid id, out PaneNode parent, out int index)
    {
        if (!node.IsLeaf)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                if (node.Children[i].Id == id) { parent = node; index = i; return true; }
                if (FindParent(node.Children[i], id, out parent, out index)) return true;
            }
        }
        parent = null!;
        index = -1;
        return false;
    }

    // Swap two nodes' positions in the tree (each keeps its own Weight, so
    // sizes travel with the panes). Reads both slots before writing so a
    // shared-parent swap works too.
    private static bool SwapNodes(PaneNode root, Guid a, Guid b)
    {
        if (!FindParent(root, a, out var pa, out var ia)) return false;
        if (!FindParent(root, b, out var pb, out var ib)) return false;
        (pa.Children[ia], pb.Children[ib]) = (pb.Children[ib], pa.Children[ia]);
        return true;
    }

    // Insert `newNode` immediately before/after the target leaf, wrapping
    // them in a split of `orient`. Mirrors SplitImpl's flat-split behavior:
    // when the new split matches the parent's orientation, the parent absorbs
    // the children directly instead of nesting. Returns the (possibly new)
    // root, or null if target wasn't found.
    private static PaneNode? InsertBesideImpl(PaneNode node, Guid targetId, PaneNode newNode, SplitOrientation orient, bool before)
    {
        if (node.IsLeaf)
        {
            if (node.Id != targetId) return null;
            // The wrapper takes the target's slot; inside, target + newNode
            // share it evenly (both 1.0). Default (all-1.0) trees stay even.
            var wrapperWeight = node.Weight;
            node.Weight = 1.0;
            var children = before
                ? new List<PaneNode> { newNode, node }
                : new List<PaneNode> { node, newNode };
            return new PaneNode { Split = orient, Weight = wrapperWeight, Children = children };
        }
        for (int i = 0; i < node.Children.Count; i++)
        {
            var rep = InsertBesideImpl(node.Children[i], targetId, newNode, orient, before);
            if (rep == null) continue;
            if (rep.Split == node.Split)
            {
                node.Children.RemoveAt(i);
                node.Children.InsertRange(i, rep.Children);
            }
            else node.Children[i] = rep;
            return node;
        }
        return null;
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
                prefs = new { fontSize = _settings.FontSize, onboardingSeen = _settings.OnboardingSeen },
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
                    var waitingCount = leaves.Count(p => p.AgentState is AgentState.Waiting or AgentState.Permission);
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
                        // Git signal aggregated across panes: total diff size
                        // (the session's whole footprint) and the largest
                        // unpushed count (panes usually share a branch).
                        linesAdded   = leaves.Sum(p => p.LinesAdded),
                        linesDeleted = leaves.Sum(p => p.LinesDeleted),
                        filesChanged = leaves.Sum(p => p.FilesChanged),
                        ahead        = leaves.Select(p => p.Ahead).DefaultIfEmpty(0).Max(),
                        // Earliest working pane's start → "this session has been
                        // working Xm". 0 when nothing is working.
                        turnStartMs  = leaves
                            .Where(p => p.AgentState == AgentState.Working && p.TurnStartUnixMs > 0)
                            .Select(p => p.TurnStartUnixMs)
                            .DefaultIfEmpty(0)
                            .Min(),
                        // Most-recent turn-end among panes that are CURRENTLY at
                        // rest → "this session finished Xm ago". 0 when none is
                        // done. Filtered to Done so a working pane's stale prior
                        // turn-end never leaks into the live "ago".
                        doneAtMs     = leaves
                            .Where(p => p.AgentState == AgentState.Done && p.DoneAtUnixMs > 0)
                            .Select(p => p.DoneAtUnixMs)
                            .DefaultIfEmpty(0)
                            .Max(),
                        // Relative "last activity" for the dashboard card footer.
                        lastActivity = s.LastActivityRelative,
                    };
                }).ToArray(),
                // Recently-closed sessions for the sidebar's restore list. Just
                // the summary the row needs — title, pane/agent counts, and when
                // it was closed (the page renders "closed 5m ago" live).
                closedSessions = _store.ClosedSessions.Select(s =>
                {
                    var leaves = AllLeaves(s.Root).ToArray();
                    return new
                    {
                        id = s.Id.ToString("D"),
                        title = s.Title,
                        paneCount = leaves.Length,
                        resumableCount = leaves.Count(p => !string.IsNullOrEmpty(p.ClaudeSessionId)),
                        closedAtMs = s.ClosedAtUnixMs,
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
                // Size weight within the parent split (flex-grow). See
                // PaneNode.Weight. Applied by the web on each rebuild.
                weight = node.Weight,
                name = node.Name ?? "pane",
                // Full first-prompt text for the header hover tooltip; the
                // label above is a 40-char cut of it. Empty when the pane was
                // never auto-named from a prompt (placeholder / user-named).
                nameFull = node.NamePrompt ?? "",
                url = node.Url,
                colorIndex = node.ColorIndex,
                // Per-pane state — shows up in the pane header so each
                // pane's agent status is visible at a glance, no clicking
                // through the sidebar to figure out which one needs you.
                agentState = StateToString(node.AgentState),
                // Which agent runs here ("claude" / "codex" / "") — drives the
                // small CC badge in the header.
                agentType = node.AgentType,
                activityDetail = node.ActivityDetail,
                branch = node.Branch,
                ports  = node.Ports,
                /* Commits made since cc session-start (HEAD baseline). 0 when
                 * no session is active. Surfaces as "+N commits" chip in the
                 * pane header so the user can see at a glance how much work
                 * the agent has actually landed. */
                commitCount = node.CommitCount,
                /* Diff size since baseline (committed + uncommitted) and the
                 * unpushed-commit count — feed the "+A −D · ↑N" signal. */
                linesAdded   = node.LinesAdded,
                linesDeleted = node.LinesDeleted,
                filesChanged = node.FilesChanged,
                ahead        = node.Ahead,
                /* Unix-ms the pane started its current working spell (0 when
                 * not working) — the page ticks "working · 2m" against it. */
                turnStartMs  = node.TurnStartUnixMs,
                /* Unix-ms the pane last finished a turn (0 if never) — the page
                 * ticks "finished · 2m ago" against it on done rows. */
                doneAtMs     = node.DoneAtUnixMs,
                notification = string.IsNullOrEmpty(node.NotificationText) ? null : new
                {
                    text  = node.NotificationText,
                    level = LevelToString(node.NotificationLevel),
                },
            };
        return new
        {
            kind = "split",
            // Stable id so pane.resizeSplit can address THIS split node when
            // the user drags one of its gutters.
            id = node.Id.ToString("D"),
            // This split's own size weight inside its parent split (1.0 at the
            // root, where it's ignored). Lets a nested split keep its share.
            weight = node.Weight,
            orientation = node.Split == SplitOrientation.Horizontal ? "h" : "v",
            children = node.Children.Select(ProjectPane).ToArray(),
        };
    }

    /// Most-urgent state across panes. Drives the session row indicator.
    /// Order: Permission > Waiting > Done > Working > Idle. Done outranks
    /// Working so a session with one finished pane (your move) surfaces as
    /// "ready" even while its other panes still churn.
    private static AgentState AggregateState(IEnumerable<PaneNode> leaves)
    {
        var seen = AgentState.Idle;
        foreach (var p in leaves)
        {
            if (p.AgentState == AgentState.Permission) return AgentState.Permission;
            // Rank the remaining states; never let a lower one overwrite a
            // higher one already seen.
            var rank = Rank(p.AgentState);
            if (rank > Rank(seen)) seen = p.AgentState;
        }
        return seen;

        static int Rank(AgentState s) => s switch
        {
            AgentState.Waiting    => 3,
            AgentState.Done       => 2,
            AgentState.Working    => 1,
            _                     => 0, // Idle
        };
    }
    private static string StateToString(AgentState s) => s switch
    {
        AgentState.Working    => "working",
        AgentState.Done       => "done",
        AgentState.Waiting    => "waiting",
        AgentState.Permission => "permission",
        _                     => "idle",
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
            case "session.restore": OnSessionRestore(root); break;
            case "session.purge":   OnSessionPurge(root); break;
            case "resume.decision": OnResumeDecision(root); break;
            // Stage 3b verbs. The pane.* page verbs already take {paneId,...}
            // so we just forward the JsonElement through.
            case "pane.split":      OnPaneSplit(root); break;
            case "pane.close":      OnPaneClose(root); break;
            case "pane.move":       OnPaneMove(root); break;
            case "pane.move-dir":   OnPaneMoveDir(root); break;
            case "pane.resize-split":
                // Harness convenience: weights arrive as a comma-separated
                // string ("--weights 1.5,0.5") since `perch test` flags are
                // strings; rewrap as a JSON array for OnPaneResizeSplit.
                {
                    var splitId = root.TryGetProperty("splitId", out var si) ? si.GetString() : null;
                    var wcsv    = root.TryGetProperty("weights", out var wv) ? wv.GetString() : null;
                    if (!string.IsNullOrEmpty(splitId) && !string.IsNullOrEmpty(wcsv))
                    {
                        var arr = string.Join(",", wcsv.Split(',', StringSplitOptions.RemoveEmptyEntries));
                        var fakeRoot = JsonDocument.Parse(
                            $"{{\"splitId\":\"{splitId}\",\"weights\":[{arr}],\"final\":true}}").RootElement;
                        OnPaneResizeSplit(fakeRoot);
                    }
                }
                break;
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
                // Mirror the page → host wire: perch test passes flags as
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
                // can exercise persistence without DOM interaction. perch
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
            case "render.ping":
                // Round-trip a marker through the page's main thread and log
                // the latency on return (OnRenderPong). The test fires these
                // while two panes flood to measure renderer responsiveness.
                {
                    // `perch test` sends flag values as strings; a raw pipe
                    // client may send a JSON number. Accept either.
                    var id = 0;
                    if (root.TryGetProperty("id", out var ie))
                    {
                        if (ie.ValueKind == JsonValueKind.Number) ie.TryGetInt32(out id);
                        else int.TryParse(ie.GetString(), out id);
                    }
                    _pingSent[id] = System.Diagnostics.Stopwatch.GetTimestamp();
                    Web.CoreWebView2?.PostWebMessageAsJson($"{{\"type\":\"render.ping\",\"id\":{id}}}");
                }
                break;
            case "pty.flowstats":
                // Log the peak unacked backlog per pane in the active session.
                // Proves the backpressure gate kept the renderer from falling
                // arbitrarily far behind. Format: FLOW pane=<id> max=<bytes>.
                {
                    var s = ActiveSession();
                    if (s != null)
                        foreach (var leaf in AllLeaves(s.Root))
                            if (_ptys.TryGetValue(leaf.Id, out var pty))
                                Log.Info("FlowStats", $"FLOW pane={leaf.Id:D} max={pty.MaxOutstanding}");
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
                            // Resume-related persisted fields, surfaced so the
                            // self-test can assert capture/persistence.
                            cwd = p.Cwd,
                            claudeSessionId = p.ClaudeSessionId,
                        }).ToArray(),
                    }).ToArray();
                    // Recently-closed list, so the test can assert archive /
                    // restore / purge moved sessions between the two lists.
                    var closed = _store.ClosedSessions.Select(s => new
                    {
                        id = s.Id.ToString("D"),
                        title = s.Title,
                        closedAtMs = s.ClosedAtUnixMs,
                        panes = AllLeaves(s.Root).Select(p => new
                        {
                            id = p.Id.ToString("D"),
                            cwd = p.Cwd,
                            claudeSessionId = p.ClaudeSessionId,
                        }).ToArray(),
                    }).ToArray();
                    var prefs = new
                    {
                        fontSize = _settings.FontSize,
                        defaultShell = _settings.DefaultShell,
                        defaultCwd = _settings.DefaultCwd,
                        resumeAgentsOnLaunch = _settings.ResumeAgentsOnLaunch,
                    };
                    var dump = new { sessions = snap, closedSessions = closed, prefs };
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
<html><head><meta charset='utf-8'><title>perch</title>
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
