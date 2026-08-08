using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using static Perch.PaneTree;

namespace Perch;

/// The application: every page↔host verb, pane/session/agent lifecycle,
/// git stats, boards, updates, cloud/local polling. Extracted verbatim from
/// the WPF MainWindow — hosts (WPF window, mac window) supply the native
/// surfaces via IWebViewHost / IWindowHost / IUrlPanes / IUiThread and call
/// the lifecycle methods (StartAsync, OnActivated, OnWindowResized,
/// Shutdown).
internal sealed partial class AppController
{
    private readonly IWebViewHost _web;
    private readonly IWindowHost _host;
    private readonly IUrlPanes? _urlPanes;
    private readonly IUpdateService? _updates;
    private readonly Settings _settings;

    /// Hosts read/write window geometry and init toggles through this.
    internal Settings SettingsRef => _settings;
    private readonly SessionStore _store;
    private readonly ProjectStore _projects;

    // Per-pane pty + agent-IPC lifecycles, byte counters and last-output
    // timestamps all live in PaneManager. AppController decides what to spawn
    // and reacts to its events (wired in the constructor).
    private readonly IUiThread _ui;
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

    // Ambient-output filter for the watchdog. An IDLE Claude pane is not
    // actually silent: a configured statusline repaints every few seconds, and
    // other TUI chrome ticks too. Each repaint is a single short burst — but a
    // WORKING agent is continuously chatty (it redraws its spinner ~1/sec). So
    // one burst must never read as "the agent resumed": output only counts as
    // ACTIVITY once the pane's byte counter has advanced across two
    // consecutive 1Hz watchdog ticks. Byte-counter deltas, not output
    // timestamps — a "was output recent?" window wider than the tick reads a
    // single burst as two ticks' worth, and one narrower drops legitimate
    // 1/sec spinner frames to timer jitter.
    // Without this, a pane whose turn ended hook-lessly (Esc interrupt — cc
    // fires no Stop) oscillated working↔done forever on statusline repaints,
    // its sidebar spinner restarting on every flip.
    // _activityStreak counts consecutive byte-advancing ticks per pane;
    // _lastSustainedTicks is when a streak last reached two — the level signal
    // BOTH watchdog edges are driven by (which also makes the demote immune to
    // ambient ticks resetting the silence clock). Working entries in
    // OnAgentStatus / OnPaneProbe restart that clock directly: the hook IS the
    // activity signal, and the agent's first byte can lag it by seconds.
    private readonly Dictionary<Guid, long> _lastByteCounts = new();
    private readonly Dictionary<Guid, int> _activityStreak = new();

    /// Per-pane signature of everything RefreshGitStatsAsync's answers depend
    /// on. Equal signature → equal answers → skip the git walks entirely. See
    /// the gate in RefreshGitStatsAsync for why this exists.
    private readonly Dictionary<Guid, string> _lastGitSig = new();

    /// Working-tree watchers. The .git fingerprint can't see a file an agent's
    /// Bash command created, so without these the gate above would go stale.
    private RepoWatchers? _repoWatchers;

    /// Bumped every time a working tree changes on disk. Folded into the
    /// refresh signature so a filesystem event invalidates it — this is what
    /// makes the gate event-driven rather than merely cached.
    private readonly Dictionary<string, long> _worktreeEpoch = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, long> _lastSustainedTicks = new();

    private IUiTimer? _idleWatchdog;

    private ControlIpcServer? _control;

    // Page → host dispatch table; shared by the WebView2 bridge and the
    // control pipe. Built once in the constructor (BuildRouter).
    private readonly MessageRouter _router;

    // Parses each agent pane's Claude transcript for the Inspector rail.
    // Stateful on purpose: it tails by byte offset, so re-reading a pane after
    // the agent has appended a few rows costs only those rows.
    private readonly TranscriptReader _transcripts = new();

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

    /// Per-pane state for the "Setting up…" boot cover. The cover is not just a
    /// spinner — it's the airspace in which we type `/color <name>` into cc's
    /// TUI. cc exposes no flag for the prompt-bar color, only the slash command,
    /// so it has to be typed; the cover is what stops the user's keystrokes from
    /// colliding with ours (the bug that killed the pre-overlay version).
    ///
    /// Lifecycle, per pane: ShowSetupOverlay (PTY spawned) → session-start hook
    /// lands (SessionUp) → cc's first paint QUIETS → write `/color` → cc's
    /// repaint QUIETS → hide. Each step is gated on output going quiet rather
    /// than a fixed delay, so it tracks the machine. Cap force-finishes the
    /// whole thing if any step never arrives.
    ///
    /// Escape hatch for prompts that block BEFORE the session starts (the
    /// "Do you trust the files in this folder?" gate, the first-run theme picker,
    /// a login screen): the session hook never lands for those, so a pre-session
    /// watchdog (PreQuiet) uncovers once cc paints then goes quiet with no hook,
    /// letting the user answer instead of sitting trapped behind the cover until
    /// the boot cap. See OnPreSessionQuiet.
    private sealed class SetupState
    {
        /// `/color` name still to be typed; null once written (or never wanted).
        public string? Color;
        /// cc's session-start hook has landed — it's listening, safe to type.
        public bool SessionUp;
        /// Debounce reset by every PTY output chunk; fires when cc goes quiet.
        public IUiTimer? Quiet;
        /// Hard cap — finishes the cover even if cc never reports or never quiets.
        public IUiTimer? Cap;
        /// Pre-session watchdog: cc painted a screenful then went silent BEFORE
        /// its session-start hook landed → it's parked on an interactive prompt
        /// (trust-this-folder, theme picker, login). Fires to uncover so the user
        /// can answer. Stopped the moment the hook arrives (healthy boot).
        public IUiTimer? PreQuiet;
        /// Cumulative bytes cc has emitted while SessionUp is still false — used
        /// both to gate the watchdog (a lone spinner byte is not "cc is blocked")
        /// and, under PERCH_SETUP_DIAG, to trace the pre-session paint.
        public long PreSessionBytes;
        // Setup-timing trace (surfaced by PERCH_SETUP_DIAG=1). These pinned the
        // ~1.4s inter-paint boot lulls behind the /color race; kept as the
        // standing diagnostic for this choreography, which has no cheaper probe.
        public DateTime SessionUpAt;
        public DateTime LastOutputAt;
        public int OutputChunks;
        public int QuietFires;
        public long OutputBytes;   // cumulative bytes since SessionUp
    }
    private readonly Dictionary<Guid, SetupState> _setup = new();

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
    // PERCH_SETUP_DIAG=1 → log every output chunk's gap + cumulative bytes (the
    // instrumentation that pinned the /color race). Off by default.
    private static readonly bool SetupDiagVerbose =
        Environment.GetEnvironmentVariable("PERCH_SETUP_DIAG") == "1";

    /// PHASE 1 gate — how long cc's output must stay QUIET before we accept that
    /// its boot has SETTLED (input box painted, ready for a slash command). This
    /// is the fix for the /color race: cc's cold boot is not one continuous
    /// paint but several bursts separated by ~1.3–1.4s lulls (config load, the
    /// update check, MCP init — measured on this machine). The old 150ms gate
    /// fired inside the FIRST such lull, ~250ms in, and typed /color into a cc
    /// whose input reader wasn't attached yet — the color landed as buffered
    /// text (or got eaten by a boot prompt) and the cover was pulled while cc was
    /// still painting. The settle gate has to comfortably OUTLAST those lulls, so
    /// only cc's final go-quiet (nothing more coming) trips it. Every output
    /// chunk restarts it, so on a slow machine it simply tracks cc's real pace.
    /// 2000ms leaves ~600ms of margin over the largest lull measured here (~1.4s);
    /// a boot so slow it exceeds even this is caught by SetupPaintCapMs.
    private static readonly int SetupSettleMs = EnvInt("PERCH_SETUP_SETTLE_MS", 2000);
    /// PHASE 2 gate — after /color is typed, how long the echo must stay quiet
    /// before uncovering. Short: cc is provably alive and echoing here, and the
    /// /color repaint is a single quick burst, so this only has to outlast the
    /// gap between two chunks of ONE paint.
    private static readonly int SetupEchoMs = EnvInt("PERCH_SETUP_ECHO_MS", 150);
    /// Ceiling on the FIRST phase — waiting for cc's session-start hook. A cold
    /// `claude` behind a pwsh boot measured 7.3s on this machine, so this has to
    /// be generous: cap phase 1 too tight and we uncover before cc is listening
    /// AND type /color into a PTY with no reader attached, which is precisely
    /// the race the cover exists to prevent. On expiry we uncover WITHOUT
    /// typing — cc never came up, so there is nothing to color.
    private static readonly int SetupBootCapMs = EnvInt("PERCH_SETUP_BOOTCAP_MS", 20000);
    /// Ceiling on the SECOND phase — hook landed, waiting for cc to settle then
    /// echo /color. Must exceed the real settle path (cc paints ~3s + SetupSettleMs
    /// + the echo), so a healthy boot finishes on the settle gate, not this cap;
    /// the cap only catches a cc that painted but never went quiet. On expiry we
    /// DO type the pending /color before uncovering (cc is provably up).
    private static readonly int SetupPaintCapMs = EnvInt("PERCH_SETUP_PAINTCAP_MS", 12000);
    /// PRE-SESSION gate — how long cc may stay QUIET, after painting something,
    /// while its session-start hook still hasn't landed, before we conclude it's
    /// blocked on an interactive prompt (the "Do you trust the files in this
    /// folder?" gate, the first-run theme picker, a login screen — all of which
    /// appear BEFORE any session starts) and uncover so the user can answer.
    /// Without this the cover just sits until SetupBootCapMs (20s) while focus is
    /// parked on it, trapping the user behind a prompt they can't reach. cc's
    /// session hook fires early on a healthy boot ("listening but not painted"),
    /// so a long POST-PAINT pre-hook silence isn't a normal lull — it's cc
    /// waiting on the user. Well under SetupBootCapMs so the reveal is prompt;
    /// comfortably over a normal pre-hook paint gap so a slow-but-healthy boot
    /// still settles the usual way instead of tripping this early.
    private static readonly int SetupPromptMs = EnvInt("PERCH_SETUP_PROMPT_MS", 4000);
    /// A prompt paints a screenful; a lone spinner byte does not mean cc is
    /// blocked. Only arm the pre-session watchdog once cc has painted at least
    /// this much, so a boot that dribbles a byte then loads silently isn't
    /// mistaken for a prompt. cc's trust box is far larger than this floor.
    private const int SetupPromptMinBytes = 256;
    // Panes shown in the restore-progress lightbox → whether the pane has
    // reported "alive again" (its resumed session-start hook fired). Empty when
    // no restore is in flight. _restoreTimeout force-completes a batch whose
    // panes never come back so the lightbox can't hang.
    private readonly Dictionary<Guid, bool> _restoreBatch = new();
    private IUiTimer? _restoreTimeout;

    // ---- Auto-update (Velopack) ------------------------------------------
    // Headless updater (see UpdateService). We check shortly after the page is
    // ready, then hourly, and again whenever the window regains focus after a
    // lull — so a release published while Perch sat in the background is noticed
    // the moment you come back, not up to an hour later. Each check pushes
    // `update.available` to the webview footer pill; the pill's click comes back
    // as `update.apply`. A manual `update.check` (Settings → "Check now") runs
    // the same path but also reports the result via `update.status`. Null until
    // the first check; a no-op when this copy isn't a Velopack install.
        private IUiTimer? _updateTimer;

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
    private LocalController? _local;
    private IUiTimer? _usageTimer;
    // When the last update check ran (UTC). Throttles the re-check we fire on
    // window activation so rapid alt-tabbing can't hammer the GitHub feed.
    private DateTime _lastUpdateCheckUtc = DateTime.MinValue;
    // Minimum gap between activation-triggered checks. The launch check + hourly
    // timer cover the steady state; this just catches a release published while
    // Perch sat in the background, the moment you come back to it.
    private static readonly TimeSpan UpdateRefocusThrottle = TimeSpan.FromMinutes(30);

    public AppController(
        IWebViewHost web,
        IWindowHost host,
        IUiThread ui,
        IPtyFactory ptyFactory,
        ISystemProbe? probe,
        IUrlPanes? urlPanes,
        IUpdateService? updates)
    {
        _web = web;
        _host = host;
        _ui = ui;
        _urlPanes = urlPanes;
        _updates = updates;
        _settings = Settings.Load();
        _store = SessionStore.Load();
        _projects = ProjectStore.Load();
        // Before BuildRouter: the router registers a handler that reads it.
        _boardCtrl = new BoardController(OwningSession, a => _ui.Post(a));
        WireBoardController();
        _router = BuildRouter();
        _panes = new PaneManager(_ui, ptyFactory);
        _panes.Output += PostPaneOut;
        _panes.Exited += PostPaneExit;
        _panes.AgentStatus += OnAgentStatus;
        _panes.AgentNotify += OnAgentNotify;
        _panes.AgentMeta += OnAgentMeta;
        _panes.GitBaseline += OnGitBaseline;
        _panes.GitTouched += OnGitTouched;
        _panes.GitCommitted += OnGitCommitted;
        _panes.AgentTitle += OnAgentTitle;
        _panes.NameReset += OnNameReset;
        _panes.AgentType += OnAgentType;
        _panes.AgentSession += OnAgentSession;
        _panes.CloudStamped += OnCloudStamped;
        _panes.PeerMsg += OnPeerMsg;
        // Usage poller for the model picker. Subscribe once here; a new snapshot
        // marshals back to the UI thread and re-pushes state so the menu picks
        // up freshly-disabled models. PushState guards on the webview being up.
        _usage = new UsageService();
        _usage.Updated += () => _ui.Post(PushState);
        // Cloud resources. Inert unless gcloud is installed and authenticated.
        // LookupPaneState is the ONLY thing that decides orphan-vs-live: a machine
        // whose agent session no longer maps to a live pane is one nothing is
        // using. Deliberately not time-based — an agent legitimately waits an hour
        // on a running cluster.
        _cloud = new CloudController(_ui, PostToPage, LookupPaneStateBySession);
        _cloud.Start();
        // Local dev servers. No auth to probe — a port scan is always available —
        // so this is always on, but invisible until something is listening. The
        // snapshot of live pane pids is taken on THIS (UI) thread each scan, so
        // attribution never reads session/pane state off-thread.
        _local = new LocalController(_ui, probe, PostToPage, SnapshotLivePanes, ApplyPanePorts);
        _local.Start();
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

        // Page → host bridge and crash policy. Wired here (not in StartAsync)
        // so even an early message/crash is caught.
        _web.MessageReceived += OnWebMessage;
        _web.ProcessFailed += OnWebViewProcessFailed;
        if (_urlPanes != null)
        {
            _urlPanes.AutoTitleRequested += (paneId, title) => ApplyAutoTitle(paneId, title);
            _urlPanes.Rejected += (paneId, url) => PostUrlPaneError(
                paneId, "Perch can\u2019t display this address. Only web pages and local .html files open in a pane.");
            // Only file:// misses need this: for a web failure the browser
            // renders its own (better) error page inside the pane, and
            // overlaying ours on top of that would just hide it.
            _urlPanes.Failed += (paneId, status) =>
            {
                if (_urlPanes.UrlOf(paneId)?.StartsWith("file:", StringComparison.OrdinalIgnoreCase) == true)
                    PostUrlPaneError(paneId, $"That file couldn\u2019t be opened ({status}).");
            };
        }
    }

    /// Host calls this once its window is up. Creates the webview, navigates
    /// to the app, and starts the background machinery.
    public async Task StartAsync()
    {
        var ok = await _web.InitAsync();
        if (ok)
        {
            if (Directory.Exists(_web.WebRoot)) _web.NavigateToApp(_webglDisabled);
            else _web.NavigateToString(BootstrapHtml(_web.WebRoot));
        }

        if (ControlIpcServer.IsEnabled)
        {
            _control = new ControlIpcServer(_ui, OnControlVerb);
            _control.Start();
        }
        // Idle watchdog: 1Hz sweep that demotes output-silent Working panes
        // to Done (and re-promotes its own guesses when output resumes), so
        // a missed Stop hook can't pin a pane on "working" forever.
        _idleWatchdog = _ui.CreateTimer(TimeSpan.FromSeconds(1), OnIdleWatchdogTick);
        _idleWatchdog.Start();
        _repoWatchers = new RepoWatchers(OnWorktreeChanged);
    }

    /// Host's window was activated (foregrounded).
    public void OnActivated()
    {
        SyncClipboardToWeb();
        // Re-check for updates on refocus, throttled. Catches a release
        // published while the window was in the background without
        // waiting out the hourly timer; the throttle keeps rapid
        // alt-tabbing from spamming the feed. No-op on dev/portable.
        if (DateTime.UtcNow - _lastUpdateCheckUtc >= UpdateRefocusThrottle)
            _ = CheckForUpdatesAsync();
    }

    /// Host's clipboard-change notification.
    public void OnClipboardChanged() => SyncClipboardToWeb();

    /// On every main-window size change (interactive drag, maximize, restore,
    /// snap), tell the page to re-emit each URL pane's rect. forceRefit() in
    /// url-pane.ts invalidates the rect cache so the IPC always fires, even
    /// when the page thinks size hasn't changed yet.
    public void OnWindowResized()
    {
        if (_urlPanes?.HasPanes == true)
            try { _web.PostJson("{\"type\":\"ui.urlpane.relayout\"}"); } catch { }
    }

    /// The WPF title bar's sidebar toggle (or the mac toolbar item).
    public void ToggleSidebar() => _web.PostJson("{\"type\":\"ui.sidebar.toggle\"}");

    /// Host window is closing. The host disposes its webview itself after
    /// this returns (the browser lives outside the kill-on-close job so it
    /// gets to finish writing its profile).
    public void Shutdown()
    {
        _idleWatchdog?.Stop();
        _repoWatchers?.Dispose();
        _updateTimer?.Stop();
        _usageTimer?.Stop();
        _usage?.Dispose();
        _control?.Dispose();
        _panes.Dispose();
        _store.Save();
    }

    private void EnsurePaneNames()
    {
        // PaneNode.Name doubles as the human-readable address for `perch
        // focus/send/open`. Auto-assign pane-N for leaves missing a name.
        foreach (var s in _store.Sessions) AutoName(s.Root);
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

    /// Tell the page a URL pane has no WebView2 behind it and why. Without this
    /// the pane's placeholder just stays empty, which is indistinguishable from
    /// a page that never finished loading.
    private void PostUrlPaneError(Guid paneId, string message)
    {
        _ui.Post(() =>
        {
            try
            {
                _web.PostJson(JsonSerializer.Serialize(new
                {
                    type = "ui.urlpane.error",
                    paneId = paneId.ToString("D"),
                    message,
                }));
            }
            catch (Exception ex) { Log.Error("PostUrlPaneError", ex); }
        });
    }

    private void OnWebViewProcessFailed(WebViewFailure e)
    {
        // The crash reason is otherwise invisible — we only ever saw the
        // downstream "control no longer valid" on the next post. Capture it so a
        // recurrence is actually diagnosable.
        Log.Error("WebView.ProcessFailed", new Exception($"kind={e.Kind} {e.Detail}"));

        // Only the render process exiting/hanging actually blanks the page and
        // needs us to reload. GPU / utility / frame-render failures are
        // auto-recovered by the engine itself. The browser process exiting
        // kills the whole control — Reload() can't help; the control itself
        // must be rebuilt (IWebViewHost.RecreateAsync). Bounded so a browser
        // that dies deterministically at startup can't spin us in a rebuild
        // loop.
        if (e.Kind == WebViewFailureKind.BrowserExited)
        {
            var nowUtc = DateTime.UtcNow;
            if (_browserRecreates == 0 || nowUtc - _browserRecreateWindowUtc > TimeSpan.FromMinutes(5))
            {
                _browserRecreates = 0;
                _browserRecreateWindowUtc = nowUtc;
            }
            if (++_browserRecreates > 3)
            {
                Log.Error("WebView.ProcessFailed",
                    new Exception("browser process keeps dying; stopped rebuilding"));
                return;
            }
            Log.Info("WebView.ProcessFailed", $"browser process gone — rebuilding the control (attempt {_browserRecreates})");
            _ui.Post(async () =>
            {
                try
                {
                    // URL panes hosted child views of the same dead browser;
                    // the host drops and recreates them during RecreateAsync.
                    await _web.RecreateAsync();
                    if (Directory.Exists(_web.WebRoot)) _web.NavigateToApp(_webglDisabled);
                    else _web.NavigateToString(BootstrapHtml(_web.WebRoot));
                }
                catch (Exception ex) { Log.Error("WebView.Recreate", ex); }
            });
            return;
        }
        if (e.Kind != WebViewFailureKind.RenderExited &&
            e.Kind != WebViewFailureKind.RenderUnresponsive)
            return;

        var now = DateTime.UtcNow;
        if (_rendererCrashes == 0 || now - _rendererCrashWindowUtc > TimeSpan.FromMinutes(2))
        {
            _rendererCrashes = 0;
            _rendererCrashWindowUtc = now;
        }
        _rendererCrashes++;

        // Crashing again within the window → the renderer is failing repeatedly,
        // and the likeliest culprit is the WebGL terminal path. Re-navigate with
        // WebGL off so xterm uses its DOM renderer (slower, but it won't take the
        // GPU/render process down) — keeps the app usable instead of looping on
        // grey.
        if (_rendererCrashes >= 2 && !_webglDisabled)
        {
            _webglDisabled = true;
            Log.Info("WebView.ProcessFailed", "repeated render crash — reloading with WebGL disabled");
            try { _web.NavigateToApp(disableWebgl: true); }
            catch (Exception ex) { Log.Error("WebView.ProcessFailed.navigate", ex); }
            return;
        }

        // Still crashing even with WebGL off → stop reloading so we don't thrash;
        // the page is down, so there's nothing to post. The log holds the reason.
        if (_rendererCrashes > 4)
        {
            Log.Error("WebView.ProcessFailed",
                new Exception("render process keeps crashing; stopped auto-reloading"));
            return;
        }

        // Reload to respawn the dead render process and restore the UI. The page
        // re-runs from scratch (sends `ready`); OnPageReady is idempotent and the
        // backing PTYs are still alive, so panes re-attach to their live shells.
        try { _web.Reload(); }
        catch (Exception ex) { Log.Error("WebView.ProcessFailed.reload", ex); }
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
        .Add<UrlPaneLayoutMsg>("urlpane.layout", m => _urlPanes?.OnLayout(m))
        .Add<PaneRef>("urlpane.dispose", m => _urlPanes?.OnDispose(m))
        .Add<PaneRef>("board.request", m => _boardCtrl.OnRequest(m))
        .Add<PaneRef>("board.new", OnBoardNew)
        .Add<BoardAddMsg>("board.add", m => _boardCtrl.OnAdd(m))
        .Add<BoardEditMsg>("board.edit", m => _boardCtrl.OnEdit(m))
        .Add<BoardPickFileMsg>("board.pickFile", OnBoardPickFile)
        .Add<BoardPasteMsg>("board.paste", OnBoardPaste)
        .Add<BoardMoveMsg>("board.move", m => _boardCtrl.OnMove(m))
        .Add<BoardResizeMsg>("board.resize", m => _boardCtrl.OnResize(m))
        .Add<BoardNodeRefMsg>("board.remove", m => _boardCtrl.OnRemove(m))
        .Add<BoardNodeRefMsg>("board.image", m => _boardCtrl.OnImage(m))
        .Add<UrlPaneVisibleMsg>("urlpane.visible", m => _urlPanes?.SetVisible(m.PaneId, m.Visible))
        .Add<WebPanesSuppressMsg>("ui.webpanes.suppress", m => _urlPanes?.SetSuppressed(m.Suppress))
        .Add<SessionNewMsg>("session.new", OnSessionNew)
        .Add<SessionRef>("session.select", OnSessionSelect)
        .Add<SessionRenameMsg>("session.rename", OnSessionRename)
        .Add<SessionCloseMsg>("session.close", OnSessionClose)
        .Add<SessionRef>("session.dormant", OnSessionDormant)
        .Add<SessionPairMsg>("session.pair", OnSessionPair)
        .Add<SessionRef>("session.unpair", OnSessionUnpair)
        .Add<SessionRef>("session.restore", OnSessionRestore)
        .Add<SessionRef>("session.purge", OnSessionPurge)
        .Add<ResumeDecisionMsg>("resume.decision", OnResumeDecision)
        .Add<PaneRef>("pane.focus", OnPaneFocus)
        .Add<UrlOpenMsg>("url.open", OnUrlOpen)
        .Add<PrefsSetMsg>("prefs.set", OnPrefsSet)
        .Add<PaneRef>("commits.request", OnCommitsRequest)
        .Add<PaneRef>("inspector.request", OnInspectorRequest)
        .Add<InspectorImageMsg>("inspector.image", OnInspectorImage)
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
        .Add("cloud.deleteOrphans", () => _ = _cloud?.DeleteOrphansAsync())
        .Add<LocalPanelMsg>("local.panel", m => _local?.SetPanelOpen(m.Open))
        .Add("local.refresh", () => _ = _local?.RefreshAsync())
        .Add<LocalOpenMsg>("local.open", m => OpenLocalUrl(m.Port))
        .Add<LocalKillMsg>("local.kill", m => _ = _local?.KillAsync(m.Pid))
        .Add("local.killLingering", () => _ = _local?.KillLingeringAsync());

    private void OnWebMessage(string raw)
    {
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
            _updateTimer = _ui.CreateTimer(TimeSpan.FromHours(1), () => _ = CheckForUpdatesAsync());
            _updateTimer.Start();
        }

        // Kick the usage poll now that the page can receive a re-push, then poll
        // every 5 minutes. The service's own throttle enforces the real 10/30-
        // minute cadence — the timer just gives it a chance to become due.
        _ = _usage?.RefreshIfDueAsync();
        if (_usageTimer is null)
        {
            _usageTimer = _ui.CreateTimer(TimeSpan.FromMinutes(5), () => _ = _usage?.RefreshIfDueAsync());
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
            if (_updates is null || !_updates.IsUpdatable)                  // dev run / portable unzip
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
            _web.PostJson(JsonSerializer.Serialize(
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
            _web.PostJson(JsonSerializer.Serialize(
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
            _web.PostJson(JsonSerializer.Serialize(
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
        try { _web.PostJson(JsonSerializer.Serialize(payload)); }
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
            _web.PostJson(JsonSerializer.Serialize(
                new { type = "restore.begin", panes = panes.ToArray() }));
        }
        catch (Exception ex) { Log.Error("BeginRestoreProgress", ex); }
        // Safety net: a pane that never re-fires session-start (resume failed,
        // stale id, plain shell) shouldn't pin the lightbox open.
        _restoreTimeout?.Stop();
        _restoreTimeout = _ui.CreateTimer(TimeSpan.FromSeconds(12), () => CompleteRestoreBatch(force: true));
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
        try { _web.PostJson(JsonSerializer.Serialize(new { type = "restore.done" })); }
        catch (Exception ex) { Log.Error("CompleteRestoreBatch", ex); }
    }

    private void PostRestoreProgress(Guid paneId, string state)
    {
        try
        {
            _web.PostJson(JsonSerializer.Serialize(
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
        var text = _host.ReadClipboardText();
        if (text == null) return;
        if (text.Length > MaxCachedClipboardChars) text = "";

        try
        {
            var payload = JsonSerializer.Serialize(new { type = "clipboard.text", text });
            _web.PostJson(payload);
        }
        catch (Exception ex) { Log.Error("Clipboard.Push", ex); }
    }

    /// Seat a just-created tab per the "new tab position" preference. Call it
    /// AFTER ProjectId is stamped — placement is relative to the tab's project
    /// siblings, so it can't be decided inside AddNew.
    private void PlaceNewTab(Session s)
    {
        if (_settings.NewTabPosition == "top") _store.PlaceAtProjectTop(s);
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
            // Board marker for this pane, written BEFORE the shell starts so an
            // agent's very first prompt already knows about the tab's board.
            // Same %TEMP% mechanism as the model state above, for the same
            // reason: the per-pane pipe only runs the other way.
            _boardCtrl.PublishMarkers(sess);
            // Shell.BuildStartupCommandLine injects PERCH_PIPE / PERCH_PANE_ID
            // env vars per-pane so agents inside the shell can call back
            // into our IPC layer (stage 4 reactivates that pipe).
            var startCmd = Shell.BuildStartupCommandLine(baseShell, cwd, pane.Id, initialCommand);
            _panes.Spawn(sess, pane, startCmd, cwd, cols, rows, baseShell);
            // Cover the pane while Claude Code boots (a fresh `claude --session-id`
            // or a `claude --resume`), so nothing the user types lands in cc mid-
            // boot. Dropped on the session-start hook (OnAgentSession) or failsafe.
            if (initialCommand != null &&
                initialCommand.StartsWith("claude", StringComparison.Ordinal))
                ShowSetupOverlay(pane.Id, pane.ColorIndex);
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
            {
                pane.TurnStartUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                // Restart the watchdog's silence clock too: the hook asserting
                // "working" IS the activity signal, and without this a pane
                // whose last sustained output predates the turn (it usually
                // does — the user just typed one line) demotes on the very
                // next tick, before the agent's first byte lands.
                _lastSustainedTicks[pane.Id] = System.Diagnostics.Stopwatch.GetTimestamp();
            }
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
    private void OnIdleWatchdogTick()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var changed = false;
        foreach (var sess in _store.Sessions)
        {
            foreach (var pane in AllLeaves(sess.Root))
            {
                var hasOutput = _panes.TryGetLastOutputTicks(pane.Id, out var last);
                // "Bytes arrived since the previous tick" — and they aren't a
                // resize's own redraw (see RedrawWindowTicks above).
                var bytes = _panes.BytesReceived(pane.Id);
                var advanced = _lastByteCounts.TryGetValue(pane.Id, out var prevBytes)
                               && bytes > prevBytes;
                _lastByteCounts[pane.Id] = bytes;
                var fresh = advanced
                            && (!_lastResizeTicks.TryGetValue(pane.Id, out var rz)
                                || !hasOutput || last - rz > RedrawWindowTicks);
                var streak = fresh ? _activityStreak.GetValueOrDefault(pane.Id) + 1 : 0;
                _activityStreak[pane.Id] = streak;
                var sustained = streak >= 2;

                // The silence clock runs from the last SUSTAINED activity, so an
                // ambient one-burst repaint (statusline tick) neither resets it
                // nor counts as the agent resuming. First sight seeds the clock
                // (from the last output if there was one, else "now") so a
                // just-spawned pane isn't demoted before it had a chance to draw.
                if (!_lastSustainedTicks.TryGetValue(pane.Id, out var sustainedAt))
                    sustainedAt = _lastSustainedTicks[pane.Id] = hasOutput ? last : now;
                if (sustained)
                    sustainedAt = _lastSustainedTicks[pane.Id] = now;

                var silent = (now - sustainedAt) >= IdleDemoteTicks;

                if (pane.AgentState == AgentState.Working && silent)
                {
                    pane.AgentState = AgentState.Done;
                    pane.StateInferred = true;
                    pane.TurnStartUnixMs = 0;            // left working → no elapsed
                    pane.DoneAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();  // turn-end clock for "finished · Xm ago"
                    changed = true;
                    Log.Info("IdleWatchdog", $"pane={pane.Id:N} working->done (output-silent)");
                }
                else if (pane.AgentState == AgentState.Done && pane.StateInferred && sustained)
                {
                    // Real output resumed (two consecutive ticks of it, not a
                    // lone repaint). Walk it back.
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
            // Same silence-clock restart as OnAgentStatus's Working entry: give
            // the (possibly) resumed agent its grace window before the watchdog
            // may settle an Esc'd turn to Done.
            _lastSustainedTicks[pane.Id] = System.Diagnostics.Stopwatch.GetTimestamp();
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
        if (!pane.IsTerminal) return;      // URL panes name from <title>; boards from their slug
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
        // The peer name this launch actually went out under — authoritative
        // for routing observed SendMessage targets back to this row.
        if (!string.IsNullOrWhiteSpace(msg.Name) && pane.PeerName != msg.Name)
        {
            pane.PeerName = msg.Name;
            _store.Save();
        }
        // A pairing made while this tab's Claude was down: deliver the parked
        // introduction now. A few seconds' delay so the TUI has painted and
        // the typed line lands in a real input box, not the boot noise.
        if (sess.PairIntroPending && sess.PairedWithId != null)
        {
            var sid = sess.Id;
            IUiTimer? timer = null;
            timer = _ui.CreateTimer(TimeSpan.FromSeconds(4), () =>
            {
                timer!.Stop();
                var s2 = _store.Sessions.FirstOrDefault(x => x.Id == sid);
                if (s2 == null || !s2.PairIntroPending || s2.PairedWithId == null) return;
                if (TryIntroduce(s2))
                {
                    s2.PairIntroPending = false;
                    _store.Save();
                }
            });
            timer.Start();
        }
        MarkRestorePaneReady(paneId);
        // cc is listening — but NOT yet painted. This only arms the boot cover's
        // quiet-watch; the cover drops once cc's paint settles and the pane's
        // /color has been applied (OnSetupQuiet), not here.
        NoteSetupSessionUp(paneId);
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

    /// Live panes' job objects + root shell pids, snapshotted on the UI thread
    /// for the local server scan. A dev server started in a pane is a member of
    /// that pane's job (and a descendant of its shell), so the job is how the
    /// scan attributes the server back to the pane — and the pane's ABSENCE from
    /// this list is how a server is found to have outlived the pane that spawned
    /// it (lingering).
    private IReadOnlyList<PaneProc> SnapshotLivePanes()
    {
        var list = new List<PaneProc>();
        foreach (var sess in _store.Sessions)
        {
            if (sess.ClosedAtUnixMs > 0) continue;
            foreach (var pane in AllLeaves(sess.Root))
                if (_panes.TryGet(pane.Id, out var pty) && pty.ProcessId > 0)
                    list.Add(new PaneProc(
                        pty.ProcessId,
                        pane.Id.ToString("N"),
                        pane.Name ?? "",
                        pane.AgentState.ToString().ToLowerInvariant(),
                        pty.Scope));
        }
        return list;
    }

    /// Scan results → pane state: each live pane's Ports becomes exactly the
    /// set the local scan attributed to it (empty when it serves nothing).
    /// This drives the tab/header ":port" chips and the sidebar serving pip.
    /// The scan is authoritative — a stale `meta --port` value gets corrected
    /// on the next pass. Runs on the UI thread (LocalController guarantees it).
    private void ApplyPanePorts(IReadOnlyDictionary<string, int[]> byPane)
    {
        var changed = false;
        foreach (var sess in _store.Sessions)
        {
            if (sess.ClosedAtUnixMs > 0) continue;
            foreach (var pane in AllLeaves(sess.Root))
            {
                var want = byPane.TryGetValue(pane.Id.ToString("N"), out var p)
                    ? p : Array.Empty<int>();
                if (!want.SequenceEqual(pane.Ports)) { pane.Ports = want; changed = true; }
            }
        }
        if (changed) PushState();
    }

    /// Open a localhost URL in the system default browser. Host-side (not a
    /// webview navigation) so it lands in the real browser, not a chrome popup.
    private static void OpenLocalUrl(int port)
    {
        if (port <= 0 || port > 65535) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"http://localhost:{port}/") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("local.open", ex); }
    }

    /// Post an arbitrary payload to the page. Shared by CloudController so it
    /// doesn't need a WebView2 reference of its own.
    private void PostToPage(object payload)
    {
        try { _web.PostJson(JsonSerializer.Serialize(payload)); }
        catch (Exception ex) { Log.Error("PostToPage", ex); }
    }

    // Boot cover: while a Claude Code pane starts up we show a frosted
    // "Setting up…" overlay over the pane (see setup-overlay.ts) and, underneath
    // it, type `/color <name>` into cc so the prompt bar matches the sidebar dot.
    // Typing into the raw PTY is the only way to set that color, and doing it
    // uncovered was the old bug: it raced cc's input reader and could concatenate
    // onto whatever the user had already typed. The cover owns focus for the
    // whole window, so our keystrokes are the only ones in flight.
    //
    // Shown from SpawnPty on a `claude` launch. It does NOT drop on the
    // session-start hook — that hook fires the moment cc starts, long before its
    // TUI has painted and the --name/--color have taken, so dropping there put
    // you back in an unfinished pane. See NoteSetupSessionUp / OnSetupQuiet.
    private void ShowSetupOverlay(Guid paneId, int colorIndex) => _ui.InvokeAsync(() =>
    {
        PostToPage(new { type = "pane.setup", paneId = paneId.ToString("D"), show = true, colorIndex });

        if (!_setup.TryGetValue(paneId, out var st)) _setup[paneId] = st = new SetupState();
        st.SessionUp = false;
        st.PreSessionBytes = 0;
        st.Color = CcColorNames[((colorIndex % CcColorNames.Length) + CcColorNames.Length) % CcColorNames.Length];

        st.Quiet ??= NewSetupTimer(SetupSettleMs, () => OnSetupQuiet(paneId));
        st.Cap ??= NewSetupTimer(SetupBootCapMs, () => FinishSetup(paneId, capped: true));
        st.PreQuiet ??= NewSetupTimer(SetupPromptMs, () => OnPreSessionQuiet(paneId));
        st.Quiet.Stop();          // idle until the session-start hook arms it
        st.PreQuiet.Stop();       // idle until cc paints something pre-session
        st.Cap.Stop();
        st.Cap.Interval = TimeSpan.FromMilliseconds(SetupBootCapMs);
        st.Cap.Start();
    });

    private IUiTimer NewSetupTimer(int ms, Action tick)
    {
        return _ui.CreateTimer(TimeSpan.FromMilliseconds(ms), tick);
    }

    // cc's session-start hook landed: it's up and listening. Start watching for
    // its boot to SETTLE (the phase-1 gate). Starting the debounce here (rather
    // than only on the next output chunk) means a cc that emits nothing further
    // still completes. Fires from the IPC pipe thread → marshal to the UI thread,
    // where the timers and _setup live.
    private void NoteSetupSessionUp(Guid paneId) => _ui.InvokeAsync(() =>
    {
        if (!_setup.TryGetValue(paneId, out var st)) return;
        st.SessionUp = true;
        st.SessionUpAt = DateTime.Now;
        // Healthy boot reached the session hook — cc was not blocked on a
        // pre-session prompt after all. Kill the pre-session watchdog so it can't
        // fire mid-settle and yank the cover while /color is still landing.
        st.PreQuiet?.Stop();
        if (SetupDiagVerbose)
            Log.Info("SetupDiag", $"pane {paneId:N} session hook landed; arming {SetupSettleMs}ms settle watch");
        // The generous boot cap (waiting for the hook) gives way to the paint
        // cap now that cc is provably listening.
        if (st.Cap != null)
        {
            st.Cap.Stop();
            st.Cap.Interval = TimeSpan.FromMilliseconds(SetupPaintCapMs);
            st.Cap.Start();
        }
        // Phase 1: watch for cc's boot to go quiet for SetupSettleMs (must outlast
        // cc's inter-paint boot lulls — see the SetupSettleMs note).
        if (st.Quiet != null) st.Quiet.Interval = TimeSpan.FromMilliseconds(SetupSettleMs);
        RestartSetupQuiet(st);
    });

    // Every PTY output chunk pushes the quiet deadline out — cc is still
    // painting. UI thread only (called from PostPaneOut's dispatch).
    private void NoteSetupOutput(Guid paneId, int byteCount)
    {
        if (!_setup.TryGetValue(paneId, out var st)) return;
        if (st.SessionUp)
        {
            // Every chunk restarts the settle timer — the gate fires only once cc
            // truly stops, so it tracks the machine's real pace instead of a fixed
            // delay. PERCH_SETUP_DIAG=1 logs each chunk's gap + cumulative bytes
            // (the trace that pinned the ~1.4s inter-paint boot lulls).
            var now = DateTime.Now;
            st.OutputChunks++;
            st.OutputBytes += byteCount;
            if (SetupDiagVerbose && st.LastOutputAt != default)
            {
                var gap = (now - st.LastOutputAt).TotalMilliseconds;
                Log.Info("SetupDiag", $"pane {paneId:N} chunk #{st.OutputChunks} +{(now - st.SessionUpAt).TotalMilliseconds:F0}ms gap={gap:F0}ms bytes={byteCount} cum={st.OutputBytes} (color={(st.Color != null ? "PENDING" : "typed")})");
            }
            st.LastOutputAt = now;
            RestartSetupQuiet(st);
            return;
        }
        // Pre-session: cc is emitting but its session-start hook hasn't landed. On
        // a healthy boot the hook fires within a few seconds and we never reach
        // the watchdog (NoteSetupSessionUp stops it). If cc instead paints a
        // screenful and then goes SILENT, it's parked on an interactive prompt
        // only the user can clear (trust-this-folder, theme picker, login) — arm a
        // debounce, restarted by each chunk, whose fire means "painted, then quiet
        // before the session started" → uncover (see OnPreSessionQuiet).
        st.PreSessionBytes += byteCount;
        if (SetupDiagVerbose)
            Log.Info("SetupDiag", $"pane {paneId:N} pre-session chunk bytes={byteCount} cum={st.PreSessionBytes}");
        if (st.PreSessionBytes < SetupPromptMinBytes) return;
        st.PreQuiet?.Stop();
        st.PreQuiet?.Start();
    }

    // Pre-session watchdog fired: cc painted a screenful and then went silent
    // while its session-start hook still hasn't landed. Reaching here means cc is
    // waiting on an interactive prompt that only the user can clear — the "Do you
    // trust the files in this folder?" gate, the first-run theme picker, a login
    // screen — all of which appear BEFORE the session begins. Uncover so the user
    // can answer. There's no /color to type (cc never started a session), and we
    // deliberately do NOT re-cover when the session finally does start: typing
    // /color onto a pane the user is now driving is the exact race the cover
    // exists to prevent, so we forfeit the auto-color for this one interrupted
    // boot. Runs on the UI thread (DispatcherTimer tick).
    private void OnPreSessionQuiet(Guid paneId)
    {
        if (!_setup.TryGetValue(paneId, out var st)) return;
        st.PreQuiet?.Stop();
        // The session hook raced in just as this tick was queued — the boot is
        // healthy after all; the post-session settle path owns the cover now.
        if (st.SessionUp) return;
        _setup.Remove(paneId);
        st.Quiet?.Stop();
        st.Cap?.Stop();
        Log.Info("Setup", $"pane {paneId:N} quiet before session-start ({st.PreSessionBytes}B painted, no hook) — cc is on an interactive prompt (trust/theme/login); uncovering so it can be answered");
        PostToPage(new { type = "pane.setup", paneId = paneId.ToString("D"), show = false, colorIndex = 0 });
    }

    private static void RestartSetupQuiet(SetupState st)
    {
        st.Quiet?.Stop();
        st.Quiet?.Start();
    }

    // cc's output has gone quiet. Two quiet periods happen under the cover, on
    // DIFFERENT thresholds: phase 1 waits SetupSettleMs for cc's boot to settle
    // (long — must clear its inter-paint lulls) → type /color; phase 2 waits the
    // short SetupEchoMs for the /color repaint to settle → uncover. Draining
    // st.Color is what distinguishes them, and it also makes the write one-shot,
    // so a later `/clear` — which re-fires session-start — doesn't re-type it.
    private void OnSetupQuiet(Guid paneId)
    {
        // DispatcherTimer repeats; every path below either restarts it or ends
        // the cover, so stop it up front rather than leaving one ticking on a
        // pane whose state has already been torn down.
        if (!_setup.TryGetValue(paneId, out var st)) return;
        st.Quiet?.Stop();
        st.QuietFires++;
        if (SetupDiagVerbose)
            Log.Info("SetupDiag", $"pane {paneId:N} QUIET #{st.QuietFires} at +{(DateTime.Now - st.SessionUpAt).TotalMilliseconds:F0}ms after {st.OutputChunks} chunks (color={(st.Color != null ? "about-to-type" : "already-typed")})");
        if (st.Color != null)
        {
            // Phase 1 done: cc settled. Type /color, then switch to the short echo
            // gate and wait for cc to redraw+quiet before uncovering.
            WriteCcColor(paneId, st.Color);
            st.Color = null;
            if (st.Quiet != null) st.Quiet.Interval = TimeSpan.FromMilliseconds(SetupEchoMs);
            RestartSetupQuiet(st);
            return;
        }
        FinishSetup(paneId, capped: false);
    }

    // Type `/color <name>\r` into the pane. Guarded: the pane may have closed
    // between the last output chunk and this tick.
    private void WriteCcColor(Guid paneId, string color)
    {
        try
        {
            _panes.Write(paneId, System.Text.Encoding.UTF8.GetBytes($"/color {color}\r"));
            Log.Info("CcColor", $"pane {paneId:N} → /color {color}");
        }
        catch (Exception ex) { Log.Info("CcColor", $"skipped: {ex.Message}"); }
    }

    // Tear down the boot cover. On the capped path cc never quieted (or never
    // reported), so any pending /color is typed here as a last shot before we
    // uncover — the user may see it echo, which beats losing the color.
    private void FinishSetup(Guid paneId, bool capped) => _ui.InvokeAsync(() =>
    {
        if (_setup.Remove(paneId, out var st))
        {
            st.Quiet?.Stop();
            st.Cap?.Stop();
            st.PreQuiet?.Stop();
            // Only type on the capped path if cc actually came up. Capping in
            // phase 1 means the session-start hook never arrived — writing then
            // would push /color into a PTY with no reader attached, which is the
            // exact race the cover exists to prevent.
            if (capped && st.Color != null && st.SessionUp)
            {
                Log.Info("Setup", $"pane {paneId:N} paint never quieted ({SetupPaintCapMs}ms); typing /color late");
                WriteCcColor(paneId, st.Color);
            }
            else if (capped)
                Log.Info("Setup", $"pane {paneId:N} gave up (sessionUp={st.SessionUp}); uncovering without /color");
            else Log.Info("Setup", $"pane {paneId:N} settled; uncovering");
        }
        PostToPage(new { type = "pane.setup", paneId = paneId.ToString("D"), show = false, colorIndex = 0 });
    });

    // Drop the cover without ceremony (pane closed / PTY died). No /color, no
    // waiting — there's nothing left to set up.
    private void CancelSetupOverlay(Guid paneId) => _ui.InvokeAsync(() =>
    {
        if (!_setup.Remove(paneId, out var st)) return;
        st.Quiet?.Stop();
        st.Cap?.Stop();
        st.PreQuiet?.Stop();
        PostToPage(new { type = "pane.setup", paneId = paneId.ToString("D"), show = false, colorIndex = 0 });
    });

    // New Claude session in a terminal pane (fresh launch after ctrl+c twice,
    // or `/clear`) re-arms auto-naming so the next first prompt re-titles the
    // pane to the new task. We don't wipe the current label here — it stays
    // until the next prompt replaces it. Skipped for user-named panes and for
    // "resume" (a resumed session keeps its established label).
    private void OnNameReset(Session sess, Guid paneId, NameResetMessage msg)
    {
        var pane = FindPane(sess, paneId);
        if (pane == null) return;
        if (!pane.IsTerminal) return;
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

    /// A working tree changed on disk (debounced by RepoWatcher). Bump its
    /// epoch so every gated signature for that tree differs, then refresh the
    /// panes sitting in it. This is the event that replaced the 1Hz poll: the
    /// git walks now run when something HAPPENED, not when a timer fired.
    private void OnWorktreeChanged(string root)
    {
        _ui.Post(() =>
        {
            _worktreeEpoch[root] = _worktreeEpoch.GetValueOrDefault(root, 0) + 1;
            GitProc.InvalidateCache(root);
            foreach (var sess in _store.Sessions)
                foreach (var pane in AllLeaves(sess.Root))
                    if (_paneCwd.TryGetValue(pane.Id, out var c)
                        && string.Equals(c, root, StringComparison.OrdinalIgnoreCase))
                        _ = RefreshGitStatsAsync(pane);
        });
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

        // Nothing changed since the last refresh for this pane? Then the answers
        // are the ones already on screen, and the three git walks below would
        // recompute them byte-for-byte at the cost of ~4 process launches.
        //
        // This gate is the fix for the regression that started this work. The
        // calls underneath are each fast and each defensible; what was not
        // defensible was running them on a timer whether or not anything had
        // happened. Measured 1.9 git launches per SECOND on an idle desktop.
        //
        // The signature deliberately folds in everything the results depend on,
        // not just the repo: baseline and the agent-touched set move
        // independently of .git, and a stale read of either shows the wrong
        // number in the footer. Untracked-file edits are covered by the
        // git.touched IPC, which calls GitProc.InvalidateCache.
        _repoWatchers?.Ensure(cwd);
        var sig = GitProc.RefreshSignature(cwd)
                  + "|b=" + baseline
                  + "|t=" + touched.Count
                  + "|f=" + (pathFilter?.Count ?? -1)
                  + "|w=" + _worktreeEpoch.GetValueOrDefault(cwd, 0);
        if (_lastGitSig.TryGetValue(pane.Id, out var prevSig) && prevSig == sig) return;
        _lastGitSig[pane.Id] = sig;

        // Run the git queries concurrently off the UI thread — they're
        // independent and each is a fast plumbing command. The commit count and
        // the loc size come from one walk (SessionStatsAsync) so they can't
        // disagree about what counts as this session's work.
        var statsT = hasBaseline
            ? GitProc.SessionStatsAsync(baseline, cwd, pane.UntrackedBaseline, pathFilter, touched)
            : System.Threading.Tasks.Task.FromResult<GitSessionStats?>(null);
        var aheadT = GitProc.AheadAsync(cwd);
        // The per-pane attributed split rides the same refresh: a push (or
        // rebase) shrinks the unpushed set, and the "↑N mine" counts must
        // follow without waiting for another commit to trigger them.
        var minesT = GitProc.UnpushedShasAsync(cwd);
        await System.Threading.Tasks.Task.WhenAll(statsT, aheadT, minesT);
        var stats = await statsT;
        var ahead = await aheadT;
        var mines = await minesT;
        await _ui.InvokeAsync(() =>
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
            // Reconcile the WHOLE repo, not just this pane: a sibling pane on the
            // same tree that pushed without a status change of its own would keep
            // the Max-based ↑N chip inflated. Passive path — no hover needed.
            if (ahead is int a && ReconcileAheadForCwd(cwd, a)) changed = true;
            if (mines != null && ReconcileAheadMineForCwd(cwd, mines)) changed = true;
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

    // Loud: agent is blocked on you / wants feedback. Flashes/bounces until
    // the window is foregrounded.
    private void FlashAttention() => _host.FlashAttention(loud: true);

    // Calm: a turn just finished (→ Done). One brief blink — a glance-worthy
    // "an agent freed up" ping without the nagging flash of a real attention
    // state. The host skips it when the window is already foreground. Only
    // fired from the authoritative Stop hook, never the idle watchdog's
    // inferred Done, so a silent build that momentarily looks done doesn't
    // ping you.
    private void FlashDoneGentle() => _host.FlashAttention(loud: false);

    private void PostToast(string text, string? level, Guid paneId)
    {
        _ui.Post(() =>
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
                _web.PostJson(payload);
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
        PlaceNewTab(s);
        _store.ActiveSessionId = s.Id;
        // The new session's root leaf is the active pane. PTY spawns
        // lazily on first pane.resize from the page (sized correctly).
        _activePaneId = s.Root.Id;
        _store.Save();
        PushState();
    }

    /// Put a tab to sleep: stop its panes, keep everything else. There is no
    /// inverse message — selecting the row IS the wake (see OnSessionSelect),
    /// which is what makes the sidebar's Idle group behave like a drawer rather
    /// than a mode you have to leave.
    private void OnSessionDormant(SessionRef msg)
    {
        var sess = _store.Sessions.FirstOrDefault(x => x.Id == msg.Id);
        if (sess == null || sess.Dormant) return;
        var leaves = AllLeaves(sess.Root).ToList();

        // Pick the successor BEFORE the flag flips and the row moves — after
        // that, `sess` is no longer a candidate and its index has changed.
        var wasActive = _store.ActiveSessionId == sess.Id;
        var next = wasActive ? _store.PickActiveAfter(sess) : null;

        _store.SetDormant(sess, true);

        if (wasActive)
        {
            // Same rule as closing: nearest live tab in this project, or the
            // empty workspace. Never another dormant tab — waking one is the
            // thing we're deliberately not doing.
            _store.ActiveSessionId = next?.Id;
            _activePaneId = next == null ? null : FirstLeaf(next.Root)?.Id;
        }

        _store.Save();
        PushState();

        // Teardown AFTER the UI has moved on, same as close: a Claude pane gets
        // its polite /exit so the transcript is saved and `--resume` has
        // something to come back to.
        _ = CloseTeardownAsync(leaves, "", "");
    }

    /// Bring a slept tab back. Clearing the flag re-seats it at the top of its
    /// project's active run; the PTYs respawn lazily on the page's first
    /// pane.resize, exactly as they do for any tab you haven't opened yet.
    private void WakeSession(Session sess)
    {
        _store.SetDormant(sess, false);
        // A polite exit from the sleep may still be in flight, and Spawn refuses
        // a pane id that still owns a live PTY — cut the grace short or the
        // woken tab comes up dead.
        foreach (var p in AllLeaves(sess.Root)) CancelPendingShutdown(p.Id);

        if (!_settings.ResumeAgentsOnLaunch) return;
        // Only panes whose transcript actually exists — a saved id with no
        // on-disk conversation would just error "No conversation found".
        var resumable = AllLeaves(sess.Root)
            .Where(p => !string.IsNullOrEmpty(p.ClaudeSessionId)
                        && ClaudeTranscripts.Exists(p.ClaudeSessionId!, ResolvePaneCwd(sess, p)))
            .ToList();
        if (resumable.Count == 0) return;
        foreach (var p in resumable) _armedResumePanes.Add(p.Id);
        BeginRestoreProgress(resumable.Select(p => p.Id).ToList());
    }

    private void OnSessionSelect(SessionRef msg)
    {
        var sess = _store.Sessions.FirstOrDefault(s => s.Id == msg.Id);
        if (sess == null) return;
        if (sess.Dormant) WakeSession(sess);
        // Looking at the tab is the ack for its incoming peer note — same
        // gesture that clears an unread marker anywhere else.
        if (sess.PairNoteText.Length > 0)
        {
            sess.PairNoteText = "";
            sess.PairNoteFrom = "";
            sess.PairNoteAtMs = 0;
        }
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

    // ---- Cross-session pairing -------------------------------------------
    //
    // Pairing wires two tabs together for Claude Code's cross-session
    // messaging. Perch's whole contribution is the INTRODUCTION — one typed
    // line telling each agent who its partner is — plus the name plumbing
    // (sessions launch under their tab titles, see SweepPeerNames) and the
    // rendering of observed traffic. Perch never composes a message: deciding
    // "does this change affect my partner?" is the agents' own judgment, which
    // is the entire point.

    private void OnSessionPair(SessionPairMsg msg)
    {
        var a = _store.Sessions.FirstOrDefault(x => x.Id == msg.Id);
        var b = _store.Sessions.FirstOrDefault(x => x.Id == msg.PartnerId);
        if (a == null || b == null || a.Id == b.Id) return;
        if (a.PairedWithId == b.Id && b.PairedWithId == a.Id) return;   // already paired

        // One partner per tab: pairing implicitly dissolves any existing pair
        // on either side (telling the dropped partner, so its agent stops
        // sending updates into the void).
        BreakPair(a, notifySelf: true, notifyPartner: true);
        BreakPair(b, notifySelf: true, notifyPartner: true);

        a.PairedWithId = b.Id;
        b.PairedWithId = a.Id;
        a.PairIntroPending = !TryIntroduce(a);
        b.PairIntroPending = !TryIntroduce(b);
        _store.Save();
        PushState();
        Log.Info("Pair", $"paired {a.Id:N} <-> {b.Id:N} (introA={!a.PairIntroPending} introB={!b.PairIntroPending})");
    }

    private void OnSessionUnpair(SessionRef msg)
    {
        var sess = _store.Sessions.FirstOrDefault(x => x.Id == msg.Id);
        if (sess?.PairedWithId == null) return;
        BreakPair(sess, notifySelf: true, notifyPartner: true);
        _store.Save();
        PushState();
    }

    /// Dissolve `sess`'s pair (if any) symmetrically. `notifySelf` /
    /// `notifyPartner` type a short "no longer paired" line into the running
    /// agent(s) — a closing tab skips its own (the pane is being torn down).
    private void BreakPair(Session sess, bool notifySelf, bool notifyPartner)
    {
        if (sess.PairedWithId is not Guid pid) return;
        var partner = _store.Sessions.FirstOrDefault(x => x.Id == pid);
        sess.PairedWithId = null;
        sess.PairIntroPending = false;
        if (partner != null && partner.PairedWithId == sess.Id)
        {
            partner.PairedWithId = null;
            partner.PairIntroPending = false;
            if (notifyPartner)
                TypeToClaude(partner, $"[Perch] This tab is no longer paired with \"{PeerNameOf(sess)}\"; stop sending it updates.");
        }
        if (notifySelf && partner != null)
            TypeToClaude(sess, $"[Perch] This tab is no longer paired with \"{PeerNameOf(partner)}\"; stop sending it updates.");
    }

    /// Type the pairing introduction into `sess`'s running Claude pane. False
    /// when no Claude is up here — the caller then parks it on
    /// PairIntroPending and OnAgentSession delivers it at the next launch.
    private bool TryIntroduce(Session sess)
    {
        if (sess.PairedWithId is not Guid pid) return true;
        var partner = _store.Sessions.FirstOrDefault(x => x.Id == pid);
        if (partner == null) return true;   // nothing to introduce; don't park
        var pname = PeerNameOf(partner);
        return TypeToClaude(sess,
            $"[Perch] This tab is now paired with the Claude Code session \"{pname}\" (it shows under that name in ListAgents). " +
            "When you change or finish something that could affect its work, send it a brief update with your SendMessage tool; " +
            "you may also ask it questions. It received the same note about you. No reply to this note is needed.");
    }

    /// The name another session should address this one by: the live peer name
    /// its Claude actually launched under, falling back to the sanitized tab
    /// title (the name its NEXT launch will use).
    private string PeerNameOf(Session sess)
        => AllLeaves(sess.Root).FirstOrDefault(p => p.IsTerminal && !string.IsNullOrEmpty(p.PeerName))?.PeerName
           ?? ClaudePeerNames.Sanitize(sess.Title);

    /// Type one line into the session's running Claude pane (the same PTY
    /// mechanism as the /model live switch). False when no running Claude.
    private bool TypeToClaude(Session sess, string line)
    {
        var pane = AllLeaves(sess.Root)
            .FirstOrDefault(p => p.IsTerminal && p.AgentType == "claude" && _panes.Has(p.Id));
        if (pane == null) return false;
        try
        {
            _panes.Write(pane.Id, System.Text.Encoding.UTF8.GetBytes(line + "\r"));
            return true;
        }
        catch (Exception ex)
        {
            Log.Info("Pair", $"type into {pane.Id:N} failed: {ex.Message}");
            return false;
        }
    }

    /// An observed cross-session SendMessage from `sess`'s agent. "sending"
    /// already reaches the sidebar as a "messaging <target>" activity detail
    /// (a plain status push); "sent" carries the verdict: success lands a
    /// quiet "from <sender>" note on the RECEIVING tab's row, failure a warn
    /// on the sender's. Never an attention state, never a taskbar flash —
    /// this is agents coordinating, not agents needing the user.
    private void OnPeerMsg(Session sess, Guid paneId, PeerMsgMessage msg)
    {
        if ((msg.Phase ?? "") != "sent") return;
        var target = (msg.Target ?? "").Trim();
        if (target.Length == 0) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tsess = FindSessionByPeerName(target);
        if (msg.Ok == false)
        {
            sess.PairNoteFrom  = "";
            sess.PairNoteText  = tsess?.Dormant == true
                ? $"{target} is asleep, not delivered"
                : $"Couldn't deliver to {target}";
            sess.PairNoteLevel = NotificationLevel.Warn;
            sess.PairNoteAtMs  = now;
        }
        else if (tsess != null && tsess.Id != sess.Id)
        {
            tsess.PairNoteFrom  = sess.Title;
            tsess.PairNoteText  = string.IsNullOrWhiteSpace(msg.Text) ? "sent an update" : msg.Text!;
            tsess.PairNoteLevel = NotificationLevel.Info;
            tsess.PairNoteAtMs  = now;
        }
        else return;   // target isn't one of our tabs — nothing to render
        PushState();
    }

    /// The live session whose Claude answers to `name` — matched against the
    /// per-pane peer names first, then against sanitized tab titles (covers a
    /// pane that never launched with --name, where the intro used the title).
    private Session? FindSessionByPeerName(string name)
    {
        foreach (var s in _store.Sessions)
            foreach (var p in AllLeaves(s.Root))
                if (p.IsTerminal && string.Equals(p.PeerName, name, StringComparison.OrdinalIgnoreCase))
                    return s;
        return _store.Sessions.FirstOrDefault(
            s => string.Equals(ClaudePeerNames.Sanitize(s.Title), name, StringComparison.OrdinalIgnoreCase));
    }

    /// Keep every live pane's --name file equal to its tab title (deduped
    /// app-wide), and age out stale pair notes. Runs at the top of every
    /// PushState: every title change already ends in a push, so this is the
    /// one choke point that can't miss a rename. Writes only on change.
    private void SweepPeerNames()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dirty = false;
        // Live names other panes already answer to — a NEW assignment must not
        // collide with a name some running session still owns.
        var liveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _store.Sessions)
            foreach (var p in AllLeaves(s.Root))
                if (p.IsTerminal && !string.IsNullOrEmpty(p.PeerName))
                    liveNames.Add(p.PeerName!);

        foreach (var s in _store.Sessions)
        {
            // Pair notes fade on their own after 10 minutes — they're ambient
            // traffic, not an inbox.
            if (s.PairNoteAtMs > 0 && now - s.PairNoteAtMs > 10 * 60_000)
            {
                s.PairNoteText = "";
                s.PairNoteFrom = "";
                s.PairNoteAtMs = 0;
            }

            var baseName = ClaudePeerNames.Sanitize(s.Title);
            var ordinal = 0;
            foreach (var p in AllLeaves(s.Root))
            {
                if (!p.IsTerminal) continue;
                ordinal++;
                var suffix = ordinal;
                string candidate;
                do
                {
                    candidate = suffix <= 1 ? baseName : $"{baseName} {suffix}";
                    suffix++;
                } while (used.Contains(candidate)
                         || (liveNames.Contains(candidate)
                             && !string.Equals(p.PeerName, candidate, StringComparison.OrdinalIgnoreCase)));
                used.Add(candidate);

                if (!string.Equals(_assignedPeerNames.GetValueOrDefault(p.Id), candidate, StringComparison.Ordinal))
                {
                    _assignedPeerNames[p.Id] = candidate;
                    ClaudePeerNames.Write(p.Id, candidate);
                }
                // Seed the routing name for a pane that never launched with
                // --name yet; a running session's live name is authoritative
                // (set by the session-start hook) and is never overwritten here.
                if (string.IsNullOrEmpty(p.PeerName))
                {
                    p.PeerName = candidate;
                    dirty = true;
                }
            }
        }
        if (dirty) _store.Save();
    }

    /// Last --name file content written per pane, so the sweep only touches
    /// disk on an actual change.
    private readonly Dictionary<Guid, string> _assignedPeerNames = new();

    private void OnSessionClose(SessionCloseMsg msg)
    {
        var sess = _store.Sessions.FirstOrDefault(x => x.Id == msg.Id);
        if (sess == null) return;
        // A closed tab can't keep a pair. The surviving partner is told (its
        // agent stops messaging a ghost); the closing side skips its own note —
        // that pane is about to get its polite /exit.
        BreakPair(sess, notifySelf: false, notifyPartner: true);
        var leaves = AllLeaves(sess.Root).ToList();

        var wtPath = sess.WorktreePath;
        var wtRepo = sess.WorktreeRepo;
        // Null = that was the last session. Legitimate: the page then shows an
        // empty workspace instead of us conjuring a replacement shell the same
        // instant the user closed one.
        // Null = no live tab left in this project to move to (every sibling is
        // dormant, or that was the last session). Legitimate: the page then
        // shows an empty workspace instead of us waking a shell you weren't
        // using the same instant you closed one.
        var next = _store.Remove(sess);   // archives to Recently closed (not deleted)
        if (next == null) _activePaneId = null;

        // "Also delete the worktree folder" makes the close PERMANENT: a restored
        // session would otherwise reopen its panes into a directory that no longer
        // exists. So we purge the archive too, rather than leave a restore button
        // that's guaranteed to fail. The BRANCH survives either way — the commits
        // are the work, and closing a tab must never be able to destroy them.
        var purgeWorktree = msg.RemoveWorktree == true && wtPath.Length > 0 && wtRepo.Length > 0;
        if (purgeWorktree) _store.Purge(sess.Id);

        // Only when something is actually becoming active. EnsureActivePane
        // falls back to Sessions.First() when ActiveSessionId is null, which
        // would quietly point _activePaneId back into a tab we just decided
        // NOT to activate.
        if (next != null) EnsureActivePane();
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
        if (msg.LocalPerchOnly is bool perchOnly && _settings.LocalPerchOnly != perchOnly)
        {
            _settings.LocalPerchOnly = perchOnly;
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
    /// Snap every pane that shares this repo (same cwd) to a freshly-measured
    /// ahead count. The sidebar's ↑N is a Max across a session's panes, so one
    /// stale sibling — a pane that pushed without a status change to trigger its
    /// own git refresh — keeps the chip inflated even after the pane you're
    /// looking at is corrected. Same cwd ⇒ same `@{upstream}..HEAD` ⇒ same ahead,
    /// so reconciling them together is safe. Returns whether anything changed.
    /// UI thread only (touches session/pane state).
    private bool ReconcileAheadForCwd(string cwd, int ahead)
    {
        if (string.IsNullOrEmpty(cwd)) return false;
        var changed = false;
        foreach (var s in _store.Sessions)
            foreach (var p in AllLeaves(s.Root))
                if (p.Ahead != ahead
                    && _paneCwd.TryGetValue(p.Id, out var pc)
                    && string.Equals(pc, cwd, StringComparison.OrdinalIgnoreCase))
                { p.Ahead = ahead; changed = true; }
        return changed;
    }

    /// The hook parsed a "[branch abc1234]" marker out of a `git commit` this
    /// pane's agent just ran: claim the sha for the pane (persisted — unpushed
    /// commits outlive restarts) and recompute the repo's per-pane split now,
    /// since this is exactly the moment the counts change.
    private void OnGitCommitted(Session sess, Guid paneId, GitCommitMessage msg)
    {
        var pane = FindPane(sess, paneId);
        var sha = msg.Sha?.Trim();
        if (pane == null || string.IsNullOrEmpty(sha) || sha!.Length < 7) return;
        if (!pane.CommitShas.Contains(sha, StringComparer.OrdinalIgnoreCase))
        {
            pane.CommitShas.Add(sha);
            // Bounded so sessions.json can't grow without limit under a
            // long-lived never-pushing pane; oldest claims age out first —
            // they're also the first to push and stop mattering.
            while (pane.CommitShas.Count > 200) pane.CommitShas.RemoveAt(0);
            _store.Save();
        }
        if (_paneCwd.TryGetValue(paneId, out var cwd) && !string.IsNullOrEmpty(cwd))
            _ = RefreshAheadMineAsync(cwd);
    }

    private async System.Threading.Tasks.Task RefreshAheadMineAsync(string cwd)
    {
        var unpushed = await GitProc.UnpushedShasAsync(cwd);
        if (unpushed == null) return;
        await _ui.InvokeAsync(() =>
        {
            if (ReconcileAheadMineForCwd(cwd, unpushed)) PushState();
        });
    }

    /// Recompute every same-repo pane's attributed unpushed count against a
    /// fresh `@{upstream}..HEAD` set. Sibling panes reconcile together for the
    /// same reason ReconcileAheadForCwd does: a push from one pane changes the
    /// answer for all of them. UI thread only.
    private bool ReconcileAheadMineForCwd(string cwd, IReadOnlySet<string> unpushedFull)
    {
        if (string.IsNullOrEmpty(cwd)) return false;
        var changed = false;
        foreach (var s in _store.Sessions)
            foreach (var p in AllLeaves(s.Root))
                if (_paneCwd.TryGetValue(p.Id, out var pc)
                    && string.Equals(pc, cwd, StringComparison.OrdinalIgnoreCase))
                {
                    var mine = p.CommitShas.Count == 0
                        ? 0 : GitProc.CountAttributed(unpushedFull, p.CommitShas);
                    if (p.AheadMine != mine) { p.AheadMine = mine; changed = true; }
                }
        return changed;
    }

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
            _web.PostJson(JsonSerializer.Serialize(payload));

            // The footer chip renders the cached pane.Ahead, which goes stale when
            // the agent pushes without a status change to trigger a git refresh —
            // so the chip could read "↑6" while this live hover reads "↑2". This
            // fetch is the moment to reconcile them: correct pane.Ahead and push,
            // so the chip snaps to the hover's (accurate) count.
            if (freshAhead is int a)
                await _ui.InvokeAsync(() =>
                {
                    // Fan out to every same-repo pane, not just the hovered one —
                    // otherwise the Max-based chip keeps a stale sibling's count.
                    if (ReconcileAheadForCwd(cwd, a)) PushState();
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
            _web.PostJson(JsonSerializer.Serialize(payload));
        }
        catch (Exception ex) { Log.Error("OnInspectorRequest", ex); }
    }

    // Page asked for one conversation image's bytes (rail thumbnail or lightbox
    // full-size). The journal rows carry only image IDs; resolving one means
    // re-reading its line from the transcript, decoding, and (for thumbs)
    // downscaling — file IO + image decode, so it runs off-thread. Locator is
    // resolved on the UI thread FIRST (it reads TranscriptReader's tail state,
    // which is UI-thread-owned). The host always replies, even with empty data:
    // a request that vanished would leave the page waiting on its timeout.
    private async void OnInspectorImage(InspectorImageMsg msg)
    {
        var full = string.Equals(msg.Variant, "full", StringComparison.OrdinalIgnoreCase);
        string mediaType = "", data = "";
        try
        {
            if (_transcripts.LocateImage(msg.PaneId, msg.ImageId) is { } loc)
            {
                var img = await Task.Run(() => TranscriptReader.ExtractImage(loc, thumb: !full));
                if (img is { } i) { mediaType = i.MediaType; data = i.Data; }
            }
        }
        catch (Exception ex) { Log.Error("OnInspectorImage", ex); }

        var payload = new
        {
            type = "inspector.image.data",
            paneId = msg.PaneId.ToString("D"),
            imageId = msg.ImageId,
            variant = full ? "full" : "thumb",
            mediaType,
            data,
        };
        _web.PostJson(JsonSerializer.Serialize(payload));
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
    // (Window-placement save/restore lives in the host — it owns geometry,
    // screens and maximize state. The reachability rule is WindowPlacement.
    // IsReachable; hosts read/write the Window* fields on SettingsRef.)

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
            _web.PostJson(JsonSerializer.Serialize(payload));
        }
        catch (Exception ex) { Log.Error("OnProjectsScan", ex); }
    }

    // Native folder picker → register whatever they pick. The escape hatch for
    // a repo that's neither open nor under a scan root.
    private async void OnProjectBrowse()
    {
        try
        {
            var folder = await _host.PickFolderAsync(_settings.ResolveDefaultCwd());
            if (folder != null) AddProject(folder);
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
            await _ui.InvokeAsync(() => { _store.Save(); PushState(); });
        }
        catch (Exception ex) { Log.Error("AdoptSessionsIntoProject", ex); }
    }

    /// Our 6-hue pane palette mapped onto the color names Claude Code's `/color`
    /// accepts (red|blue|green|yellow|purple|orange|pink|cyan). Ours is a strict
    /// SUBSET of cc's, which is the happy accident that lets a single pick drive
    /// both surfaces: the sidebar dot and the prompt bar inside the session end up
    /// the same color, so a tab looks the same from the outside and the inside.
    /// Index order must stay aligned with --color-pane-tag-N in tokens.css.
    private static readonly string[] CcColorNames =
        { "blue", "green", "yellow", "orange", "pink", "purple" };

    // A readable tab title for a browser tab created without a typed name:
    // the host (minus "www."), or the file name for a file:// URL. Falls back
    // to "browser" if the URL won't parse.
    private static string BrowserTabTitle(string url)
    {
        try
        {
            var u = new Uri(url.Contains("://") ? url : "https://" + url);
            var host = u.Host;
            if (string.IsNullOrEmpty(host))
            {
                var fn = System.IO.Path.GetFileName(u.LocalPath);
                return string.IsNullOrEmpty(fn) ? "browser" : fn;
            }
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? host.Substring(4) : host;
        }
        catch { return "browser"; }
    }

    // Create a tab under a project. This is the feature: a named, colored agent
    // session in its own git worktree.
    private async void OnProjectTabNew(ProjectTabNewMsg msg)
    {
        try
        {
            var proj = _projects.ById(msg.ProjectId);
            if (proj == null) return;

            // Browser tab: the root leaf is a webview, not a terminal. No name is
            // required (the webview auto-titles from <title>), no worktree, no
            // PTY, no agent. File it under the project so it groups there; the
            // color comes from the project's unused hues like any other tab.
            if ((msg.Agent ?? "") == "browser")
            {
                var url = (msg.Url ?? "").Trim();
                if (url.Length == 0) return;
                var typed = (msg.Name ?? "").Trim();
                var bTitle = typed.Length > 0 ? typed : BrowserTabTitle(url);
                var bs = _store.AddNew();
                bs.Title = bTitle;
                bs.IsAutoTitle = typed.Length == 0;   // untyped title may improve later
                bs.ProjectId = proj.Id;
                bs.Root.Url = url;
                bs.Root.ColorIndex = _store.PickUnusedColorForProject(proj.Id);
                bs.Root.Name = bTitle;
                // A typed name is the user's; keep it. An auto host-title stays
                // auto so the webview's <title> can replace it once it loads.
                bs.Root.IsUserNamed = typed.Length > 0;
                bs.Root.IsAutoName = typed.Length == 0;
                PlaceNewTab(bs);
                _store.ActiveSessionId = bs.Id;
                _activePaneId = bs.Root.Id;
                _store.Save();
                PushState();
                return;
            }

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

            PlaceNewTab(s);
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
            var payload = new
            {
                type = "settings.data",
                shells,
                defaultShell = _settings.DefaultShell,
                defaultCwd = _settings.DefaultCwd,
                defaultCwdResolved = _settings.ResolveDefaultCwd(),
                fontSize = _settings.FontSize,
                resumeAgentsOnLaunch = _settings.ResumeAgentsOnLaunch,
                newTabPosition = _settings.NewTabPosition,
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
                appVersion = _updates?.CurrentVersion,
                updatable = _updates?.IsUpdatable ?? false,
            };
            _web.PostJson(JsonSerializer.Serialize(payload));
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
        // Clamped like every other string enum here: an unknown value is
        // dropped rather than persisted, so a stale page can't wedge new tabs
        // into a placement nothing implements.
        if (msg.NewTabPosition is string np)
        {
            var pos = np == "top" ? "top" : "bottom";
            if (_settings.NewTabPosition != pos) { _settings.NewTabPosition = pos; dirty = true; }
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
        // The chooser is armed for TERMINAL leaves only. It has to be, twice
        // over: a board leaf never sends pane.resize, so PostPaneChooser would
        // never fire and the _pendingChoosers entry would sit there until the
        // pane closed; and splitting FROM a board has no cwd to offer, so the
        // fallback below covers that case rather than silently opening a shell
        // in the default folder.
        if (offerChooser && newPane.IsTerminal && srcPane != null)
        {
            // A board has no cwd of its own, so fall back to the session's when
            // the split came from one.
            var srcCwd = FirstExistingDir(srcPane.Cwd, sess.Cwd);
            if (srcCwd != null)
                _pendingChoosers[newPane.Id] = (srcCwd, srcPane.AgentType);
        }
        AutoName(sess.Root);
        _activePaneId = newPane.Id;
        _store.Save();
        PushState();
    }

    /// Split `msg.PaneId` and make the new leaf a BOARD — the tab's context
    /// staging surface. Creates the board folder on first use.
    ///
    /// Separate from OnPaneSplit rather than another optional field on it,
    /// because the two do different things: a split makes a pane, this makes a
    /// pane AND (maybe) a folder on disk, and it needs a repo to put it in.
    ///
    /// One board per tab. Asking for a second just opens another window onto
    /// the same one, which is why the folder creation is conditional.
    private void OnBoardNew(PaneRef msg)
    {
        var sess = OwningSession(msg.PaneId);
        if (sess == null) return;

        if (string.IsNullOrEmpty(sess.BoardPath))
        {
            // The board lives in the repo the tab is working in, so its paths
            // are repo-relative and an agent can open them directly. Fall back
            // through the same chain a pane spawn uses.
            var srcPane = AllLeaves(sess.Root).FirstOrDefault(p => p.Id == msg.PaneId);
            var root = FirstExistingDir(srcPane?.Cwd, sess.Cwd) ?? _settings.ResolveDefaultCwd();
            if (string.IsNullOrEmpty(root))
            {
                PostToast("Couldn't work out where to put the board", "error", msg.PaneId);
                return;
            }
            try
            {
                var store = BoardStore.Create(root, sess.Title);
                sess.BoardPath = store.Dir;
                Log.Info("Board.create", $"session={sess.Id:N} path={store.Dir}");
            }
            catch (Exception ex)
            {
                Log.Error("Board.create", ex);
                PostToast("Couldn't create the board folder", "error", msg.PaneId);
                return;
            }
        }

        var boardPane = new PaneNode
        {
            IsBoard = true,
            // Named from the board, not AutoName's "pane-N" — a board's identity
            // is its subject. IsAutoName stays true so it follows a retitle.
            Name = System.IO.Path.GetFileName(sess.BoardPath),
            ColorIndex = _store.PickUnusedColor(),
        };
        var replacement = SplitImpl(sess.Root, msg.PaneId, SplitOrientation.Vertical, boardPane);
        if (replacement == null) return;
        sess.Root = replacement;
        AutoName(sess.Root);
        _activePaneId = boardPane.Id;
        // Tell every agent pane in this tab where the board is. Written now
        // rather than at spawn so an ALREADY-RUNNING claude picks it up on its
        // next prompt — which is the whole reason the hook is UserPromptSubmit
        // and not SessionStart.
        _boardCtrl.PublishMarkers(sess);
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
        try { _web.PostJson(JsonSerializer.Serialize(payload)); }
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
    // can't be tricked into launching arbitrary schemes (`cmd://`, an .exe
    // via file://, …) from terminal output.
    //
    // http/https always allowed. file:// is allowed ONLY for a local .html/.htm
    // file: this backs the "open in default browser" action on a detected HTML
    // file in agent output. It's user-initiated (they clicked a link and chose
    // the action), and a local HTML page opens in the sandboxed default browser.
    // Any other file:// (an .exe, .ps1, .lnk, a scheme handler) is still
    // refused, so crafted output can't shell-launch something dangerous.
    private void OnUrlOpen(UrlOpenMsg msg)
    {
        var url = msg.Url;
        if (string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

        // Same rule as the browser pane — see WebUrlPolicy.
        var kind = WebUrlPolicy.Classify(url);
        var isHtmlFile = kind == WebUrlKind.HtmlFile;
        if (kind == WebUrlKind.Rejected)
        {
            Log.Info("url.open.rejected", $"scheme={uri.Scheme}");
            PostToast("Can't open that address", "error", Guid.Empty);
            return;
        }
        // A local file that isn't there any more (or was never resolvable) fails
        // inside Process.Start with a Win32Exception the user never sees. Check
        // first so the failure is a toast, not a log line.
        if (isHtmlFile && !System.IO.File.Exists(uri.LocalPath))
        {
            Log.Info("url.open.missing", $"path={uri.LocalPath}");
            PostToast($"File not found: {System.IO.Path.GetFileName(uri.LocalPath)}", "error", Guid.Empty);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                // For a file:// URL hand ShellExecute the local path so it opens
                // with the default .html handler (the browser); for web, the URL.
                FileName = isHtmlFile ? uri.LocalPath : url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("url.open", ex);
            PostToast("Couldn't open that link", "error", Guid.Empty);
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

        // Live recolor: push the new hue INTO the running session too, so the cc
        // prompt bar keeps matching the sidebar dot after a mid-session change.
        // Same mechanism (and same caveat) as the /model live switch below — cc
        // has no flag for it, only the slash command. A pane still under the boot
        // cover is skipped: its SetupState already carries a /color that
        // OnSetupQuiet will type, and we refresh that instead of double-typing.
        if (_setup.TryGetValue(pane.Id, out var st) && st.Color != null)
            st.Color = CcColorNames[idx % CcColorNames.Length];
        else if (pane.AgentType == "claude" && _panes.Has(pane.Id))
            WriteCcColor(pane.Id, CcColorNames[idx % CcColorNames.Length]);

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
            await _ui.InvokeAsync(() =>
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

    /// Boards (the context staging surface). Constructed eagerly, unlike
    /// _urlPaneCtrl, because it needs no WebView2 environment — it only reads
    /// and writes files.
    private readonly BoardController _boardCtrl;

    /// Wire the board controller's outbound events to the page. Called from the
    /// constructor; kept separate so the field initializer stays a one-liner.
    private void WireBoardController()
    {
        _boardCtrl.StateReady += (paneId, doc) => PostBoardState(paneId, doc);
        _boardCtrl.Failed += (paneId, message, fatal) => PostBoardError(paneId, message, fatal);
        _boardCtrl.ImageReady += (paneId, nodeId, data) => PostBoardImage(paneId, nodeId, data);
    }

    private void PostBoardState(Guid paneId, BoardDoc doc)
    {
        _ui.Post(() =>
        {
            try
            {
                _web.PostJson(JsonSerializer.Serialize(new
                {
                    type = "board.state",
                    paneId = paneId.ToString("D"),
                    title = doc.Title,
                    nodes = doc.Nodes.Select(n => new
                    {
                        id = n.Id,
                        kind = n.Kind,
                        // Repo-relative, exactly as it appears in board.md —
                        // falling back to the absolute path for a node the user
                        // deliberately staged from outside the project, so the
                        // card renders either way.
                        @ref = n.Ref ?? n.ExtRef,
                        // Lets the card mark external refs. Derived here rather
                        // than re-tested in the page: the page has no repo root
                        // and cannot answer "is this inside the project".
                        external = n.ExtRef != null,
                        text = n.Text,
                        source = n.Source,
                        fetchedUtc = n.FetchedUtc,
                        x = n.X,
                        y = n.Y,
                        // 0 means "use the kind's default size" — an un-resized
                        // node stores nothing, so defaults stay retunable.
                        w = n.W,
                        h = n.H,
                    }).ToArray(),
                    links = doc.Links.Select(l => new { from = l.From, to = l.To, label = l.Label }).ToArray(),
                }));
            }
            catch (Exception ex) { Log.Error("PostBoardState", ex); }
        });
    }

    /// A paste landed on a board. Reading the clipboard has to happen HERE:
    /// it's STA-only and this is the UI thread, which is the same place and for
    /// the same reason SyncClipboardToWeb reads clipboard text. The controller
    /// takes the already-read bytes so it stays free of thread affinity.
    ///
    /// An image beats text when both are present — copying a screenshot often
    /// leaves a path or some HTML on the clipboard too, and the picture is what
    /// the user meant.
    private void OnBoardPaste(BoardPasteMsg msg)
    {
        // The clipboard can be locked by another app mid-read (null result).
        // That's a "try again", not a crash.
        var clip = _host.ReadClipboardForBoard();
        if (clip == null)
        {
            PostBoardError(msg.PaneId, "Couldn't read the clipboard just then. Try again.", fatal: false);
            return;
        }
        _boardCtrl.OnPaste(msg.PaneId, clip.Value.Png, clip.Value.Text, msg.X, msg.Y);
    }

    /// The board's "add a file" button. Rooted at the project, because a board
    /// can only hold files from inside it (BoardStore.ToRepoRelative rejects the
    /// rest) — starting the dialog anywhere else would invite a pick we then
    /// have to refuse.
    ///
    /// Multi-select is on: staging four files for one task is the normal case,
    /// and the cards cascade so the last pick doesn't sit exactly on the first.
    private async void OnBoardPickFile(BoardPickFileMsg msg)
    {
        try
        {
            var root = _boardCtrl.RepoRootFor(msg.PaneId);
            if (root == null)
            {
                PostBoardError(msg.PaneId, "This tab has no board to add a file to.", fatal: false);
                return;
            }
            var files = await _host.PickFilesAsync(root);
            if (files == null || files.Length == 0) return;

            var (x, y) = (msg.X, msg.Y);
            foreach (var file in files)
            {
                _boardCtrl.OnAdd(new BoardAddMsg
                {
                    PaneId = msg.PaneId, Kind = "path", Text = file, X = x, Y = y,
                    // Choosing a file in a modal dialog is as human a gesture as
                    // Ctrl+V, so it carries the same provenance — otherwise a
                    // reference file outside the repo would be refused with
                    // "only you can add that", to the person who just did.
                    Origin = "user",
                });
                x += 24; y += 24;
            }
        }
        catch (Exception ex) { Log.Error("OnBoardPickFile", ex); }
    }

    private void PostBoardImage(Guid paneId, string nodeId, string? data)
    {
        _ui.Post(() =>
        {
            try
            {
                _web.PostJson(JsonSerializer.Serialize(new
                {
                    type = "board.image.data",
                    paneId = paneId.ToString("D"),
                    nodeId,
                    // JPEG preview, capped long edge — see ImageThumb. Null when
                    // the file is gone, so the card can say so.
                    mediaType = data == null ? "" : "image/jpeg",
                    data = data ?? "",
                }));
            }
            catch (Exception ex) { Log.Error("PostBoardImage", ex); }
        });
    }

    /// `fatal` decides how the pane shows it: true replaces the whole surface
    /// (this board cannot be opened), false is a strip along the bottom (one
    /// action failed). See BoardController.Failed.
    private void PostBoardError(Guid paneId, string message, bool fatal)
    {
        _ui.Post(() =>
        {
            try
            {
                _web.PostJson(JsonSerializer.Serialize(new
                {
                    type = "board.error",
                    paneId = paneId.ToString("D"),
                    message,
                    fatal,
                }));
            }
            catch (Exception ex) { Log.Error("PostBoardError", ex); }
        });
    }

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
        // And its board marker, so a temp file left behind can't point a future
        // pane at a board that was never its own.
        BoardController.ClearMarker(id);
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
            // Names + note aging ride the push: every title change already ends
            // in a PushState, so one sweep here keeps the per-pane --name files
            // current without hooking each rename path separately. Cheap —
            // string compares, a file write only on an actual change.
            SweepPeerNames();
            var snap = StateProjection.BuildSnapshot(
                _store, _activePaneId, _settings.FontSize, _settings.OnboardingSeen,
                _projects, _settings.SidebarMode, _usage?.CurrentLimits(), _settings.InspectorOpen,
                _settings.WideLayout, _settings.LocalPerchOnly);
            _web.PostJson(JsonSerializer.Serialize(snap));
        }
        catch (Exception ex) { Log.Error("PushState", ex); }
    }

    private void PostPaneOut(Guid paneId, ReadOnlyMemory<byte> bytes)
    {
        _ui.Post(() =>
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type   = "pane.out",
                    paneId = paneId.ToString("D"),
                    b64    = Convert.ToBase64String(bytes.Span),
                });
                _web.PostJson(payload);
            }
            catch (Exception ex) { Log.Error("PostPaneOut", ex); }

            // cc is still painting → push the boot cover's quiet deadline out.
            NoteSetupOutput(paneId, bytes.Length);
        });
    }

    private void PostPaneExit(Guid paneId, int code)
    {
        _ui.Post(() =>
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type   = "pane.exit",
                    paneId = paneId.ToString("D"),
                    code,
                });
                _web.PostJson(payload);
            }
            catch (Exception ex) { Log.Error("PostPaneExit", ex); }

            // The PTY died — there is nothing left to set up. Drop the cover now
            // rather than letting it sit there until the cap.
            CancelSetupOverlay(paneId);
        });
    }

    private void PostHostError(string message)
    {
        _ui.Post(() =>
        {
            try
            {
                var payload = JsonSerializer.Serialize(new { type = "host.error", message });
                _web.PostJson(payload);
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
            case "board.new-active":
                // Board equivalent of pane.split-active, so the harness can
                // exercise the board lifecycle without a click. Same handler
                // the header button reaches, targeting the active pane.
                if (_activePaneId is Guid bap) OnBoardNew(new PaneRef { PaneId = bap });
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
                _web.PostJson("{\"type\":\"ui.open-settings\"}");
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
                    _web.PostJson($"{{\"type\":\"render.ping\",\"id\":{id}}}");
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
