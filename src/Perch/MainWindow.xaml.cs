using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;

namespace Perch;

/// The Windows host shell: a Mica FluentWindow owning the WebView2 control,
/// clipboard watcher, taskbar flash, native dialogs and window placement.
/// Everything the app *does* lives in AppController (Perch.Core); this class
/// only supplies the native surfaces it asks for via IWebViewHost/IWindowHost.
internal partial class MainWindow : FluentWindow, IWebViewHost, IWindowHost
{
    private const string VirtualHost = "perch.local";

    private readonly string _webRoot;
    private readonly AppController _app;
    private readonly UrlPanes _urlPanes;
    private ClipboardWatcher? _clipWatch;

    public MainWindow()
    {
        InitializeComponent();
        _webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var ui = new WpfUiThread(Dispatcher);
        // The registry/policy layer is Core's; only the WebView2 mechanics
        // are ours. The webview getter stays live across crash rebuilds.
        _urlPanes = new UrlPanes(ui, new WinUrlPaneHostFactory(this, () => Web));
        _app = new AppController(
            web: this,
            host: this,
            ui: ui,
            ptyFactory: new ConPtyFactory(),
            probe: new WindowsSystemProbe(),
            urlPanes: _urlPanes,
            updates: new UpdateService());

        // Window geometry. Restored BEFORE the window is shown (SourceInitialized
        // fires after the HWND exists but before layout/render), so it opens
        // where you left it instead of visibly jumping there a frame later.
        SourceInitialized += (_, _) => RestoreWindowPlacement();
        Closing += (_, _) => SaveWindowPlacement();

        Loaded += async (_, _) =>
        {
            await _app.StartAsync();
            // Keep the page's clipboard cache fresh: on every clipboard change
            // while we're foreground (covers copies made inside Perch) and on
            // window activation (covers copies made in another app before
            // switching back). The initial sync happens at page-ready.
            _clipWatch = new ClipboardWatcher(this);
            _clipWatch.ClipboardChanged += _app.OnClipboardChanged;
            _clipWatch.Attach();
            Activated += (_, _) => _app.OnActivated();
            SizeChanged += (_, _) => _app.OnWindowResized();
        };
        Closed += (_, _) =>
        {
            _clipWatch?.Dispose();
            _app.Shutdown();
            // Start the browser's own shutdown before the process exits so the
            // profile is left clean (it lives outside the kill-on-close job —
            // see InitAsync — so it gets to finish writing).
            try { Web.Dispose(); } catch { }
        };
    }

    private void OnSidebarToggleClick(object sender, System.Windows.RoutedEventArgs e)
        => _app.ToggleSidebar();

    // ---- IWebViewHost -----------------------------------------------------

    public string WebRoot => _webRoot;

    public event Action<string>? MessageReceived;
    public event Action<WebViewFailure>? ProcessFailed;

    public async Task<bool> InitAsync()
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
            // DevTools only in Debug builds or under the test harness. A shipped
            // Release build (incl. the Store MSIX) leaves them off so an end user
            // can't open a console in the app's webview. (Audit issue #1, item 3.)
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = ControlIpcServer.IsEnabled;
#endif
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsNonClientRegionSupportEnabled = true;
            core.WebMessageReceived += OnCoreWebMessage;
            // Recover from a render-process crash instead of stranding the user
            // on a grey screen. Subscribed BEFORE navigation so even an early
            // crash is caught.
            core.ProcessFailed += OnCoreProcessFailed;

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("WebView2.Init", ex);
            System.Windows.MessageBox.Show($"WebView2 failed to initialize:\n\n{ex.Message}",
                "Perch", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return false;
        }
    }

    public void PostJson(string json)
    {
        try { Web.CoreWebView2?.PostWebMessageAsJson(json); }
        catch (Exception ex) { Log.Error("WebView2.Post", ex); }
    }

    public void NavigateToApp(bool disableWebgl)
        => Web.CoreWebView2?.Navigate(
            $"https://{VirtualHost}/index.html" + (disableWebgl ? "?nowebgl=1" : ""));

    public void NavigateToString(string html) => Web.CoreWebView2?.NavigateToString(html);

    public void Reload() => Web.CoreWebView2?.Reload();

    /// The browser process died (e.g. an access violation), taking every view
    /// with it — the WPF control is permanently dead and Reload() throws. Swap
    /// in a fresh WebView2 control and run the normal init path against it.
    public async Task RecreateAsync()
    {
        // URL panes hosted child views of the same dead browser; drop them.
        // The reloaded page re-emits urlpane.layout and they get recreated.
        _urlPanes.CloseAll();

        var grid = (System.Windows.Controls.Grid)Web.Parent;
        var slot = grid.Children.IndexOf(Web);
        grid.Children.RemoveAt(slot);
        try { Web.Dispose(); } catch { }
        Web = new Microsoft.Web.WebView2.Wpf.WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
        };
        grid.Children.Insert(slot, Web);

        await InitAsync();
    }

    private void OnCoreWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try { raw = e.TryGetWebMessageAsString(); }
        catch (Exception ex) { Log.Error("Web.OnMessage.read", ex); return; }
        MessageReceived?.Invoke(raw);
    }

    private void OnCoreProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        var kind = e.ProcessFailedKind switch
        {
            CoreWebView2ProcessFailedKind.BrowserProcessExited => WebViewFailureKind.BrowserExited,
            CoreWebView2ProcessFailedKind.RenderProcessExited => WebViewFailureKind.RenderExited,
            CoreWebView2ProcessFailedKind.RenderProcessUnresponsive => WebViewFailureKind.RenderUnresponsive,
            _ => WebViewFailureKind.Recoverable,
        };
        ProcessFailed?.Invoke(new WebViewFailure(kind,
            $"reason={e.Reason} exit={e.ExitCode} proc={e.ProcessDescription} module={e.FailureSourceModulePath}"));
    }

    // ---- IWindowHost ------------------------------------------------------

    // FlashWindowEx P/Invoke for the taskbar-attention nudge. Defined inline
    // here because it's the only place in the app that needs it.
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

    public void FlashAttention(bool loud)
    {
        if (loud) { Flash(FLASHW_ALL | FLASHW_TIMERNOFG, count: 5); return; }
        // Gentle: one taskbar blink, skipped when already foreground (there's
        // nothing to draw the eye back to).
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

    public string? ReadClipboardText()
    {
        try { return System.Windows.Clipboard.GetText(); }
        catch (Exception ex) { Log.Error("Clipboard.Read", ex); return null; }
    }

    public (byte[]? Png, string? Text)? ReadClipboardForBoard()
    {
        try
        {
            byte[]? png = null;
            string? text = null;
            if (System.Windows.Clipboard.ContainsImage())
            {
                var src = System.Windows.Clipboard.GetImage();
                if (src != null) png = WpfImageCodec.EncodePng(src);
            }
            if (png == null && System.Windows.Clipboard.ContainsText())
                text = System.Windows.Clipboard.GetText();
            return (png, text);
        }
        catch (Exception ex)
        {
            // The clipboard can be locked by another app mid-read. That's a
            // "try again", not a crash.
            Log.Error("Board.paste.clipboard", ex);
            return null;
        }
    }

    public Task<string?> PickFolderAsync(string? initialDir, string? title = null)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = title ?? "Add project",
                InitialDirectory = initialDir ?? "",
            };
            return Task.FromResult(dlg.ShowDialog(this) == true ? dlg.FolderName : null);
        }
        catch (Exception ex) { Log.Error("PickFolder", ex); return Task.FromResult<string?>(null); }
    }

    public Task<string[]?> PickFilesAsync(string? initialDir)
    {
        try
        {
            // Multi-select is on: staging four files for one task is the normal
            // case, and the cards cascade so the last pick doesn't sit exactly
            // on the first.
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Add a file to the board",
                InitialDirectory = initialDir ?? "",
                Multiselect = true,
                CheckFileExists = true,
            };
            return Task.FromResult(dlg.ShowDialog(this) == true ? dlg.FileNames : null);
        }
        catch (Exception ex) { Log.Error("PickFiles", ex); return Task.FromResult<string[]?>(null); }
    }

    // ---- Window placement -------------------------------------------------
    // The reachability rule (can you still grab the title bar?) lives in
    // Core's WindowPlacement; this is just the WPF geometry plumbing.

    private void RestoreWindowPlacement()
    {
        var s = _app.SettingsRef;
        // Size first — it's always safe, and it's what we fall back to when the
        // saved position is no longer on any screen.
        if (s.WindowWidth >= MinWidth && s.WindowHeight >= MinHeight)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
        }

        var virtualScreen = new ScreenRect(
            System.Windows.SystemParameters.VirtualScreenLeft,
            System.Windows.SystemParameters.VirtualScreenTop,
            System.Windows.SystemParameters.VirtualScreenWidth,
            System.Windows.SystemParameters.VirtualScreenHeight);
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop) &&
            WindowPlacement.IsReachable(
                new ScreenRect(s.WindowLeft, s.WindowTop, Width, Height), virtualScreen))
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
            var s = _app.SettingsRef;
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
                s.WindowLeft = r.X;
                s.WindowTop = r.Y;
                s.WindowWidth = r.Width;
                s.WindowHeight = r.Height;
            }
            s.WindowMaximized = maximized;
            s.Save();
        }
        catch (Exception ex) { Log.Error("SaveWindowPlacement", ex); }
    }
}

