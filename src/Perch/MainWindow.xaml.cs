using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;
using static Perch.PaneTree;

namespace Perch;

public partial class MainWindow : FluentWindow
{
    private const string VirtualHost = "perch.local";

    private readonly string _webRoot;
    private readonly Settings _settings;
    private readonly SessionStore _store;
    private readonly ProjectStore _projects;

    // Per-pane ConPty + agent-IPC lifecycles, byte counters and last-output
    // timestamps all live in PaneManager. MainWindow decides what to spawn
    // and reacts to its events (wired in the constructor).
    private readonly PaneManager _panes;

    // How long a Working pane must be output-silent before the watchdog treats
    // its turn as finished and demotes it to Done. Long enough to ride out
    // brief pauses between an agent's spinner frames, short enough that a
    // dropped Stop self-heals quickly.
    private static readonly long IdleDemoteTicks = (long)(8.0 * System.Diagnostics.Stopwatch.Frequency);

    // A REAL resize fires SIGWINCH and the TUI redraws — genuine PTY output the
    // idle watchdog would read as renewed activity and use to re-promote a
    // silence-demoted (Done, inferred) pane back to Working. 6126482 stopped
    // NO-OP resizes from doing this; this covers the remaining case: a real
    // resize when you first show a tab, or after the window/layout changed while
    // it was hidden. We stamp the resize time and refuse to promote on output
    // that lands within RedrawWindowTicks of it — that output IS the redraw, not
    // the agent. Genuine activity keeps printing past the window and still
    // promotes. Keyed by pane; a stale entry only ever holds the last resize.
    private static readonly long RedrawWindowTicks = System.Diagnostics.Stopwatch.Frequency; // ~1s
    private readonly Dictionary<Guid, long> _lastResizeTicks = new();

    private System.Windows.Threading.DispatcherTimer? _idleWatchdog;

    private ControlIpcServer? _control;

    // Page → host dispatch table; shared by the WebView2 bridge and the
    // control pipe. Built once in the constructor (BuildRouter).
    private readonly MessageRouter _router;

    // Parses each agent pane's Claude transcript for the Inspector rail.
    // Stateful on purpose: it tails by byte offset, so re-reading a pane after
    // the agent has appended a few rows costs only those rows.
    private readonly TranscriptReader _transcripts = new();

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
    // ---- New-pane chooser ------------------------------------------------
    // Panes split from a pane whose working directory we already know (an agent
    // ran there, OSC 7 reported the cwd). id -> (source pane's cwd, source
    // pane's agent type). The fresh pane's lazy spawn is parked in
    // _deferredSpawns and a `pane.chooser` is posted on its first measure; the
    // user's pane.chooser.choose answer releases the spawn into the chosen cwd
    // + initial command, or closes the never-spawned pane on cancel.
    private readonly Dictionary<Guid, (string cwd, string agentType)> _pendingChoosers = new();

    /// Command a pane should run on its first spawn — the PTY is created lazily
    /// (on the page's first pane.resize, so it's sized right), long after the tab
    /// was created, so the command has to wait here for it. Mirrors
    /// _armedResumePanes / _pendingChoosers.
    private readonly Dictionary<Guid, string> _pendingInitialCommand = new();

    /// Prompt-bar color to set inside a Claude session once it's up, keyed by
    /// pane. cc has no flag for this (only the /color slash command), so it has
    /// to be typed into the TUI — and only once cc is actually listening, which
    /// its session-start hook tells us.
    private readonly Dictionary<Guid, string> _pendingCcColor = new();

    // Per-pane debounce for typing the pending /color into cc. Armed by the
    // session-start hook (ApplyPendingCcColor); each PTY output chunk resets it
    // (PostPaneOut); it fires when cc's first paint QUIETS — reader attached and
    // idle — which is faster than a fixed wait and won't type mid-boot.
    private readonly Dictionary<Guid, System.Windows.Threading.DispatcherTimer> _ccColorTimers = new();
    // Panes shown in the restore-progress lightbox → whether the pane has
    // reported "alive again" (its resumed session-start hook fired). Empty when
    // no restore is in flight. _restoreTimeout force-completes a batch whose
    // panes never come back so the lightbox can't hang.
    private readonly Dictionary<Guid, bool> _restoreBatch = new();
    private System.Windows.Threading.DispatcherTimer? _restoreTimeout;

    // ---- Auto-update (Velopack) ------------------------------------------
    // Headless updater (see UpdateService). We check shortly after the page is
    // ready, then hourly, and again whenever the window regains focus after a
    // lull — so a release published while Perch sat in the background is noticed
    // the moment you come back, not up to an hour later. Each check pushes
    // `update.available` to the webview footer pill; the pill's click comes back
    // as `update.apply`. A manual `update.check` (Settings → "Check now") runs
    // the same path but also reports the result via `update.status`. Null until
    // the first check; a no-op when this copy isn't a Velopack install.
    private UpdateService? _updates;
    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    // ---- Usage-limit awareness (model picker) ----------------------------
    // Polls Claude's OAuth usage endpoint on a slow, self-throttled cadence and
    // exposes per-model rate-limit state that the model menu uses to disable a
    // maxed-out model and annotate its reset time. Designed for the data being
    // usually ABSENT (the endpoint 429s today): when there's no snapshot every
    // model stays enabled and nothing is annotated. Created in the constructor;
    // kicked at page-ready and polled by a 5-minute timer (whose ticks the
    // service no-ops until its own 10/30-minute gap elapses).
    private UsageService? _usage;
    private CloudController? _cloud;
    private System.Windows.Threading.DispatcherTimer? _usageTimer;
    // When the last update check ran (UTC). Throttles the re-check we fire on
    // window activation so rapid alt-tabbing can't hammer the GitHub feed.
    private DateTime _lastUpdateCheckUtc = DateTime.MinValue;
    // Minimum gap between activation-triggered checks. The launch check + hourly
    // timer cover the steady state; this just catches a release published while
    // Perch sat in the background, the moment you come back to it.
    private static readonly TimeSpan UpdateRefocusThrottle = TimeSpan.FromMinutes(30);

    public MainWindow()
    {
        InitializeComponent();
        _webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        _settings = Settings.Load();
        _store = SessionStore.Load();
        _projects = ProjectStore.Load();
        _router = BuildRouter();
        _panes = new PaneManager(Dispatcher);
        _panes.Output += PostPaneOut;
        _panes.Exited += PostPaneExit;
        _panes.AgentStatus += OnAgentStatus;
        _panes.AgentNotify += OnAgentNotify;
        _panes.AgentMeta += OnAgentMeta;
        _panes.GitBaseline += OnGitBaseline;
        _panes.GitTouched += OnGitTouched;
        _panes.AgentTitle += OnAgentTitle;
        _panes.NameReset += OnNameReset;
        _panes.AgentType += OnAgentType;
        _panes.AgentSession += OnAgentSession;
        _panes.CloudStamped += OnCloudStamped;
        // Usage poller for the model picker. Subscribe once here; a new snapshot
        // marshals back to the UI thread and re-pushes state so the menu picks
        // up freshly-disabled models. PushState guards on the webview being up.
        _usage = new UsageService();
        _usage.Updated += () => Dispatcher.BeginInvoke(new Action(PushState));
        // Cloud resources. Inert unless gcloud is installed and authenticated.
        // LookupPaneState is the ONLY thing that decides orphan-vs-live: a machine
        // whose agent session no longer maps to a live pane is one nothing is
        // using. Deliberately not time-based — an agent legitimately waits an hour
        // on a running cluster.
        _cloud = new CloudController(Dispatcher, PostToPage, LookupPaneStateBySession);
        _cloud.Start();
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

        // Window geometry. Restored BEFORE the window is shown (SourceInitialized
        // fires after the HWND exists but before layout/render), so it opens
        // where you left it instead of visibly jumping there a frame later.
        SourceInitialized += (_, _) => RestoreWindowPlacement();
        Closing += (_, _) => SaveWindowPlacement();

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
            Activated += (_, _) =>
            {
                SyncClipboardToWeb();
                // Re-check for updates on refocus, throttled. Catches a release
                // published while the window was in the background without
                // waiting out the hourly timer; the throttle keeps rapid
                // alt-tabbing from spamming the feed. No-op on dev/portable.
                if (DateTime.UtcNow - _lastUpdateCheckUtc >= UpdateRefocusThrottle)
                    _ = CheckForUpdatesAsync();
            };

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
            _updateTimer?.Stop();
            _usageTimer?.Stop();
            _usage?.Dispose();
            _clipWatch?.Dispose();
            _control?.Dispose();
            _panes.Dispose();
            _store.Save();
            // Start the browser's own shutdown before the process exits so the
            // profile is left clean (it lives outside the kill-on-close job —
            // see InitWebViewAsync — so it gets to finish writing).
            try { Web.Dispose(); } catch { }
        };
    }

    private void EnsurePaneNames()
    {
        // PaneNode.Name doubles as the human-readable address for `perch
        // focus/send/open`. Auto-assign pane-N for leaves missing a name.
        foreach (var s in _store.Sessions) AutoName(s.Root);
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

            // Spawn the browser family OUTSIDE our kill-on-close job. WebView2
            // watches its host handle and shuts down cleanly on its own when we
            // die; the job would instead TerminateProcess it mid-write on every
            // exit — including Velopack's Environment.Exit during an update
            // restart — leaving the profile dirty. A browser starting on such a
            // profile intermittently crashes with an access violation seconds
            // in (BrowserProcessExited 0xC0000005: the post-update grey screen).
            // Shell/conhost children still join the job: page-driven PTY spawns
            // can't happen until the page loads, well after this window closes.
            JobObjectGuard.AllowChildBreakaway();
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder,
                    options: options);
                await Web.EnsureCoreWebView2Async(env);
            }
            finally
            {
                JobObjectGuard.DisallowChildBreakaway();
            }

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
            // Recover from a render-process crash instead of stranding the user
            // on a grey screen. Subscribed BEFORE navigation so even an early
            // crash is caught. See OnWebViewProcessFailed.
            core.ProcessFailed += OnWebViewProcessFailed;

            if (Directory.Exists(_webRoot))
                core.Navigate(AppUrl(_webglDisabled));
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

    // ---- Renderer-crash recovery -----------------------------------------
    // WebView2 runs our page in a child "render" process. When it dies (a GPU
    // hiccup, a bad WebGL state, an OOM under a fast output burst such as a
    // `claude --resume` transcript replay) the page goes blank/grey and STAYS
    // that way: every later PostWebMessageAsJson throws "the browser process
    // crashed". Before this handler a single render crash was an unrecoverable
    // grey screen — and because resume re-ran on the next launch, every relaunch
    // re-crashed (see the "grey screen resuming after update" report).
    //
    // Crash count inside a rolling window, so a *deterministic* crash can't spin
    // in a reload loop. Reset whenever the window lapses.
    private int _rendererCrashes;
    private DateTime _rendererCrashWindowUtc;
    // Once set, every (re)navigation drops the WebGL terminal renderer (xterm
    // falls back to its DOM renderer). Sticky for the process lifetime — a GPU
    // that crashed the renderer once will do it again.
    private bool _webglDisabled;
    // Browser-process deaths handled in a rolling window (see
    // OnWebViewProcessFailed) — the whole-control rebuilds, not page reloads.
    private int _browserRecreates;
    private DateTime _browserRecreateWindowUtc;

    /// The browser process died (e.g. an access violation), taking every view
    /// with it — the WPF control is permanently dead and Reload() throws. Swap
    /// in a fresh WebView2 control and run the normal init path against it. The
    /// PTYs live in PaneManager, untouched; the reloaded page sends `ready` and
    /// panes re-attach to their still-running shells, same contract as the
    /// render-crash Reload().
    private async Task RecreateWebViewAsync()
    {
        // URL panes hosted child views of the same dead browser; drop them.
        // The reloaded page re-emits urlpane.layout and they get recreated.
        _urlPaneCtrl?.CloseAll();

        var grid = (System.Windows.Controls.Grid)Web.Parent;
        var slot = grid.Children.IndexOf(Web);
        grid.Children.RemoveAt(slot);
        try { Web.Dispose(); } catch { }
        Web = new Microsoft.Web.WebView2.Wpf.WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
        };
        grid.Children.Insert(slot, Web);

        await InitWebViewAsync();

        // The controller captured the old control; rebind to the new one.
        _urlPaneCtrl = new UrlPaneController(this, Web);
        _urlPaneCtrl.AutoTitleRequested += (paneId, title) =>
            ApplyAutoTitle(paneId, title);
    }

    /// The app URL, optionally telling the page to skip the WebGL renderer.
    private string AppUrl(bool disableWebgl) =>
        $"https://{VirtualHost}/index.html" + (disableWebgl ? "?nowebgl=1" : "");

    private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        // The crash reason is otherwise invisible — we only ever saw the
        // downstream "control no longer valid" on the next post. Capture it so a
        // recurrence is actually diagnosable.
        Log.Error("WebView2.ProcessFailed", new Exception(
            $"kind={e.ProcessFailedKind} reason={e.Reason} exit={e.ExitCode} " +
            $"proc={e.ProcessDescription} module={e.FailureSourceModulePath}"));

        // Only the render process exiting/hanging actually blanks the page and
        // needs us to reload. GPU / utility / frame-render failures are
        // auto-recovered by WebView2 itself. The browser process exiting kills
        // the whole control — Reload() can't help; the control itself must be
        // rebuilt (see RecreateWebViewAsync). Bounded so a browser that dies
        // deterministically at startup can't spin us in a rebuild loop.
        var kind = e.ProcessFailedKind;
        if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            var nowUtc = DateTime.UtcNow;
            if (_browserRecreates == 0 || nowUtc - _browserRecreateWindowUtc > TimeSpan.FromMinutes(5))
            {
                _browserRecreates = 0;
                _browserRecreateWindowUtc = nowUtc;
            }
            if (++_browserRecreates > 3)
            {
                Log.Error("WebView2.ProcessFailed",
                    new Exception("browser process keeps dying; stopped rebuilding"));
                return;
            }
            Log.Info("WebView2.ProcessFailed", $"browser process gone — rebuilding the control (attempt {_browserRecreates})");
            _ = Dispatcher.BeginInvoke(async () =>
            {
                try { await RecreateWebViewAsync(); }
                catch (Exception ex) { Log.Error("WebView2.Recreate", ex); }
            });
            return;
        }
        if (kind != CoreWebView2ProcessFailedKind.RenderProcessExited &&
            kind != CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            return;

        var now = DateTime.UtcNow;
        if (_rendererCrashes == 0 || now - _rendererCrashWindowUtc > TimeSpan.FromMinutes(2))
        {
            _rendererCrashes = 0;
            _rendererCrashWindowUtc = now;
        }
        _rendererCrashes++;

        var core = Web.CoreWebView2;
        if (core == null) return;

        // Crashing again within the window → the renderer is failing repeatedly,
        // and the likeliest culprit is the WebGL terminal path. Re-navigate with
        // WebGL off so xterm uses its DOM renderer (slower, but it won't take the
        // GPU/render process down) — keeps the app usable instead of looping on
        // grey.
        if (_rendererCrashes >= 2 && !_webglDisabled)
        {
            _webglDisabled = true;
            Log.Info("WebView2.ProcessFailed", "repeated render crash — reloading with WebGL disabled");
            try { core.Navigate(AppUrl(disableWebgl: true)); }
            catch (Exception ex) { Log.Error("WebView2.ProcessFailed.navigate", ex); }
            return;
        }

        // Still crashing even with WebGL off → stop reloading so we don't thrash;
        // the page is down, so there's nothing to post. The log holds the reason.
        if (_rendererCrashes > 4)
        {
            Log.Error("WebView2.ProcessFailed",
                new Exception("render process keeps crashing; stopped auto-reloading"));
            return;
        }

        // Reload to respawn the dead render process and restore the UI. The page
        // re-runs from scratch (sends `ready`); OnPageReady is idempotent and the
        // backing PTYs are still alive, so panes re-attach to their live shells.
        try { core.Reload(); }
        catch (Exception ex) { Log.Error("WebView2.ProcessFailed.reload", ex); }
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

    // One route table for every page → host message. The control pipe
    // dispatches through the SAME table (see OnControlVerb), so the two entry
    // points can't drift apart. Payloads deserialize into the typed records in
    // PageMessages.cs at this boundary; a mismatch throws and is logged with
    // the payload instead of silently no-op'ing.
    private MessageRouter BuildRouter() => new MessageRouter()
        .Add("ready", OnPageReady)
        .Add<PaneInMsg>("pane.in", OnPaneIn)
        .Add<PaneAckMsg>("pane.ack", OnPaneAck)
        .Add<PaneResizeMsg>("pane.resize", OnPaneResize)
        .Add<RenderPongMsg>("render.pong", OnRenderPong)
        .Add<PaneSplitMsg>("pane.split", m => OnPaneSplit(m))
        .Add<PaneRef>("pane.close", OnPaneClose)
        .Add<PaneChooserChooseMsg>("pane.chooser.choose", OnPaneChooserChoose)
        .Add<ResizeSplitMsg>("pane.resizeSplit", OnPaneResizeSplit)
        .Add<PaneMoveMsg>("pane.move", OnPaneMove)
        .Add<PaneMoveDirMsg>("pane.moveDir", OnPaneMoveDir)
        .Add<SidebarReorderMsg>("sidebar.reorder", OnSidebarReorder)
        .Add<PaneRenameMsg>("pane.rename", OnPaneRename)
        .Add<PaneRecolorMsg>("pane.recolor", OnPaneRecolor)
        .Add<PaneCwdMsg>("pane.cwd", OnPaneCwd)
        .Add<PaneModelMsg>("pane.model", OnPaneModel)
        .Add<PaneProbeMsg>("pane.probe", OnPaneProbe)
        .Add<UrlPaneLayoutMsg>("urlpane.layout", m => _urlPaneCtrl?.OnLayout(m))
        .Add<PaneRef>("urlpane.dispose", m => _urlPaneCtrl?.OnDispose(m))
        .Add<SessionNewMsg>("session.new", OnSessionNew)
        .Add<SessionRef>("session.select", OnSessionSelect)
        .Add<SessionRenameMsg>("session.rename", OnSessionRename)
        .Add<SessionCloseMsg>("session.close", OnSessionClose)
        .Add<SessionRef>("session.restore", OnSessionRestore)
        .Add<SessionRef>("session.purge", OnSessionPurge)
        .Add<ResumeDecisionMsg>("resume.decision", OnResumeDecision)
        .Add<PaneRef>("pane.focus", OnPaneFocus)
        .Add<UrlOpenMsg>("url.open", OnUrlOpen)
        .Add<PrefsSetMsg>("prefs.set", OnPrefsSet)
        .Add<PaneRef>("commits.request", OnCommitsRequest)
        .Add<PaneRef>("inspector.request", OnInspectorRequest)
        .Add("settings.request", OnSettingsRequest)
        .Add<SettingsSaveMsg>("settings.save", OnSettingsSave)
        .Add("onboarding.seen", OnOnboardingSeen)
        .Add<UiModeMsg>("ui.mode", OnUiMode)
        .Add("projects.scan", OnProjectsScan)
        .Add("project.browse", OnProjectBrowse)
        .Add<ProjectAddMsg>("project.add", OnProjectAdd)
        .Add<ProjectRef>("project.remove", OnProjectRemove)
        .Add<ProjectUpdateMsg>("project.update", OnProjectUpdate)
        .Add<ProjectTabNewMsg>("project.tab.new", OnProjectTabNew)
        .Add("update.apply", OnUpdateApply)
        .Add("update.check", OnUpdateCheckRequested)
        .Add<CloudPanelMsg>("cloud.panel", m => _cloud?.SetPanelOpen(m.Open))
        .Add("cloud.refresh", () => _ = _cloud?.RefreshAsync())
        .Add<CloudDeleteMsg>("cloud.delete", m => _ = _cloud?.DeleteAsync(m.Id))
        .Add("cloud.deleteOrphans", () => _ = _cloud?.DeleteOrphansAsync());

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
            var type = t.GetString() ?? "";
            if (!_router.Dispatch(type, root))
                Log.Info("Web.msg.unknown", $"type={type}");
        }
        // Payload didn't match its DTO (or wasn't JSON at all) — the protocol
        // drifted between bridge.ts and PageMessages.cs. Log the head of the
        // payload so the mismatch is diagnosable from errors.log alone.
        catch (JsonException ex) { Log.Error($"Web.OnMessage.json payload={Truncate(raw, 300)}", ex); }
        catch (Exception ex)     { Log.Error("Web.OnMessage", ex); }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

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

        // Kick off the auto-update check now that the page can receive
        // messages. Fire-and-forget: the await inside resumes on the UI thread
        // (WPF SynchronizationContext) so the eventual PostWebMessageAsJson is
        // thread-safe. A one-shot here (with a brief settle delay) plus the
        // hourly timer and the on-refocus check keep the pill current without a
        // relaunch.
        _ = CheckForUpdatesAsync(initialDelay: true);
        if (_updateTimer is null)
        {
            // Hourly, matching cmux-for-macOS's Sparkle cadence (it likewise
            // dropped from a longer default to 1h). Frequent enough that a
            // running session notices a release the same day without a restart,
            // cheap enough on the public feed.
            _updateTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromHours(1),
            };
            _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync();
            _updateTimer.Start();
        }

        // Kick the usage poll now that the page can receive a re-push, then poll
        // every 5 minutes. The service's own throttle enforces the real 10/30-
        // minute cadence — the timer just gives it a chance to become due.
        _ = _usage?.RefreshIfDueAsync();
        if (_usageTimer is null)
        {
            _usageTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(5),
            };
            _usageTimer.Tick += (_, _) => _ = _usage?.RefreshIfDueAsync();
            _usageTimer.Start();
        }
    }

    // ---- Auto-update ------------------------------------------------------

    /// Page asked for a manual check (Settings → "Check now"). Same path as the
    /// automatic checks, but `userInitiated` makes it report the outcome back to
    /// the dialog via `update.status` (the silent background checks stay quiet
    /// when up to date).
    private void OnUpdateCheckRequested() => _ = CheckForUpdatesAsync(userInitiated: true);

    /// Check the GitHub release feed and, if a newer version exists, light up
    /// the webview's update pill. Background checks (launch / hourly / refocus)
    /// are silent on a non-Velopack install (dev, portable) and on any
    /// network/feed error — a failed check must never surface noise; the pill
    /// simply stays hidden until a later check succeeds. A `userInitiated` check
    /// additionally posts an `update.status` so the Settings dialog can show
    /// "up to date" / "couldn't check" / "not this build" feedback.
    ///
    /// `initialDelay` lets the very first (launch) check wait a few seconds for
    /// the first frames to settle before hitting the network; every other caller
    /// skips it so the result is prompt.
    private async Task CheckForUpdatesAsync(bool initialDelay = false, bool userInitiated = false)
    {
        try
        {
            _updates ??= new UpdateService();
            if (!_updates.IsUpdatable)                  // dev run / portable unzip
            {
                if (userInitiated) PostUpdateStatus("unsupported");
                return;
            }
            // Stamp before the (optional) delay + network call so the refocus
            // throttle counts from when this check started, not when it finished.
            _lastUpdateCheckUtc = DateTime.UtcNow;
            if (initialDelay) await Task.Delay(TimeSpan.FromSeconds(3));
            var newVersion = await _updates.CheckAsync();
            if (string.IsNullOrEmpty(newVersion))           // already up to date
            {
                if (userInitiated) PostUpdateStatus("uptodate", _updates.CurrentVersion);
                return;
            }
            // Always reveal the pill; on a manual check also confirm in Settings.
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(
                new { type = "update.available", version = newVersion }));
            if (userInitiated) PostUpdateStatus("available", newVersion);
        }
        catch (Exception ex)
        {
            Log.Error("Update.check", ex);
            if (userInitiated) PostUpdateStatus("error");
        }
    }

    /// Tell the Settings dialog the outcome of a manual check. `state` is one of
    /// uptodate / available / error / unsupported; `version` is the relevant
    /// version string where one applies.
    private void PostUpdateStatus(string state, string? version = null)
    {
        try
        {
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(
                new { type = "update.status", state, version }));
        }
        catch (Exception ex) { Log.Error("Update.status", ex); }
    }

    /// User clicked the update pill. Persist session state (the process is
    /// about to be replaced), then download + relaunch into the new version. On
    /// failure, tell the page so the pill can offer a retry.
    private async void OnUpdateApply()
    {
        if (_updates is null) return;
        try
        {
            _store.Save();
            await _updates.DownloadAndApplyAsync();
            // Not reached on success — ApplyUpdatesAndRestart replaces us.
        }
        catch (Exception ex)
        {
            Log.Error("Update.apply", ex);
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(
                new { type = "update.error", message = ex.Message }));
        }
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
                        && ClaudeTranscripts.Exists(t.pane.ClaudeSessionId!, ResolvePaneCwd(t.sess, t.pane)));

    /// The cwd a pane spawns in: its own persisted cwd, then the session cwd,
    /// then the configured default. Single source so the resume pre-flight and
    /// SpawnPty agree on where the agent runs.
    private string ResolvePaneCwd(Session sess, PaneNode pane) =>
        FirstExistingDir(pane.Cwd, sess.Cwd) ?? _settings.ResolveDefaultCwd();

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
    private void OnResumeDecision(ResumeDecisionMsg msg)
    {
        // Absent/malformed accept degrades to "declined" (spawns release as
        // plain shells) — never to parked-forever.
        var accept = msg.Accept == true;
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

    private void SpawnPty(Session sess, PaneNode pane, int cols = 80, int rows = 24, string? initialCommand = null)
    {
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
            // auto-relaunches the agent. Skipped when the caller already supplied
            // an initial command (the new-pane chooser passes "claude"/"codex");
            // a fresh split is never armed for resume anyway, so they can't clash.
            // A freshly-created project tab carries its own command (`claude
            // --session-id … --name …`). It wins over resume-arming: the tab was
            // made seconds ago, so there is nothing to resume into yet — and if
            // both fired we'd launch two agents in one pane.
            if (initialCommand == null && _pendingInitialCommand.Remove(pane.Id, out var queued))
            {
                initialCommand = queued;
                _armedResumePanes.Remove(pane.Id);
            }
            if (initialCommand == null && _armedResumePanes.Remove(pane.Id) && !string.IsNullOrEmpty(pane.ClaudeSessionId))
            {
                initialCommand = $"claude --resume {pane.ClaudeSessionId}";
                NoteRestorePaneResuming(pane.Id);
            }
            // Reassert the pane's model selection to disk so wrap-claude picks
            // it up on the first `claude` in this shell (the temp file may have
            // been reaped from %TEMP%, or this is a restored/respawned pane
            // whose Model was persisted but whose file is gone). Empty clears it.
            ClaudeModelState.Write(pane.Id, pane.Model);
            // Shell.BuildStartupCommandLine injects PERCH_PIPE / PERCH_PANE_ID
            // env vars per-pane so agents inside the shell can call back
            // into our IPC layer (stage 4 reactivates that pipe).
            var startCmd = Shell.BuildStartupCommandLine(baseShell, cwd, pane.Id, initialCommand);
            _panes.Spawn(sess, pane, startCmd, cwd, cols, rows, baseShell);
            // Seed the pane's cwd from the dir we just spawned into instead of
            // waiting for the shell to report it via OSC 7. A pane that autostarts
            // an agent (`claude --resume <id>`) runs that as the LAST statement of
            // pwsh's -Command script, and the prompt function — the only thing that
            // emits OSC 7 — doesn't run until the script returns. For a long-lived
            // agent that's never, so the host never learned the pane's cwd and every
            // git signal gated on it (branch chip, +N commits, diff chip, unpushed-
            // commit recap) stayed dark for the whole session. OSC 7 still corrects
            // this on a real `cd`; OnPaneCwd no-ops when the value hasn't changed.
            if (!string.IsNullOrEmpty(cwd))
                OnPaneCwd(new PaneCwdMsg { PaneId = pane.Id, Cwd = cwd });
        }
        catch (Exception ex)
        {
            Log.Error($"Pane.spawn {pane.Id:N}", ex);
            PostHostError($"failed to spawn pane: {ex.Message}");
        }
    }

    private void DestroyPty(Guid paneId) => _panes.Destroy(paneId);

    // ---- Graceful agent shutdown on close ----------------------------------
    // Closing a tab used to shoot the console outright (ClosePseudoConsole
    // terminates the whole attached tree), which could kill Claude halfway
    // through a multi-file edit and skip its SessionEnd hooks. Now a pane
    // RUNNING an agent gets a polite exit first: Esc (abort any mid-flight
    // turn), a beat, Esc again (dismiss a dialog left standing), then
    // "/exit". The session-end hook coming back over the pane pipe clears
    // pane.AgentType — that's the clean-exit ack — after which the hard
    // teardown only kills a bare shell. Timeout → hard kill anyway, so this
    // can never be worse than the old behavior, only ≤ ~4s slower — and the
    // UI never waits (the tab is gone immediately; the grace window runs on
    // detached PTYs).
    //
    // Everything here runs on the UI thread (awaits resume on the WPF
    // context), same as the IPC handlers that mutate pane.AgentType — so the
    // completion poll below is race-free by construction.

    private readonly Dictionary<Guid, System.Threading.CancellationTokenSource> _pendingShutdown = new();

    private async System.Threading.Tasks.Task ShutdownPaneAsync(PaneNode pane)
    {
        // Polite path only for a live Claude pane with a PTY to talk to.
        // Plain shells have no hooks to save, and "/exit" is cc's exit
        // choreography — other agents take the hard kill as before.
        if (pane.AgentType != "claude" || !_panes.Has(pane.Id))
        {
            DestroyPty(pane.Id);
            return;
        }
        var cts = new System.Threading.CancellationTokenSource();
        _pendingShutdown[pane.Id] = cts;
        try
        {
            var esc = new byte[] { 0x1B };
            _panes.Write(pane.Id, esc);
            await System.Threading.Tasks.Task.Delay(250, cts.Token);
            _panes.Write(pane.Id, esc);
            await System.Threading.Tasks.Task.Delay(250, cts.Token);
            _panes.Write(pane.Id, System.Text.Encoding.UTF8.GetBytes("/exit\r"));
            // session-end → `agent ""` clears AgentType: the clean-exit ack.
            // (If cc died earlier WITHOUT its hook, the ack never comes and
            // we're typing /exit at a shell — harmless; the deadline caps it.)
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (pane.AgentType == "claude" && DateTime.UtcNow < deadline)
                await System.Threading.Tasks.Task.Delay(100, cts.Token);
            Log.Info("Shutdown", pane.AgentType == "claude"
                ? $"pane={pane.Id:N} no session-end within grace; hard kill"
                : $"pane={pane.Id:N} clean agent exit");
        }
        catch (System.Threading.Tasks.TaskCanceledException) { /* restore cut the grace short */ }
        catch (Exception ex) { Log.Error("Shutdown", ex); /* dead pipe etc. → straight to kill */ }
        finally
        {
            _pendingShutdown.Remove(pane.Id);
            DestroyPty(pane.Id);   // idempotent; CancelPendingShutdown may have beaten us here
        }
    }

    /// Cut a pane's grace window short. Restore needs the PTY slot NOW:
    /// Spawn refuses a pane id that still has a live PTY, so a session
    /// restored while its panes are politely exiting would come up dead.
    private void CancelPendingShutdown(Guid paneId)
    {
        if (_pendingShutdown.Remove(paneId, out var cts))
        {
            try { cts.Cancel(); } catch { }
            DestroyPty(paneId);
        }
    }

    /// Post-close teardown, detached from the UI: polite-exit every pane
    /// concurrently, and only then reclaim the worktree folder (when asked) —
    /// `git worktree remove` fails while a process still has its cwd inside.
    private async System.Threading.Tasks.Task CloseTeardownAsync(
        List<PaneNode> leaves, string wtRepo, string wtPath)
    {
        await System.Threading.Tasks.Task.WhenAll(leaves.Select(ShutdownPaneAsync));
        if (wtRepo.Length > 0 && wtPath.Length > 0)
            _ = Worktree.RemoveAsync(wtRepo, wtPath);
    }

    // ---- Agent IPC handlers (perch status / notify / meta) ----------------
    // State/level string mappings live in StateProjection.cs.

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
        var newState  = StateProjection.ParseAgentState(msg.State);
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
        // The note (the agent's ask) belongs to the blocked state that raised
        // it. Leaving Waiting/Permission for a non-blocked state means the ask
        // was answered or abandoned — without this, the dashboard card (which
        // shows the note for ANY state) kept reading "Claude needs your
        // permission" long after a normal approve → work → finish cycle.
        if (IsAttention(prev) && !IsAttention(pane.AgentState) &&
            pane.NotificationText.Length > 0)
            pane.NotificationText = "";
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
                var silent = _panes.TryGetLastOutputTicks(pane.Id, out var last)
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
                else if (pane.AgentState == AgentState.Done && pane.StateInferred && !silent
                         && (!_lastResizeTicks.TryGetValue(pane.Id, out var rz)
                             || last - rz > RedrawWindowTicks))
                {
                    // Not a resize's redraw — real output resumed. Walk it back.
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

    // The watchdog's sibling for the BLOCKED states, driven by the page. The
    // watchdog can't touch them — silence is their NORMAL shape (a dialog sits
    // quietly) — yet several of their edges fire no hook at all (Esc aborts
    // the turn without a Stop; deny-with-feedback resumes without the tool; a
    // question dialog whose notification was dropped never announces itself).
    // The page owns the terminal buffer, so IT watches for dialogs leaving or
    // appearing and reports here; this handler decides, because only the host
    // knows which states were hook-asserted vs inferred:
    //   • Permission + dialog gone → INFERRED Working. If the agent resumed
    //     (approve/deny) the state is simply correct early; if it's at rest
    //     (Esc) the watchdog settles it to Done on silence. Permission is the
    //     one non-inferred state the probe may override — its exits are
    //     hook-less by design (cc fires no Stop on user interrupt).
    //   • INFERRED Done + blocked dialog visible → Waiting. The watchdog read
    //     a question/plan dialog's silence as "turn over"; the dialog says
    //     otherwise. A real Stop-hook Done is never overridden — an idle
    //     pane's ❯ menu is usually the user driving /model.
    //   • INFERRED Waiting + dialog gone → back to Done (still inferred). The
    //     unwind of the previous rule only — a hook-raised waiting (e.g. MCP
    //     elicitation, whose dialog may not match cc's markers) is never
    //     probed away.
    // No taskbar flash on the Waiting promotion: it's an inference, and a
    // false flash is the boy who cried wolf this state machine exists to avoid.
    private void OnPaneProbe(PaneProbeMsg msg)
    {
        var sess = OwningSession(msg.PaneId);
        var pane = sess == null ? null : FindPane(sess, msg.PaneId);
        if (pane == null) return;

        if (msg.PermissionVisible == false && pane.AgentState == AgentState.Permission)
        {
            pane.AgentState = AgentState.Working;
            pane.StateInferred = true;
            pane.TurnStartUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // The ask died with the dialog — a stale "Claude needs your
            // permission" note must not outlive it in the sidebar/dashboard.
            pane.NotificationText = "";
            Log.Info("PaneProbe", $"pane={pane.Id:N} permission->working (dialog gone)");
            PushState();
        }
        else if (msg.BlockedVisible == true &&
                 pane.AgentState == AgentState.Done && pane.StateInferred)
        {
            pane.AgentState = AgentState.Waiting;
            // Stays inferred — that's the license for the unwind rule below.
            if (string.IsNullOrEmpty(pane.NotificationText))
            {
                pane.NotificationText = "Waiting at a prompt in the terminal";
                pane.NotificationLevel = NotificationLevel.Warn;
            }
            Log.Info("PaneProbe", $"pane={pane.Id:N} done->waiting (blocked dialog visible)");
            PushState();
        }
        else if (msg.BlockedVisible == false &&
                 pane.AgentState == AgentState.Waiting && pane.StateInferred)
        {
            // DoneAtUnixMs still holds the watchdog's original turn-end stamp,
            // so the row's "finished · Xm ago" resumes where it left off.
            pane.AgentState = AgentState.Done;
            pane.NotificationText = "";   // the synthetic note above is ours to take back
            Log.Info("PaneProbe", $"pane={pane.Id:N} waiting->done (dialog gone)");
            PushState();
        }
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
            // A new session is a DIFFERENT transcript. Drop the parsed tail, or
            // the Inspector would keep showing the previous agent's journal
            // (and bill its tokens to this one) until the pane closed.
            _transcripts.Forget(paneId);
            _store.Save();
        }
        MarkRestorePaneReady(paneId);
        ApplyPendingCcColor(paneId);
    }

    /// The PreToolUse hook just stamped agent labels onto a `gcloud ... create`
    /// in this pane. Snapshot the pane's name and task into the ledger while the
    /// pane still exists — the labels on the resource can only hold ids (63 chars
    /// of [a-z0-9_-]), so this is the only place the sentence explaining the
    /// machine can be recorded, and by the time it's an orphan the pane is gone.
    private void OnCloudStamped(Session sess, Guid paneId, CloudStampedMessage msg)
    {
        var pane = FindPane(sess, paneId);
        if (pane == null) return;
        _cloud?.OnStamped(sess, pane, msg);
    }

    /// Claude session id → the agent state of the pane still running it, or null
    /// if no live pane owns it. Null is what makes a cloud resource an orphan.
    private string? LookupPaneStateBySession(string? session)
    {
        if (string.IsNullOrWhiteSpace(session)) return null;
        foreach (var sess in _store.Sessions)
        {
            // A closed session's panes are not "live" even though the tree
            // survives for restore — a machine whose tab you closed is exactly
            // the thing we're trying to catch.
            if (sess.ClosedAtUnixMs > 0) continue;
            foreach (var pane in AllLeaves(sess.Root))
                if (string.Equals(pane.ClaudeSessionId, session, StringComparison.OrdinalIgnoreCase))
                    // Non-null is the signal "a live pane owns this machine" —
                    // the state string itself is only for display.
                    return pane.AgentState.ToString().ToLowerInvariant();
        }
        return null;
    }

    /// Post an arbitrary payload to the page. Shared by CloudController so it
    /// doesn't need a WebView2 reference of its own.
    private void PostToPage(object payload)
    {
        try { Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload)); }
        catch (Exception ex) { Log.Error("PostToPage", ex); }
    }

    // Set the tab's color INSIDE its Claude session, so the prompt bar matches
    // the sidebar dot. cc exposes no flag for this — only the `/color` slash
    // command — so it has to be typed into the TUI, which means waiting until cc
    // is actually listening AND has finished its first paint (type into the raw
    // PTY mid-boot and it can land before cc's reader is attached, and be lost).
    //
    // The session-start hook proves the session is up; from there we wait for
    // cc's first paint to QUIET instead of a fixed delay. Each PTY output chunk
    // resets a short debounce (PostPaneOut → StartCcColorTimer); when output
    // stops, the timer types the color. That tracks the machine — quick on a fast
    // paint, patient on a slow one — where the old fixed 700ms was both too slow
    // on a fast box and occasionally too early on a loaded one. One-shot:
    // _pendingCcColor is drained on the write (FlushCcColor), so a later /clear
    // (which re-fires session-start) doesn't re-type it.
    private void ApplyPendingCcColor(Guid paneId)
    {
        // Marshal to the UI thread: this fires from the IPC pipe thread, and the
        // DispatcherTimer + its dictionaries live on the UI thread with PostPaneOut.
        Dispatcher.InvokeAsync(() =>
        {
            if (_pendingCcColor.ContainsKey(paneId)) StartCcColorTimer(paneId);
        });
    }

    // (Re)start the quiet-debounce that types the pending /color. Created lazily
    // per pane; the initial Start() means it still fires when cc emits no further
    // output after session-start. UI thread only.
    private void StartCcColorTimer(Guid paneId)
    {
        if (!_ccColorTimers.TryGetValue(paneId, out var timer))
        {
            timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150),
            };
            timer.Tick += (_, _) => FlushCcColor(paneId);
            _ccColorTimers[paneId] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    // Type `/color <name>\r` into the pane once cc's paint has settled. Runs on
    // the UI thread (timer tick / PostPaneOut), so no dispatch. Guarded: the pane
    // may have closed between the last output chunk and this tick.
    private void FlushCcColor(Guid paneId)
    {
        if (_ccColorTimers.Remove(paneId, out var timer)) timer.Stop();
        if (!_pendingCcColor.Remove(paneId, out var color)) return;
        try { _panes.Write(paneId, System.Text.Encoding.UTF8.GetBytes($"/color {color}\r")); }
        catch (Exception ex) { Log.Info("CcColor", $"skipped: {ex.Message}"); }
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
        // New session, new attribution: the touched set describes the PREVIOUS
        // cc session's work. Cleared on session-end too so a dead agent's set
        // can't filter a future session.
        pane.TouchedFiles.Clear();
        pane.DiffAttributed = false;
        if (string.IsNullOrEmpty(pane.CommitBaseline))
        {
            pane.UntrackedBaseline = null;
            pane.CommitCount = 0;
            pane.LinesAdded = pane.LinesDeleted = pane.FilesChanged = pane.Ahead = 0;
            PushState();
            // This agent leaving may make a shared tree solo again — let the
            // remaining same-cwd panes widen back to the whole-tree measurement.
            RefreshCwdSiblings(pane);
            return;
        }
        _ = CaptureUntrackedBaselineAsync(pane, refresh: true);
        // A second agent joining a tree flips its siblings from "whole tree"
        // to "my touched files" — recompute them now instead of leaving them
        // wearing the union until their own next state change.
        RefreshCwdSiblings(pane);
    }

    /// A file-editing tool just ran in this pane (post-tool-use hook). Record
    /// the file in git's own path space (cwd-relative, forward slashes) so
    /// RefreshGitStatsAsync can split a SHARED working tree's loc between the
    /// agents editing it. Bounded naturally — one entry per distinct file per
    /// session. No PushState: the stats refresh on the next status change is
    /// what repaints.
    private void OnGitTouched(Session sess, Guid paneId, GitTouchedMessage msg)
    {
        var pane = FindPane(sess, paneId);
        if (pane == null || string.IsNullOrWhiteSpace(msg.Path)) return;
        if (!_paneCwd.TryGetValue(paneId, out var cwd) || string.IsNullOrEmpty(cwd)) return;
        string rel;
        if (!System.IO.Path.IsPathRooted(msg.Path))
        {
            rel = msg.Path!;   // already relative (to the agent's cwd = the pane's cwd)
        }
        else
        {
            try { rel = System.IO.Path.GetRelativePath(cwd, msg.Path!); }
            catch { return; }
            // Outside the pane's cwd (or another drive): not expressible in the
            // cwd-relative space the git numstat rows use — skip rather than
            // mis-attribute. (Panes sit at the repo/worktree root in practice,
            // so "under the cwd" and "in the repo" are the same set.)
            if (rel.StartsWith("..") || System.IO.Path.IsPathRooted(rel)) return;
        }
        pane.TouchedFiles.Add(rel.Replace('\\', '/'));
    }

    /// Recompute git stats for every OTHER agent pane measuring the same
    /// working tree as <paramref name="pane"/>. An agent joining or leaving a
    /// tree changes what its siblings' measurement MEANS (whole tree vs own
    /// touched files) — without this nudge they'd show the stale reading until
    /// their own next state change.
    private void RefreshCwdSiblings(PaneNode pane)
    {
        if (!_paneCwd.TryGetValue(pane.Id, out var cwd) || string.IsNullOrEmpty(cwd)) return;
        foreach (var s in _store.Sessions)
            foreach (var p in AllLeaves(s.Root))
                if (p.Id != pane.Id && !string.IsNullOrEmpty(p.CommitBaseline)
                    && _paneCwd.TryGetValue(p.Id, out var c)
                    && string.Equals(c, cwd, StringComparison.OrdinalIgnoreCase))
                    _ = RefreshGitStatsAsync(p);
    }

    // Snapshot the pane's untracked-file set the moment its baseline sha
    // lands (and again on a real cwd change — both anchors are cwd-relative).
    // DiffStatsAsync uses it to count only untracked files NEW since
    // session-start; until it lands the refresh skips the untracked fold-in,
    // so a mid-capture refresh can only undercount, never re-inflate.
    private async System.Threading.Tasks.Task CaptureUntrackedBaselineAsync(PaneNode pane, bool refresh)
    {
        pane.UntrackedBaseline = null;
        if (_paneCwd.TryGetValue(pane.Id, out var cwd) && !string.IsNullOrEmpty(cwd))
        {
            var untracked = await GitProc.UntrackedFilesAsync(cwd);
            // Enumeration failed → not a repo / no git. Leave the snapshot
            // null; the refresh's own enumeration would fail the same way.
            if (untracked != null)
                pane.UntrackedBaseline = new HashSet<string>(untracked, StringComparer.Ordinal);
        }
        if (refresh) await RefreshGitStatsAsync(pane);
    }

    private async System.Threading.Tasks.Task RefreshGitStatsAsync(PaneNode pane)
    {
        // Ahead-of-upstream is meaningful for ANY repo pane — a plain shell (or
        // a resumed/pre-baseline session) with unpushed commits should still
        // light the "↑N ready to push" chip — so it's gated only on knowing the
        // cwd, NOT on a cc-session baseline. CommitCount and the diff/loc size
        // ARE baseline-relative ("what changed since the agent started"), so we
        // skip those (leaving them at 0) when no agent session has captured a
        // baseline. Anchoring the loc to HEAD without a baseline was a mistake:
        // DiffStatsAsync folds in every pre-existing untracked file, so a fresh
        // or plain-shell pane showed the working tree's ambient footprint (e.g.
        // "+3k") as work done in the pane. No baseline → no loc chip.
        if (!_paneCwd.TryGetValue(pane.Id, out var cwd) || string.IsNullOrEmpty(cwd)) return;
        var baseline = pane.CommitBaseline;
        var hasBaseline = !string.IsNullOrEmpty(baseline);
        // Shared working tree? Two agents with live baselines in the SAME cwd
        // (projects-mode tabs without worktrees) each read the whole tree's
        // diff, so every tab wore the union of everyone's work. When shared,
        // restrict this pane's stats to the files ITS agent reported touching
        // (git.touched, hook-attributed). A lone pane keeps the whole-tree
        // measurement — that also catches files a Bash command created, which
        // attribution can't see. Snapshot the set (it mutates on the UI thread
        // while the git walk runs off it).
        IReadOnlySet<string>? pathFilter = null;
        if (hasBaseline)
        {
            var shared = _store.Sessions
                .SelectMany(s => AllLeaves(s.Root))
                .Any(p => p.Id != pane.Id && !string.IsNullOrEmpty(p.CommitBaseline)
                       && _paneCwd.TryGetValue(p.Id, out var otherCwd)
                       && string.Equals(otherCwd, cwd, StringComparison.OrdinalIgnoreCase));
            if (shared)
                pathFilter = new HashSet<string>(pane.TouchedFiles, StringComparer.OrdinalIgnoreCase);
        }
        // The uncommitted working-tree diff is the one term a human's own
        // hand-edits land in — git can't tell them from the agent's — so scope
        // THAT term to the files the agent's edit tools reported touching, for
        // EVERY pane, not just shared trees. Committed + new-untracked work stay
        // whole (pathFilter), so the agent's commits and the files its Bash
        // commands created still count. Empty until the agent's first edit, which
        // correctly reads as 0 tracked loc rather than crediting your hand-edits.
        var touched = new HashSet<string>(pane.TouchedFiles, StringComparer.OrdinalIgnoreCase);
        // Run the git queries concurrently off the UI thread — they're
        // independent and each is a fast plumbing command. The commit count and
        // the loc size come from one walk (SessionStatsAsync) so they can't
        // disagree about what counts as this session's work.
        var statsT = hasBaseline
            ? GitProc.SessionStatsAsync(baseline, cwd, pane.UntrackedBaseline, pathFilter, touched)
            : System.Threading.Tasks.Task.FromResult<GitSessionStats?>(null);
        var aheadT = GitProc.AheadAsync(cwd);
        await System.Threading.Tasks.Task.WhenAll(statsT, aheadT);
        var stats = await statsT;
        var ahead = await aheadT;
        await Dispatcher.InvokeAsync(() =>
        {
            var changed = false;
            // The baseline may have moved (or been cleared by session-end)
            // while the git queries ran — session-end sends `status idle`
            // (which lands here with the old baseline) immediately before the
            // clearing `git.baseline ""`, so an unguarded write-back
            // resurrected the just-zeroed stats and the footer kept a dead
            // session's "+9". Baseline-relative values only apply if the
            // baseline they were computed against is still current; `ahead`
            // isn't baseline-relative and always applies.
            var baselineCurrent = pane.CommitBaseline == baseline;
            if (baselineCurrent && stats is GitSessionStats s)
            {
                if (pane.CommitCount  != s.Commits) { pane.CommitCount  = s.Commits; changed = true; }
                if (pane.FilesChanged != s.Files)   { pane.FilesChanged = s.Files;   changed = true; }
                if (pane.LinesAdded   != s.Added)   { pane.LinesAdded   = s.Added;   changed = true; }
                if (pane.LinesDeleted != s.Deleted) { pane.LinesDeleted = s.Deleted; changed = true; }
                var attributed = pathFilter != null;
                if (pane.DiffAttributed != attributed) { pane.DiffAttributed = attributed; changed = true; }
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
        pane.NotificationLevel = StateProjection.ParseLevel(msg.Level);
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

    private void OnPaneIn(PaneInMsg msg)
    {
        if (!_panes.TryGet(msg.PaneId, out var pty)) return;
        try { pty.Write(Convert.FromBase64String(msg.B64)); }
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
    private void OnPaneAck(PaneAckMsg msg) => _panes.Ack(msg.PaneId, msg.Bytes);

    // Renderer-responsiveness probe (test-only). The control pipe fires
    // render.ping; we round-trip it through the page's main thread and log
    // the latency. Under the old fire-and-forget output path a flooded
    // renderer makes this round-trip take seconds (the same thread that
    // would process a keystroke is buried in the write backlog); with flow
    // control it stays in the low-ms range. See scripts/test-perf-flow.ps1.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _pingSent = new();
    private void OnRenderPong(RenderPongMsg msg)
    {
        if (!_pingSent.TryRemove(msg.Id, out var ts)) return;
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1000.0
                 / System.Diagnostics.Stopwatch.Frequency;
        Log.Info("RenderPong", $"RENDER_PONG id={msg.Id} ms={ms:F1}");
    }

    private void OnPaneResize(PaneResizeMsg msg)
    {
        var id = msg.PaneId;
        var cols = msg.Cols;
        var rows = msg.Rows;

        // Don't act on degenerate measurements -- the page sends one before
        // CSS Grid has finished laying out the pane on first paint, and a
        // resize-to-tiny followed by resize-to-real makes PowerShell clear
        // the screen between the two (banner + prompt evicted).
        if (cols < 5 || rows < 3)
        {
            Log.Info($"Pane.resize.skip pane={id:N} cols={cols} rows={rows}");
            return;
        }

        // Note when this pane was resized so the watchdog can tell the resize's
        // redraw from real agent output (see RedrawWindowTicks). Stamped for every
        // resize — a deduped no-op costs nothing here, and a real one is exactly
        // the redraw we must not read as "the agent resumed".
        _lastResizeTicks[id] = System.Diagnostics.Stopwatch.GetTimestamp();

        // Lazy spawn: first valid pane.resize for a pane creates its
        // ConPty at the page's measured size, so PowerShell's banner is
        // laid out at the final dimensions and never has to be cleared.
        if (_panes.TryResize(id, cols, rows)) return;

        var sess = OwningSession(id);
        var pane = sess == null ? null : AllLeaves(sess.Root).FirstOrDefault(p => p.Id == id);
        if (sess == null || pane == null) return;
        // While the launch resume prompt is unanswered, park a resumable
        // pane's spawn so the prompt gates the first `claude --resume`
        // instead of racing it. Non-resumable panes spawn immediately.
        if (_resumeDecisionPending && !string.IsNullOrEmpty(pane.ClaudeSessionId))
        {
            Log.Info($"Pane.resize.defer pane={id:N} (awaiting resume decision)");
            _deferredSpawns[id] = (cols, rows);
            return;
        }
        // New-pane chooser: park the spawn and ask the user what to run
        // here. Released by OnPaneChooserChoose (or closed on cancel).
        if (_pendingChoosers.ContainsKey(id))
        {
            Log.Info($"Pane.resize.chooser pane={id:N} (awaiting new-pane choice)");
            _deferredSpawns[id] = (cols, rows);
            PostPaneChooser(id);
            return;
        }
        Log.Info($"Pane.resize.spawn pane={id:N} cols={cols} rows={rows}");
        SpawnPty(sess, pane, cols, rows);
    }

    private void OnSessionNew(SessionNewMsg msg)
    {
        var s = _store.AddNew();
        // Optional shell command line — when present (e.g. the page's
        // "new session with shell X", or the stability harness varying
        // shells) the session spawns that shell instead of the default.
        if (!string.IsNullOrWhiteSpace(msg.Shell)) s.Shell = msg.Shell;
        // A tab created from a project header: file it under the project and
        // open it in the repo. Cwd is set on the ROOT LEAF, not just the
        // session — ResolvePaneCwd consults the pane first, and it's the pane
        // cwd that the git signals (branch, loc, ahead) are measured against.
        // An unknown/stale project id degrades to a plain unfiled session
        // rather than spawning in a directory that no longer exists.
        var proj = msg.ProjectId is Guid pid ? _projects.ById(pid) : null;
        if (proj != null && Directory.Exists(proj.Path))
        {
            s.ProjectId = proj.Id;
            s.Cwd = proj.Path;
            s.Root.Cwd = proj.Path;
            s.Title = proj.Name;   // auto-title; OSC 7 keeps it in sync
        }
        AutoName(s.Root);
        _store.ActiveSessionId = s.Id;
        // The new session's root leaf is the active pane. PTY spawns
        // lazily on first pane.resize from the page (sized correctly).
        _activePaneId = s.Root.Id;
        _store.Save();
        PushState();
    }

    private void OnSessionSelect(SessionRef msg)
    {
        var sess = _store.Sessions.FirstOrDefault(s => s.Id == msg.Id);
        if (sess == null) return;
        _store.ActiveSessionId = msg.Id;
        // PTYs for the selected session's panes spawn lazily on first
        // pane.resize. Just point _activePaneId at a real leaf.
        _activePaneId = FirstLeaf(sess.Root)?.Id;
        _store.Save();
        PushState();
    }

    private void OnSessionRename(SessionRenameMsg msg)
    {
        var s = _store.Sessions.FirstOrDefault(x => x.Id == msg.Id);
        if (s == null) return;
        s.Title = msg.Title;
        s.IsAutoTitle = false;     // user committed a name — never auto-overwrite
        _store.Save();
        PushState();
    }

    private void OnSessionClose(SessionCloseMsg msg)
    {
        var sess = _store.Sessions.FirstOrDefault(x => x.Id == msg.Id);
        if (sess == null) return;
        var leaves = AllLeaves(sess.Root).ToList();

        var wtPath = sess.WorktreePath;
        var wtRepo = sess.WorktreeRepo;
        // Null = that was the last session. Legitimate: the page then shows an
        // empty workspace instead of us conjuring a replacement shell the same
        // instant the user closed one.
        var next = _store.Remove(sess);   // archives to Recently closed (not deleted)
        if (next == null) _activePaneId = null;

        // "Also delete the worktree folder" makes the close PERMANENT: a restored
        // session would otherwise reopen its panes into a directory that no longer
        // exists. So we purge the archive too, rather than leave a restore button
        // that's guaranteed to fail. The BRANCH survives either way — the commits
        // are the work, and closing a tab must never be able to destroy them.
        var purgeWorktree = msg.RemoveWorktree == true && wtPath.Length > 0 && wtRepo.Length > 0;
        if (purgeWorktree) _store.Purge(sess.Id);

        EnsureActivePane();
        _store.Save();
        PushState();

        // PTY teardown AFTER the UI has moved on: agent panes get their polite
        // exit (see ShutdownPaneAsync), and the worktree folder — when its
        // removal was requested — is reclaimed only once every process that
        // had a cwd inside it is dead.
        _ = CloseTeardownAsync(leaves, purgeWorktree ? wtRepo : "", purgeWorktree ? wtPath : "");
    }

    // Bring a closed session back from "Recently closed". Restores its layout
    // in the original directories and — gated by ResumeAgentsOnLaunch — arms
    // its Claude panes to `claude --resume`, driving the progress lightbox.
    private void OnSessionRestore(SessionRef msg)
    {
        var sess = _store.Restore(msg.Id);
        if (sess == null) return;
        // A restore can race the just-closed session's grace window (its PTYs
        // may still be politely exiting). Free the slots NOW — Spawn refuses a
        // pane id that still has a live PTY, so without this the restored tabs
        // would come up dead.
        foreach (var p in AllLeaves(sess.Root)) CancelPendingShutdown(p.Id);
        _activePaneId = FirstLeaf(sess.Root)?.Id;
        if (_settings.ResumeAgentsOnLaunch)
        {
            // Only arm panes whose transcript actually exists — a saved id with
            // no on-disk conversation would just error "No conversation found".
            var resumable = AllLeaves(sess.Root)
                .Where(p => !string.IsNullOrEmpty(p.ClaudeSessionId)
                            && ClaudeTranscripts.Exists(p.ClaudeSessionId!, ResolvePaneCwd(sess, p)))
                .ToList();
            foreach (var p in resumable) _armedResumePanes.Add(p.Id);
            // Open the lightbox before PushState so it's tracking these panes
            // by the time their spawns (and resumed hooks) fire.
            BeginRestoreProgress(resumable.Select(p => p.Id).ToList());
        }
        _store.Save();
        PushState();
    }

    // Permanently drop a session from "Recently closed". THIS is where a project
    // tab's worktree is finally torn down — not on close.
    //
    // Closing only archives a session (it can be restored, panes/cwd and all), so
    // deleting its worktree there would strand the restore in a directory that no
    // longer exists. Purge is the one irreversible step, so it's the honest place
    // to reclaim the tree. The BRANCH always survives: the commits are the work,
    // and no amount of tab-closing should be able to destroy them.
    private void OnSessionPurge(SessionRef msg)
    {
        var doomed = _store.ClosedSessions.FirstOrDefault(s => s.Id == msg.Id);
        var wtPath = doomed?.WorktreePath ?? "";
        var wtRepo = doomed?.WorktreeRepo ?? "";
        if (!_store.Purge(msg.Id)) return;
        _store.Save();
        PushState();
        if (wtPath.Length > 0 && wtRepo.Length > 0)
            _ = Worktree.RemoveAsync(wtRepo, wtPath);
    }

    private void OnPaneFocus(PaneRef msg)
    {
        // Stage 3b: focus shifts the active-pane marker so split-right /
        // close-pane act on the right tile.
        if (_activePaneId != msg.PaneId)
        {
            _activePaneId = msg.PaneId;
            PushState();
        }
    }

    // User changed a preference from the page (Ctrl +/- font size). Persist
    // immediately so the value survives even a hard crash; no need to
    // re-push state since the page already applied the change locally.
    private void OnPrefsSet(PrefsSetMsg msg)
    {
        var dirty = false;
        if (msg.FontSize is int n)
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
        if (msg.InspectorOpen is bool open && _settings.InspectorOpen != open)
        {
            _settings.InspectorOpen = open;
            dirty = true;
        }
        if (msg.WideLayout is bool wide && _settings.WideLayout != wide)
        {
            _settings.WideLayout = wide;
            dirty = true;
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
    // Page asked for the unpushed-commit recap behind a pane's "↑N" chip.
    // Resolve the pane's cwd + session baseline synchronously (we must not
    // touch app state off the UI thread after the await), then shell out to
    // git off-thread and reply with a commits.data message. Mirrors
    // settings.request/.data.
    private async void OnCommitsRequest(PaneRef msg)
    {
        var id = msg.PaneId;
        string cwd = "";
        string baseline = "";
        var sess = OwningSession(id);
        var pane = sess == null ? null : AllLeaves(sess.Root).FirstOrDefault(p => p.Id == id);
        if (pane != null)
        {
            _paneCwd.TryGetValue(id, out var c);
            cwd = c ?? "";
            baseline = pane.CommitBaseline;
        }
        try
        {
            // Fetch the list AND the accurate ahead count together. The list is
            // display-capped (max 50); the count is the true rev-list total, so a
            // >50-unpushed branch still shows an honest "↑N".
            var commitsT = string.IsNullOrEmpty(cwd)
                ? System.Threading.Tasks.Task.FromResult<IReadOnlyList<GitCommit>?>(null)
                : GitProc.UnpushedCommitsAsync(cwd, baseline);
            var aheadT = string.IsNullOrEmpty(cwd)
                ? System.Threading.Tasks.Task.FromResult<int?>(0)
                : GitProc.AheadAsync(cwd);
            await System.Threading.Tasks.Task.WhenAll(commitsT, aheadT);
            var commits = await commitsT;
            var freshAhead = await aheadT;
            var list = (commits ?? new List<GitCommit>()).Select(cm => new
            {
                sha = cm.ShortSha,
                subject = cm.Subject,
                committedIso = cm.CommittedIso,
                author = cm.Author,
                added = cm.Added,
                deleted = cm.Deleted,
                inSession = cm.InSession,
                files = cm.Files.Select(f => new { path = f.Path, added = f.Added, deleted = f.Deleted }).ToArray(),
            }).ToArray();
            var ahead = freshAhead ?? list.Length;
            var payload = new
            {
                type = "commits.data",
                paneId = id.ToString("D"),
                ahead,
                commits = list,
            };
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload));

            // The footer chip renders the cached pane.Ahead, which goes stale when
            // the agent pushes without a status change to trigger a git refresh —
            // so the chip could read "↑6" while this live hover reads "↑2". This
            // fetch is the moment to reconcile them: correct pane.Ahead and push,
            // so the chip snaps to the hover's (accurate) count.
            if (freshAhead is int a)
                await Dispatcher.InvokeAsync(() =>
                {
                    if (pane != null && pane.Ahead != a) { pane.Ahead = a; PushState(); }
                });
        }
        catch (Exception ex) { Log.Error("OnCommitsRequest", ex); }
    }

    // Page asked for the Inspector rail's contents for a pane. Three sources,
    // one reply:
    //   • the transcript  → the journal/activity stream + vitals
    //   • git             → the per-file change list (SessionDetailAsync; the
    //                       detail SessionStatsAsync already computed and threw
    //                       away, so this adds no git calls beyond the ahead
    //                       count the commits popover already runs)
    //   • nothing         → a shell pane; the page shows its empty state
    //
    // Deliberately request/reply rather than riding the `state` snapshot: that
    // snapshot is re-serialized IN FULL on every PostToolUse-driven change, and
    // a few hundred journal rows per pane would make a hot path quadratic.
    // Resolve pane state on the UI thread first (we must not touch it after the
    // await), then do the IO off-thread. Mirrors OnCommitsRequest.
    private async void OnInspectorRequest(PaneRef msg)
    {
        var id = msg.PaneId;
        var sess = OwningSession(id);
        var pane = sess == null ? null : AllLeaves(sess.Root).FirstOrDefault(p => p.Id == id);
        if (pane == null) return;

        _paneCwd.TryGetValue(id, out var c);
        var cwd = c ?? pane.Cwd ?? "";
        var sessionId = pane.ClaudeSessionId;
        var baseline = pane.CommitBaseline;
        var untracked = pane.UntrackedBaseline;
        // Same attribution rule the LOC chip uses: when several agents share one
        // working tree, each pane counts only the files ITS agent reported
        // touching, so the Changes list can't bill you for a neighbour's work.
        var filter = pane.DiffAttributed
            ? new HashSet<string>(pane.TouchedFiles, StringComparer.OrdinalIgnoreCase)
            : null;
        // Same working-tree scoping as the LOC chip (RefreshGitStatsAsync): the
        // Changes list restricts the uncommitted diff to the agent's own edits,
        // so a file YOU hand-edited doesn't show up as the agent's change.
        // Committed + new-untracked work stay whole.
        var touched = new HashSet<string>(pane.TouchedFiles, StringComparer.OrdinalIgnoreCase);

        try
        {
            var data = _transcripts.Read(id, sessionId, cwd);
            var detail = string.IsNullOrEmpty(cwd)
                ? null
                : await GitProc.SessionDetailAsync(baseline, cwd, untracked, filter, touched);

            var payload = new
            {
                type = "inspector.data",
                paneId = id.ToString("D"),
                hasAgent = data != null,
                events = (data?.Events ?? Array.Empty<InspectorEvent>()).Select(e => new
                {
                    kind = e.Kind,
                    ts = e.Ts,
                    text = e.Text,
                    verb = e.Verb,
                    target = e.Target,
                    note = e.Note,
                    repeat = e.Repeat,
                }).ToArray(),
                vitals = data?.Vitals is { } v ? new
                {
                    model = v.Model,
                    inputTokens = v.InputTokens,
                    outputTokens = v.OutputTokens,
                    cacheReadTokens = v.CacheReadTokens,
                    cacheWriteTokens = v.CacheWriteTokens,
                    costUsd = v.CostUsd,
                    contextTokens = v.ContextTokens,
                    contextMax = v.ContextMax,
                } : null,
                files = (detail?.Files ?? Array.Empty<GitSessionFile>()).Select(f => new
                {
                    path = f.Path,
                    added = f.Added,
                    deleted = f.Deleted,
                }).ToArray(),
                added = detail?.Added ?? 0,
                deleted = detail?.Deleted ?? 0,
            };
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch (Exception ex) { Log.Error("OnInspectorRequest", ex); }
    }

    // ── Window placement ────────────────────────────────────────────────────

    // Reopen where you left off — including on a second monitor, which is the
    // whole point (maximizing and dragging back to the right screen every launch
    // is the annoyance being fixed here).
    //
    // The one hazard is a saved position that no longer exists: unplug the second
    // monitor (or dock/undock, or change DPI) and a naive restore puts the window
    // off-screen, where it's effectively lost — you can't even grab its title bar.
    // So a restored rect must INTERSECT some current screen's working area; if it
    // doesn't, we drop the position and keep only the size, letting Windows place
    // it. Checking intersection (rather than full containment) keeps a window you
    // deliberately left half-off an edge.
    private void RestoreWindowPlacement()
    {
        var s = _settings;
        // Size first — it's always safe, and it's what we fall back to when the
        // saved position is no longer on any screen.
        if (s.WindowWidth >= MinWidth && s.WindowHeight >= MinHeight)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
        }

        var virtualScreen = new System.Windows.Rect(
            System.Windows.SystemParameters.VirtualScreenLeft,
            System.Windows.SystemParameters.VirtualScreenTop,
            System.Windows.SystemParameters.VirtualScreenWidth,
            System.Windows.SystemParameters.VirtualScreenHeight);
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop) &&
            WindowPlacement.IsReachable(
                new System.Windows.Rect(s.WindowLeft, s.WindowTop, Width, Height), virtualScreen))
        {
            // WindowStartupLocation defaults to Manual, so Left/Top are honored.
            Left = s.WindowLeft;
            Top = s.WindowTop;
        }

        // Maximized last: WPF keeps Left/Top/Width/Height as the RESTORE bounds,
        // so setting them first and then maximizing gives a correct un-maximize.
        if (s.WindowMaximized) WindowState = System.Windows.WindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        try
        {
            // When maximized (or minimized), Left/Top/Width/Height report the
            // MAXIMIZED geometry — persisting those would mean un-maximizing next
            // launch restores to a full-screen-sized "windowed" window. RestoreBounds
            // is the rect the window would return to, which is what we want to keep.
            var maximized = WindowState == System.Windows.WindowState.Maximized;
            var r = WindowState == System.Windows.WindowState.Normal
                ? new System.Windows.Rect(Left, Top, Width, Height)
                : RestoreBounds;

            if (r.Width >= MinWidth && r.Height >= MinHeight && !double.IsNaN(r.X) && !double.IsNaN(r.Y))
            {
                _settings.WindowLeft = r.X;
                _settings.WindowTop = r.Y;
                _settings.WindowWidth = r.Width;
                _settings.WindowHeight = r.Height;
            }
            _settings.WindowMaximized = maximized;
            _settings.Save();
        }
        catch (Exception ex) { Log.Error("SaveWindowPlacement", ex); }
    }

    // ── Project mode ────────────────────────────────────────────────────────

    // Sidebar mode toggle ("sessions" | "projects"). Clamped: an unknown mode
    // is dropped rather than persisted, so a stale page can't wedge the sidebar
    // into a state that renders nothing.
    private void OnUiMode(UiModeMsg msg)
    {
        var mode = msg.Mode == "projects" ? "projects" : "sessions";
        if (_settings.SidebarMode == mode) return;
        _settings.SidebarMode = mode;
        _settings.Save();
        PushState();
    }

    // Candidate repos for the registration dialog: the ones you already have
    // open (their git roots), plus a one-level scan of each configured root.
    // Async because resolving a pane's cwd to a repo root shells out to git.
    private async void OnProjectsScan()
    {
        try
        {
            // Distinct pane cwds → repo roots. A cwd deep inside a repo resolves
            // to its toplevel, so panes sitting in subdirectories still offer the
            // repo itself, and several panes in one repo collapse to one candidate.
            var cwds = _paneCwd.Values
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var roots = new List<string>();
            foreach (var cwd in cwds)
            {
                var top = await GitProc.TopLevelAsync(cwd);
                if (!string.IsNullOrWhiteSpace(top)) roots.Add(top!);
            }

            var candidates = ProjectScan.Candidates(_settings.ProjectScanRoots, roots, _projects);
            var payload = new
            {
                type = "projects.candidates",
                candidates = candidates.Select(c => new
                {
                    path = c.Path,
                    name = c.Name,
                    source = c.Source == ProjectSource.InUse ? "inUse" : "scanned",
                }).ToArray(),
                scanRoots = _settings.ProjectScanRoots.ToArray(),
            };
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch (Exception ex) { Log.Error("OnProjectsScan", ex); }
    }

    // Native folder picker → register whatever they pick. The escape hatch for
    // a repo that's neither open nor under a scan root.
    private void OnProjectBrowse()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Add project",
                InitialDirectory = _settings.ResolveDefaultCwd(),
            };
            if (dlg.ShowDialog(this) != true) return;
            AddProject(dlg.FolderName);
        }
        catch (Exception ex) { Log.Error("OnProjectBrowse", ex); }
    }

    private void OnProjectAdd(ProjectAddMsg msg) => AddProject(msg.Path, msg.Name);

    private void AddProject(string path, string? name = null)
    {
        var p = _projects.Add(path, name);
        if (p == null) return;   // empty path, or gone from disk
        _projects.Save();
        // Adopt any open session already sitting in this repo, so registering a
        // project you're working in files its tabs immediately instead of
        // leaving them stranded under "Other".
        _ = AdoptSessionsIntoProjectAsync(p);
        PushState();
    }

    // Files existing sessions under a newly-registered project by matching each
    // session's repo root to the project's path. Only touches unfiled sessions —
    // an explicit ProjectId is never reassigned behind the user's back.
    private async System.Threading.Tasks.Task AdoptSessionsIntoProjectAsync(Project p)
    {
        try
        {
            var key = ProjectStore.Normalize(p.Path);
            var changed = false;
            foreach (var sess in _store.Sessions.ToArray())
            {
                if (sess.ProjectId != null) continue;
                var cwd = PaneTree.AllLeaves(sess.Root)
                    .Select(leaf => leaf.Cwd)
                    .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
                if (string.IsNullOrWhiteSpace(cwd)) continue;
                var top = await GitProc.TopLevelAsync(cwd!);
                if (string.IsNullOrWhiteSpace(top)) continue;
                if (ProjectStore.Normalize(top!) != key) continue;
                sess.ProjectId = p.Id;
                changed = true;
            }
            if (!changed) return;
            await Dispatcher.InvokeAsync(() => { _store.Save(); PushState(); });
        }
        catch (Exception ex) { Log.Error("AdoptSessionsIntoProject", ex); }
    }

    /// Our 6-hue pane palette mapped onto the color names Claude Code's `/color`
    /// accepts (red|blue|green|yellow|purple|orange|pink|cyan). Ours is a strict
    /// SUBSET of cc's, which is the happy accident that lets a single pick drive
    /// both surfaces: the sidebar dot and the prompt bar inside the session end up
    /// the same color, so a tab looks the same from the outside and the inside.
    private static readonly string[] CcColorNames =
        { "blue", "green", "yellow", "orange", "pink", "purple" };

    // Create a tab under a project. This is the feature: a named, colored agent
    // session in its own git worktree.
    private async void OnProjectTabNew(ProjectTabNewMsg msg)
    {
        try
        {
            var proj = _projects.ById(msg.ProjectId);
            if (proj == null) return;
            var name = (msg.Name ?? "").Trim();
            if (name.Length == 0) name = proj.Name;

            // Worktree first: if it fails we must NOT fall back to opening in the
            // main checkout. That's the exact collision the worktree prevents (two
            // agents in one directory silently overwriting each other), so a
            // failure has to stop the tab, loudly.
            string cwd = proj.Path, wtPath = "", wtBranch = "";
            if (msg.Worktree == true)
            {
                var (path, branch, error) = await Worktree.CreateAsync(_settings, proj, name);
                if (error != null || path == null)
                {
                    PostToast($"Couldn't create the worktree: {error}", "error", Guid.Empty);
                    return;
                }
                cwd = wtPath = path;
                wtBranch = branch ?? "";
            }

            var s = _store.AddNew();

            // Pick the color BEFORE filing the tab under the project. AddNew has
            // already stamped a globally-unused color on the new leaf, so if it
            // were already a member of the project, the pick would treat its own
            // placeholder as "taken" and skip a hue — three tabs came out 0, 2, 3
            // instead of 0, 1, 2.
            var colorIndex = _store.PickUnusedColorForProject(proj.Id);

            s.Title = name;
            s.IsAutoTitle = false;          // a name you typed is never overwritten by OSC 7
            s.ProjectId = proj.Id;
            s.Cwd = cwd;
            s.Root.Cwd = cwd;               // the PANE cwd is what the git signals measure
            s.WorktreePath = wtPath;
            s.WorktreeRepo = wtPath.Length > 0 ? proj.Path : "";
            s.WorktreeBranch = wtBranch;
            AutoName(s.Root);
            s.Root.ColorIndex = colorIndex;   // first hue unused by THIS project's tabs

            var agent = msg.Agent ?? "claude";
            if (agent == "claude")
            {
                // Mint the session id ourselves instead of learning it from the
                // hook afterwards. It makes resume deterministic, and it's what
                // lets several agents share a repo without `--continue` lassoing
                // each other's conversations.
                var sid = Guid.NewGuid().ToString();
                s.Root.ClaudeSessionId = sid;
                // --name gets the SLUG, not the raw name. The command is spliced
                // into the shell's own startup line (pwsh -Command "…"), which
                // escapes an inner double quote as `" — so `--name "loc diff fix"`
                // reached claude as the three tokens `"loc`, `diff`, `fix"`, and
                // the session came up called `"loc`. A slug has no spaces, needs
                // no quoting, and survives every shell we spawn. (It's also what
                // Termic passes.) Our own sidebar keeps the name you typed.
                var ccName = GitProc.Slugify(name);
                if (ccName.Length == 0) ccName = "tab";
                _pendingInitialCommand[s.Root.Id] = $"claude --session-id {sid} --name {ccName}";
                _pendingCcColor[s.Root.Id] = CcColorNames[colorIndex % CcColorNames.Length];
                // Creation-time model pick. Set on the PaneNode NOW — the PTY
                // spawns lazily AFTER the PushState below (page renders → first
                // pane.resize → SpawnPty), and SpawnPty writes the wrap-claude
                // state file from pane.Model before starting the shell, so the
                // very first `claude` launch in this tab gets --model. Clamped
                // to the same allowlist as pane.model; anything else → default.
                var modelAlias = (msg.Model ?? "").Trim().ToLowerInvariant();
                if (modelAlias.Length > 0 && !ModelAliases.Contains(modelAlias)) modelAlias = "";
                s.Root.Model = modelAlias;
            }
            else if (agent == "codex")
            {
                _pendingInitialCommand[s.Root.Id] = "codex";
            }

            _store.ActiveSessionId = s.Id;
            _activePaneId = s.Root.Id;
            _store.Save();
            PushState();   // the page renders the stage → pane.resize → lazy spawn
        }
        catch (Exception ex) { Log.Error("OnProjectTabNew", ex); }
    }

    // Rename a project, or override what its worktrees get seeded with. Both
    // optional — only the keys present are applied, so the settings dialog can
    // send a partial update.
    private void OnProjectUpdate(ProjectUpdateMsg msg)
    {
        var p = _projects.ById(msg.Id);
        if (p == null) return;
        var dirty = false;

        if (msg.Name is string n && n.Trim().Length > 0 && p.Name != n.Trim())
        {
            p.Name = n.Trim();
            dirty = true;
        }
        if (msg.SeedPaths is List<string> seeds)
        {
            var clean = seeds.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            // Empty = "inherit the global list", stored as null. A project that
            // genuinely wants NOTHING seeded is vanishingly rare next to a user
            // who just cleared the box, and inheriting is the safer read.
            p.SeedPaths = clean.Count > 0 ? clean : null;
            dirty = true;
        }

        if (!dirty) return;
        _projects.Save();
        PushState();
    }

    // Unregister a project. Its tabs are NOT closed — they just fall back to
    // "Other". Destroying live sessions because a folder was unregistered would
    // be a wildly disproportionate side effect of a settings tweak.
    private void OnProjectRemove(ProjectRef msg)
    {
        if (!_projects.Remove(msg.Id)) return;
        foreach (var sess in _store.Sessions)
            if (sess.ProjectId == msg.Id) sess.ProjectId = null;
        _projects.Save();
        _store.Save();
        PushState();
    }

    private void OnSettingsRequest()
    {
        try
        {
            var shells = Shell.DetectedShells()
                .Select(s => new { name = s.Name, cmd = s.CommandLine })
                .ToArray();
            // Surface the running version + whether this copy can self-update so
            // the Settings "Updates" row shows what you're on and can disable
            // "Check now" on a dev/portable build that has no feed.
            _updates ??= new UpdateService();
            var payload = new
            {
                type = "settings.data",
                shells,
                defaultShell = _settings.DefaultShell,
                defaultCwd = _settings.DefaultCwd,
                defaultCwdResolved = _settings.ResolveDefaultCwd(),
                fontSize = _settings.FontSize,
                resumeAgentsOnLaunch = _settings.ResumeAgentsOnLaunch,
                projectScanRoots = _settings.ProjectScanRoots.ToArray(),
                worktreeRoot = _settings.WorktreeRoot,
                worktreeRootResolved = Worktree.Root(_settings),
                worktreeSeedPaths = _settings.WorktreeSeedPaths.ToArray(),
                // The registered projects, so Settings can manage them (rename,
                // per-project seeds, unregister) — the "we can do it in settings"
                // half of the registration story.
                projects = _projects.Projects.Select(p => new
                {
                    id = p.Id.ToString("D"),
                    name = p.Name,
                    path = p.Path,
                    seedPaths = (p.SeedPaths ?? new List<string>()).ToArray(),
                }).ToArray(),
                appVersion = _updates.CurrentVersion,
                updatable = _updates.IsUpdatable,
            };
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch (Exception ex) { Log.Error("OnSettingsRequest", ex); }
    }

    // Page saved the settings dialog. Each field is optional — only
    // overwrite the keys present in the message. Shell/cwd take effect on
    // the next session spawn (lazy); fontSize re-pushes state so live
    // panes pick it up via the prefs ferry in PushState.
    private void OnSettingsSave(SettingsSaveMsg msg)
    {
        var dirty = false;
        var fontChanged = false;
        if (msg.DefaultShell is string shell && _settings.DefaultShell != shell)
        {
            _settings.DefaultShell = shell;
            dirty = true;
        }
        if (msg.DefaultCwd is string cwd && _settings.DefaultCwd != cwd)
        {
            _settings.DefaultCwd = cwd;
            dirty = true;
        }
        if (msg.FontSize is int n)
        {
            var clamped = Math.Max(9, Math.Min(32, n));
            if (_settings.FontSize != clamped) { _settings.FontSize = clamped; dirty = true; fontChanged = true; }
        }
        // A "true"/"false" string from the test-IPC mirror deserializes fine —
        // PageJson's LenientBoolConverter handles both wire forms.
        if (msg.ResumeAgentsOnLaunch is bool b && _settings.ResumeAgentsOnLaunch != b)
        {
            _settings.ResumeAgentsOnLaunch = b;
            dirty = true;
        }
        // Absent key → leave as-is; an empty list is a deliberate "clear them".
        if (msg.ProjectScanRoots is List<string> roots)
        {
            var clean = roots
                .Select(r => r.Trim())
                .Where(r => r.Length > 0)
                .ToList();
            if (!clean.SequenceEqual(_settings.ProjectScanRoots))
            {
                _settings.ProjectScanRoots = clean;
                dirty = true;
            }
        }
        if (msg.WorktreeRoot is string wtRoot && _settings.WorktreeRoot != wtRoot.Trim())
        {
            // Only ever affects worktrees created FROM NOW ON — existing tabs
            // carry their own absolute WorktreePath, so moving this doesn't strand
            // them.
            _settings.WorktreeRoot = wtRoot.Trim();
            dirty = true;
        }
        if (msg.WorktreeSeedPaths is List<string> seeds)
        {
            var clean = seeds.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (!clean.SequenceEqual(_settings.WorktreeSeedPaths))
            {
                _settings.WorktreeSeedPaths = clean;
                dirty = true;
            }
        }
        if (dirty) _settings.Save();
        // Re-push so the font size propagates to live panes (no-op for
        // shell/cwd, which only matter at next spawn — but cheap).
        if (fontChanged) PushState();
    }

    // ---- Pane split / close ----------------------------------------------

    private Guid? _activePaneId;

    // offerChooser: real webview splits (the user pressing Ctrl+Shift+D) get the
    // in-pane new-pane chooser when the source pane has a known cwd. Test-IPC
    // splits pass false so the stability/perf harnesses keep their deterministic
    // auto-spawn instead of parking on a dialog nobody answers.
    private void OnPaneSplit(PaneSplitMsg msg, bool offerChooser = true)
    {
        var id = msg.PaneId;
        var orient = msg.Dir == "down" ? SplitOrientation.Horizontal : SplitOrientation.Vertical;
        var sess = OwningSession(id);
        if (sess == null) return;
        // When `url` is present the new leaf is a webview pane (iframe) —
        // the page renders an iframe for leaves whose Url is non-null
        // instead of an xterm. Otherwise the new leaf is a normal PTY
        // pane and the PTY spawns lazily on first pane.resize.
        var url = msg.Url;
        // Snapshot the source pane's cwd + agent type BEFORE we mutate the tree —
        // the new-pane chooser offers "same repo / same agent" relative to it.
        var srcPane = AllLeaves(sess.Root).FirstOrDefault(p => p.Id == id);
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
        // New-pane chooser: when splitting a TERMINAL pane (no url) whose working
        // directory we already know, offer the in-pane chooser instead of
        // silently opening a default shell. Record the source context now;
        // OnPaneResize posts the chooser when the fresh pane first measures and
        // parks its spawn until pane.chooser.choose answers.
        if (offerChooser && string.IsNullOrEmpty(url) && srcPane != null)
        {
            var srcCwd = FirstExistingDir(srcPane.Cwd);
            if (srcCwd != null)
                _pendingChoosers[newPane.Id] = (srcCwd, srcPane.AgentType);
        }
        AutoName(sess.Root);
        _activePaneId = newPane.Id;
        _store.Save();
        PushState();
    }

    /// Post the in-pane new-pane chooser to the web: a centered dialog offering
    /// "start an agent here" / "open a shell here" (both in the source pane's
    /// dir) / "open a shell in the default folder". Sent when a chooser-eligible
    /// split pane first measures; its spawn stays parked until the answer.
    private void PostPaneChooser(Guid paneId)
    {
        if (!_pendingChoosers.TryGetValue(paneId, out var ctx)) return;
        var payload = new
        {
            type = "pane.chooser",
            paneId = paneId.ToString("D"),
            cwd = ctx.cwd,
            agentType = ctx.agentType,   // "claude" / "codex" / "" → web labels the agent button
            defaultCwd = _settings.ResolveDefaultCwd(),
        };
        try { Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload)); }
        catch (Exception ex) { Log.Error("PostPaneChooser", ex); }
    }

    /// The user picked an option in the new-pane chooser (or dismissed it).
    /// Releases the pane's parked spawn into the chosen cwd + initial command,
    /// or closes the never-spawned pane on cancel.
    private void OnPaneChooserChoose(PaneChooserChooseMsg msg)
    {
        var id = msg.PaneId;
        var choice = msg.Choice;
        // Ignore a stale/duplicate answer for a pane we're no longer choosing.
        if (!_pendingChoosers.Remove(id, out var ctx)) return;
        _deferredSpawns.Remove(id, out var dims);
        var cols = dims.cols > 0 ? dims.cols : 80;
        var rows = dims.rows > 0 ? dims.rows : 24;

        var sess = OwningSession(id);
        var pane = sess == null ? null : AllLeaves(sess.Root).FirstOrDefault(p => p.Id == id);
        if (sess == null || pane == null) return;

        if (choice == "cancel")
        {
            // Undo the split — the pane never spawned, so this just removes the
            // empty leaf and collapses its split back. (CloseAndCollapse returns
            // null when id is the lone leaf; a split-created pane never is.)
            var newRoot = CloseAndCollapse(sess.Root, id);
            if (newRoot == null) return;
            sess.Root = newRoot;
            AutoName(sess.Root);
            if (_activePaneId == id) _activePaneId = FirstLeaf(sess.Root)?.Id;
            _store.Save();
            PushState();
            return;
        }

        // "agent"/"same" land in the source pane's dir; "default" in the
        // configured default. Persist the resolved cwd so a later respawn /
        // restore reopens in the same place (ResolvePaneCwd reads pane.Cwd).
        string? initialCommand = null;
        switch (choice)
        {
            case "agent":
                pane.Cwd = ctx.cwd;
                initialCommand = ctx.agentType == "codex" ? "codex" : "claude";
                break;
            case "same":
                pane.Cwd = ctx.cwd;
                break;
            default: // "default" (and any unexpected value) → plain shell, default dir
                pane.Cwd = _settings.ResolveDefaultCwd();
                break;
        }
        _store.Save();
        Log.Info($"Pane.chooser.choose pane={id:N} choice={choice} cwd={pane.Cwd}");
        SpawnPty(sess, pane, cols, rows, initialCommand);
    }

    // Open a URL in the OS default browser. Shell-execute via Process.Start
    // is the canonical Win32 way — the OS uses the user's configured
    // protocol handler (Edge / Chrome / Firefox). Validate scheme so we
    // can't be tricked into launching arbitrary `file://` or `cmd://`
    // schemes from terminal output.
    private void OnUrlOpen(UrlOpenMsg msg)
    {
        var url = msg.Url;
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
    private void OnPaneRename(PaneRenameMsg msg)
    {
        var name = msg.Name.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var sess = OwningSession(msg.PaneId);
        if (sess == null) return;
        var pane = AllLeaves(sess.Root).FirstOrDefault(p => p.Id == msg.PaneId);
        if (pane == null) return;
        pane.Name = name;
        pane.IsAutoName = false;    // user committed a name — never auto-overwrite
        pane.IsUserNamed = true;    // and the agent must never re-title it
        pane.AllowAutoName = false;
        pane.NamePrompt = null;     // drop the prompt tooltip — label is now the user's
        _store.Save();
        PushState();
    }

    private void OnPaneRecolor(PaneRecolorMsg msg)
    {
        // Palette has 6 colors; wrap user input safely.
        var idx = ((msg.ColorIndex % 6) + 6) % 6;
        var sess = OwningSession(msg.PaneId);
        if (sess == null) return;
        var pane = AllLeaves(sess.Root).FirstOrDefault(p => p.Id == msg.PaneId);
        if (pane == null) return;
        pane.ColorIndex = idx;
        _store.Save();
        PushState();
    }

    // The CLI aliases the model menu offers (Default → ""). Any other value
    // from a stale page is rejected so it can never become a --model / /model
    // token we'd inject verbatim.
    private static readonly HashSet<string> ModelAliases =
        new(StringComparer.OrdinalIgnoreCase) { "fable", "opus", "sonnet", "haiku" };

    // Per-pane Claude model pick from the pane header menu. Persist it, write
    // the wrap-claude state file (read at the NEXT `claude` launch), and — when
    // cc is already running here — type `/model <alias>` into the PTY so the
    // switch takes effect on the live session too. Empty alias = account
    // default: no --model at launch, and `/model default` for the live reset.
    private void OnPaneModel(PaneModelMsg msg)
    {
        var sess = OwningSession(msg.PaneId);
        if (sess == null) return;
        var pane = AllLeaves(sess.Root).FirstOrDefault(p => p.Id == msg.PaneId);
        if (pane == null) return;
        var alias = (msg.Model ?? "").Trim().ToLowerInvariant();
        if (alias.Length > 0 && !ModelAliases.Contains(alias)) return;

        pane.Model = alias;
        // wrap-claude reads this at the next launch; writing it here (not just
        // at spawn) is what lets a change made mid-session reach a claude the
        // user starts later in the same shell.
        ClaudeModelState.Write(pane.Id, alias);
        _store.Save();

        // Live switch: cc exposes no flag to change model mid-session, only the
        // `/model` slash command, so type it into the TUI (same mechanism as
        // the /color choreography). Only when cc is actually running here —
        // otherwise the stored selection just waits for the next launch.
        if (pane.AgentType == "claude" && _panes.Has(pane.Id))
        {
            var cmd = alias.Length == 0 ? "/model default" : $"/model {alias}";
            try { _panes.Write(pane.Id, System.Text.Encoding.UTF8.GetBytes(cmd + "\r")); }
            catch (Exception ex) { Log.Info("PaneModel", $"live switch skipped: {ex.Message}"); }
        }
        PushState();
    }

    // OSC 7 from the pane's shell — give us the cwd, we figure out the
    // branch. Cached per pane so we don't shell-out to git on every prompt
    // redraw (PowerShell fires OSC 7 on every Enter, even when cwd hasn't
    // changed). Branch update pushes state when it actually changes.
    private readonly Dictionary<Guid, string> _paneCwd = new();
    private void OnPaneCwd(PaneCwdMsg msg)
    {
        var id = msg.PaneId;
        var cwd = msg.Cwd;
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
        // A cd can change what's unpushed (different repo / branch), and this
        // is the first point at which we know the pane's cwd — so recompute the
        // git signals now. Without this the "↑N" chip would only ever appear
        // after an agent state change, so a plain-shell pane (no cc hooks) with
        // unpushed commits stayed dark. Gated above on an actual cwd change, so
        // it fires once per real cd, not on every prompt redraw. A pane with a
        // live session baseline also re-snapshots its untracked set first —
        // the old snapshot's rel paths described the previous cwd, and stale
        // ones would make the new cwd's ambient untracked files read as work.
        if (!string.IsNullOrEmpty(pane.CommitBaseline))
            _ = CaptureUntrackedBaselineAsync(pane, refresh: true);
        else
            _ = RefreshGitStatsAsync(pane);
        // Resolve the branch off-thread — git can take 50–200ms on a big
        // repo and we don't want to stall the message pump. Also try to
        // auto-name the session by the repo basename (so "tab per repo"
        // works without manual rename) — but only when the title still
        // looks like our default ("main" / "session" / "session N").
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var branch  = await GitProc.BranchAsync(cwd!);
            // The repo root is needed for the auto-title AND to file an unfiled
            // session under a registered project, so resolve it for either.
            var needTop = sess.IsAutoTitle || sess.ProjectId == null;
            var repoTop = needTop ? await GitProc.TopLevelAsync(cwd!) : null;
            await Dispatcher.InvokeAsync(() =>
            {
                var dirty = false;
                if (branch != null && pane.Branch != branch) { pane.Branch = branch; dirty = true; }
                // Continuous adoption: a session running in a registered repo files
                // itself under that project. Without this, only sessions open at the
                // moment you ADD the project ever get filed, so anything you started
                // from the flat Sessions view would sit in "Other" forever — next to a
                // project with the same name, which reads as broken. Only ever fills
                // an EMPTY ProjectId; an explicit one (a worktree tab) is never
                // reassigned behind your back.
                if (sess.ProjectId == null && !string.IsNullOrEmpty(repoTop) &&
                    _projects.ByPath(repoTop!) is Project owner)
                {
                    sess.ProjectId = owner.Id;
                    dirty = true;
                }
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
    // routes the two messages there (see BuildRouter) + the auto-title
    // callback below.

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

    private void OnPaneClose(PaneRef msg)
    {
        var id = msg.PaneId;
        // Drop any parked chooser/spawn for this pane so closing one that's
        // still showing the chooser doesn't leak into the dicts.
        _pendingChoosers.Remove(id);
        _deferredSpawns.Remove(id);
        var sess = OwningSession(id);
        if (sess == null) return;
        // Closing the only leaf in a session = close the session. The worktree is
        // KEPT here: closing the last pane is a layout action, not a decision to
        // throw the work away, and the session still goes to "Recently closed"
        // where it can be restored into that same directory.
        if (sess.Root.IsLeaf && sess.Root.Id == id)
        {
            OnSessionClose(new SessionCloseMsg { Id = sess.Id });
            return;
        }
        // Capture the leaf BEFORE the tree drops it — the polite shutdown needs
        // its AgentType to decide whether there's an agent worth exiting cleanly.
        var closingLeaf = FindPane(sess, id);
        var newRoot = CloseAndCollapse(sess.Root, id);
        if (newRoot == null) return;
        if (closingLeaf != null) _ = ShutdownPaneAsync(closingLeaf);
        else DestroyPty(id);
        sess.Root = newRoot;
        // Closing a pane evenly redistributes the survivors — a resized layout
        // would otherwise leave the remaining panes lopsided (and a collapsed
        // split's lone child keeps a stale weight). The close changes the tree
        // shape, so the web rebuilds and re-reads these weights. Mirrors the
        // Ctrl+Shift+E "even out panes" command.
        ResetWeights(sess.Root);
        AutoName(sess.Root);
        // Active pane: prefer the first remaining leaf in the same session.
        _activePaneId = FirstLeaf(sess.Root)?.Id;
        _store.Save();
        PushState();
    }

    // Returns the session that owns the given pane id, or null.
    private Session? OwningSession(Guid paneId) =>
        _store.Sessions.FirstOrDefault(s => AllLeaves(s.Root).Any(p => p.Id == paneId));

    // Tree mutations (SplitImpl / CloseAndCollapse / ResetWeights / SwapNodes /
    // InsertBesideImpl / MoveWithinParent) live in PaneTree.cs — pure,
    // window-free, unit-tested. Imported via `using static` above.

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
    private void OnPaneResizeSplit(ResizeSplitMsg msg)
    {
        var weights = msg.Weights;
        foreach (var val in weights)
            if (double.IsNaN(val) || val <= 0) return;   // reject malformed payloads

        PaneNode? split = null;
        foreach (var s in _store.Sessions)
        {
            var n = FindNode(s.Root, msg.SplitId);
            if (n != null && !n.IsLeaf) { split = n; break; }
        }
        if (split == null || split.Children.Count != weights.Length) return;
        for (int i = 0; i < weights.Length; i++) split.Children[i].Weight = weights[i];

        if (msg.Final != false) _store.Save();
    }

    // ---- Move: relocate a pane within its session ------------------------
    //
    // The web drives this on a header drag-and-drop. `src` is the dragged
    // leaf, `target` the pane it was dropped on, `edge` the drop zone:
    //   left/right  → place src beside target in a Vertical split
    //   top/bottom  → place src beside target in a Horizontal split
    //   center      → swap src and target in place
    // Within-session only (the drop targets are the active session's panes).
    private void OnPaneMove(PaneMoveMsg msg)
    {
        var srcId = msg.Src;
        var tgtId = msg.Target;
        if (srcId == tgtId) return;
        var edge = msg.Edge;
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

    // Keyboard move (Ctrl+Shift+arrows) — tree math in PaneTree.MoveWithinParent;
    // a no-op (edge / perpendicular direction) skips the save + push entirely.
    private void OnPaneMoveDir(PaneMoveDirMsg msg)
    {
        var sess = OwningSession(msg.PaneId);
        if (sess == null) return;
        if (!MoveWithinParent(sess.Root, msg.PaneId, msg.Dir)) return;
        _activePaneId = msg.PaneId;
        _store.Save();
        PushState();
    }

    // Sidebar drag-reorder — projects among projects, or a tab among its
    // project's siblings. Order is purely array position (no order field), so we
    // splice the backing collection and persist; the page re-renders from the
    // pushed state. Ids compared as Guids so the wire format can't bite us.
    private void OnSidebarReorder(SidebarReorderMsg msg)
    {
        if (!Guid.TryParse(msg.MovedId, out var moved) ||
            !Guid.TryParse(msg.TargetId, out var target) || moved == target) return;
        var after = string.Equals(msg.Edge, "after", StringComparison.Ordinal);
        var changed = msg.Kind switch
        {
            "project" => ReorderProjects(moved, target, after),
            "tab"     => ReorderTabs(moved, target, after),
            _         => false,
        };
        if (changed) PushState();
    }

    private bool ReorderProjects(Guid moved, Guid target, bool after)
    {
        var list = _projects?.Projects;
        if (list == null || !SidebarReorder.Move(list, p => p.Id, moved, target, after)) return false;
        _projects!.Save();
        return true;
    }

    private bool ReorderTabs(Guid moved, Guid target, bool after)
    {
        var c = _store.Sessions;
        // Only shuffle a tab among siblings in the SAME project — a reorder must
        // never silently re-file a tab into another project.
        var mv = c.FirstOrDefault(s => s.Id == moved);
        var tg = c.FirstOrDefault(s => s.Id == target);
        if (mv == null || tg == null || mv.ProjectId != tg.ProjectId) return false;
        if (!SidebarReorder.Move(c, s => s.Id, moved, target, after)) return false;
        _store.Save();
        return true;
    }

    // ---- Host → page push ------------------------------------------------

    // Snapshot building + aggregation rules live in StateProjection.cs (pure,
    // unit-tested). This just serializes and posts.
    private void PushState()
    {
        try
        {
            var snap = StateProjection.BuildSnapshot(
                _store, _activePaneId, _settings.FontSize, _settings.OnboardingSeen,
                _projects, _settings.SidebarMode, _usage?.CurrentLimits(), _settings.InspectorOpen,
                _settings.WideLayout);
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(snap));
        }
        catch (Exception ex) { Log.Error("PushState", ex); }
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

            // cc is still painting → hold the pending /color until output quiets.
            if (_ccColorTimers.ContainsKey(paneId)) StartCcColorTimer(paneId);
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

    // Shared verbs (session.*, pane.*, prefs.set, settings.save, …) dispatch
    // through the SAME router as the page bridge — PageJson's lenient
    // converters absorb `perch test` shipping every flag as a string, so
    // there's no per-verb payload rewriting and the two paths can't drift.
    // Only genuinely control-only verbs (pty probes, *-active conveniences,
    // render.ping, state.dump) get cases here, and they construct typed DTOs
    // instead of fake JSON.
    private void OnControlVerb(string verb, JsonElement root)
    {
        switch (verb)
        {
            case "pty.send":
                // Stage 2 compat: targets the active session's first leaf.
                {
                    var leaf = ActiveSession() is Session s ? FirstLeaf(s.Root) : null;
                    if (leaf != null && root.TryGetProperty("text", out var t))
                    {
                        _panes.Write(leaf.Id, System.Text.Encoding.UTF8.GetBytes(t.GetString() ?? ""));
                    }
                }
                break;
            case "pty.snapshot":
                {
                    var leaf = ActiveSession() is Session s ? FirstLeaf(s.Root) : null;
                    if (leaf != null)
                    {
                        var n = _panes.BytesReceived(leaf.Id);
                        Log.Info("Pty.snapshot", $"bytes={n} pid={(_panes.TryGet(leaf.Id, out var p) ? p.ProcessId : 0)}");
                    }
                }
                break;
            // Harness splits skip the new-pane chooser so spawns stay
            // deterministic (offerChooser:false) — the one shared verb that
            // can't go through the router's default binding.
            case "pane.split":
                OnPaneSplit(PageJson.Deserialize<PaneSplitMsg>(root), offerChooser: false);
                break;
            // Historical control-side spelling of pane.moveDir.
            case "pane.move-dir":
                OnPaneMoveDir(PageJson.Deserialize<PaneMoveDirMsg>(root));
                break;
            case "pane.resize-split":
                // Harness convenience: weights arrive as a comma-separated
                // string ("--weights 1.5,0.5") since `perch test` flags are
                // strings; parse to the typed message.
                {
                    var splitIdStr = root.TryGetProperty("splitId", out var si) ? si.GetString() : null;
                    var wcsv       = root.TryGetProperty("weights", out var wv) ? wv.GetString() : null;
                    if (Guid.TryParse(splitIdStr, out var splitId) && !string.IsNullOrEmpty(wcsv))
                    {
                        var weights = new List<double>();
                        foreach (var part in wcsv.Split(',', StringSplitOptions.RemoveEmptyEntries))
                            if (double.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out var d))
                                weights.Add(d);
                        OnPaneResizeSplit(new ResizeSplitMsg { SplitId = splitId, Weights = weights.ToArray(), Final = true });
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
                    OnPaneSplit(new PaneSplitMsg { PaneId = ap, Dir = dir, Url = url }, offerChooser: false);
                }
                break;
            case "pane.close-active":
                if (_activePaneId is Guid acp)
                    OnPaneClose(new PaneRef { PaneId = acp });
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
                    OnPaneIn(new PaneInMsg { PaneId = sap, B64 = b64 });
                }
                break;
            case "ui.open-settings":
                Web.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"ui.open-settings\"}");
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
                            if (_panes.TryGet(leaf.Id, out var pty))
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
                            agentState = StateProjection.StateToString(p.AgentState),
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
                // Everything else (session.*, pane.close/move/moveDir,
                // resume.decision, prefs.set, settings.save, …) is the same
                // protocol the page speaks — one dispatch table for both.
                try
                {
                    if (!_router.Dispatch(verb, root))
                        Log.Info($"ControlIpc.unknown verb={verb}");
                }
                catch (JsonException ex)
                {
                    Log.Error($"ControlIpc.json verb={verb} payload={Truncate(root.GetRawText(), 300)}", ex);
                }
                break;
        }
    }

    // ---- Helpers ---------------------------------------------------------

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
