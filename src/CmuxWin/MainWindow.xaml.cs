using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EasyWindowsTerminalControl;
using Microsoft.Terminal.Wpf;
using Wpf.Ui.Controls;

namespace CmuxWin;

public partial class MainWindow : FluentWindow
{
    private readonly Settings _settings;
    private readonly SessionStore _store;

    private readonly Dictionary<Guid, FrameworkElement> _sessionRoots = new();
    private readonly Dictionary<Guid, EasyTerminalControl> _paneTerminals = new();
    private readonly Dictionary<Guid, Border> _paneBorders = new();
    private readonly Dictionary<Guid, Guid> _paneToSession = new();
    // Tracked shell PID per pane (grandchild under conhost). When this dies (`exit`),
    // we close the pane. Tracking the shell instead of conhost is essential because
    // conhost lingers briefly to show "Session Terminated" after the shell exits.
    private readonly Dictionary<Guid, int> _paneShellPid = new();
    private readonly Dictionary<Guid, TerminalClickHook> _paneClickHook = new();
    private readonly Dictionary<Guid, Microsoft.Web.WebView2.Wpf.WebView2> _paneWebViews = new();

    private ClipboardWatcher? _clipboard;
    private DispatcherTimer? _toastTimer;

    private Session? _active;
    private Guid? _activePaneId;
    private DispatcherTimer? _previewTimer;

    // Drag-reorder state
    private System.Windows.Point _dragStart;
    private Session? _dragSession;

    public ICommand SplitRightCommand { get; }
    public ICommand SplitDownCommand { get; }
    public ICommand CloseActivePaneCommand { get; }
    public ICommand RenameActiveSessionCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ZoomResetCommand { get; }
    public ICommand ShowUrlPaletteCommand { get; }

    private const int FontSizeMin = 8;
    private const int FontSizeMax = 32;
    private const int FontSizeDefault = 13;

    public MainWindow()
    {
        _settings = Settings.Load();
        _store = SessionStore.Load();

        SplitRightCommand = new RelayCommand(_ => SplitActive(SplitOrientation.Vertical));
        SplitDownCommand = new RelayCommand(_ => SplitActive(SplitOrientation.Horizontal));
        CloseActivePaneCommand = new RelayCommand(_ => CloseActivePane());
        RenameActiveSessionCommand = new RelayCommand(_ => { if (_active != null) _active.IsEditing = true; });
        ZoomInCommand = new RelayCommand(_ => AdjustFontSize(+1));
        ZoomOutCommand = new RelayCommand(_ => AdjustFontSize(-1));
        ZoomResetCommand = new RelayCommand(_ => SetFontSize(FontSizeDefault));
        ShowUrlPaletteCommand = new RelayCommand(_ => ShowUrlPalette());

        InitializeComponent();
        DataContext = this;

        SessionList.ItemsSource = _store.Sessions;
        BuildShellFlyout();

        var restore = _store.Sessions.FirstOrDefault(s => s.Id == _store.ActiveSessionId)
                      ?? _store.Sessions.FirstOrDefault();
        if (restore != null) SessionList.SelectedItem = restore;

        ApplySettings();
        Closing += OnClosing;
        Loaded += (_, _) =>
        {
            StartShellHealthPolling();
            _clipboard = new ClipboardWatcher(this);
            _clipboard.ClipboardChanged += OnClipboardChanged;
            _clipboard.Attach();
        };
    }

    // ----- Shell picker flyout -----

    private void BuildShellFlyout()
    {
        NewSessionFlyout.Items.Clear();
        foreach (var shell in Shell.DetectedShells())
        {
            var mi = new System.Windows.Controls.MenuItem { Header = shell.Name, Tag = shell.CommandLine };
            mi.Click += (_, _) =>
            {
                var s = _store.AddNew();
                s.Shell = shell.CommandLine;
                SessionList.SelectedItem = s;
                SessionList.ScrollIntoView(s);
            };
            NewSessionFlyout.Items.Add(mi);
        }
    }

    // ----- Session selection / materialization -----

    private void OnSessionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not Session s) return;
        if (_active?.Id == s.Id) return;

        if (_active != null && _sessionRoots.TryGetValue(_active.Id, out var prev))
            prev.Visibility = Visibility.Collapsed;

        if (!_sessionRoots.TryGetValue(s.Id, out var root))
        {
            root = BuildSessionContainer(s);
            _sessionRoots[s.Id] = root;
            TerminalHost.Children.Add(root);
        }
        root.Visibility = Visibility.Visible;

        // Swap PropertyChanged hook so the status bar updates on rename.
        if (_active != null) _active.PropertyChanged -= OnActiveSessionPropertyChanged;
        _active = s;
        _active.PropertyChanged += OnActiveSessionPropertyChanged;
        _store.ActiveSessionId = s.Id;

        var firstLeaf = FirstLeaf(s.Root);
        if (firstLeaf != null) SetActivePane(firstLeaf.Id);

        UpdateStatusBar();
    }

    private void OnActiveSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Session.Title) or nameof(Session.Shell))
            UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        if (_active == null) { StatusBarText.Text = ""; return; }
        var paneLabel = _active.DisplayShell;
        if (_activePaneId is Guid pid)
        {
            var node = FindLeaf(_active.Root, pid);
            if (node?.IsWebView == true) paneLabel = WebViewLabel(node.Url!);
        }
        StatusBarText.Text = $"{_active.Title}  ·  {paneLabel}";
    }

    private FrameworkElement BuildSessionContainer(Session s)
    {
        var host = new Grid();
        host.Children.Add(BuildPaneTree(s, s.Root));
        return host;
    }

    private void RebuildSessionContainer(Session s)
    {
        if (!_sessionRoots.TryGetValue(s.Id, out var host)) return;
        ((Grid)host).Children.Clear();
        var tree = BuildPaneTree(s, s.Root);
        // Belt-and-suspenders: the cached wrapper may still be parented to an orphaned Grid.
        if (tree.Parent is System.Windows.Controls.Panel orphan) orphan.Children.Remove(tree);
        ((Grid)host).Children.Add(tree);
    }

    private FrameworkElement BuildPaneTree(Session sess, PaneNode node)
    {
        if (node.IsLeaf)
        {
            var leaf = GetOrCreatePaneWrapper(sess, node);
            // Cached wrappers may still be parented to a Grid from a prior render.
            if (leaf.Parent is System.Windows.Controls.Panel oldp) oldp.Children.Remove(leaf);
            return leaf;
        }

        var grid = new Grid();
        var isVertical = node.Split == SplitOrientation.Vertical;

        for (int i = 0; i < node.Children.Count; i++)
        {
            if (isVertical)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (i < node.Children.Count - 1)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }
            else
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                if (i < node.Children.Count - 1)
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            var childEl = BuildPaneTree(sess, node.Children[i]);
            if (childEl.Parent is System.Windows.Controls.Panel oldp && oldp != grid)
                oldp.Children.Remove(childEl);

            if (isVertical) Grid.SetColumn(childEl, i * 2);
            else Grid.SetRow(childEl, i * 2);
            if (childEl.Parent == null) grid.Children.Add(childEl);

            if (i < node.Children.Count - 1)
            {
                var splitter = new GridSplitter
                {
                    Background = (System.Windows.Media.Brush)FindResource("ControlStrokeColorDefaultBrush"),
                    ShowsPreview = false,
                };
                if (isVertical)
                {
                    splitter.Width = 4;
                    splitter.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                    splitter.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
                    splitter.ResizeDirection = GridResizeDirection.Columns;
                    Grid.SetColumn(splitter, i * 2 + 1);
                }
                else
                {
                    splitter.Height = 4;
                    splitter.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                    splitter.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
                    splitter.ResizeDirection = GridResizeDirection.Rows;
                    Grid.SetRow(splitter, i * 2 + 1);
                }
                grid.Children.Add(splitter);
            }
        }
        return grid;
    }

    private Border GetOrCreatePaneWrapper(Session sess, PaneNode pane)
    {
        if (_paneBorders.TryGetValue(pane.Id, out var existing)) return existing;

        _paneToSession[pane.Id] = sess.Id;
        FrameworkElement content = pane.IsWebView
            ? GetOrCreateWebView(pane)
            : GetOrCreateTerminal(sess, pane);

        var inner = new Grid();
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = BuildPaneHeader(sess, pane);
        Grid.SetRow(header, 0);
        inner.Children.Add(header);

        Grid.SetRow(content, 1);
        if (content.Parent is System.Windows.Controls.Panel oldParent) oldParent.Children.Remove(content);
        inner.Children.Add(content);

        var border = new Border
        {
            Child = inner,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Tag = pane.Id,
        };
        border.PreviewMouseDown += (_, _) => SetActivePane(pane.Id);
        border.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is bool nv && nv) SetActivePane(pane.Id);
        };

        _paneBorders[pane.Id] = border;
        return border;
    }

    private EasyTerminalControl GetOrCreateTerminal(Session sess, PaneNode pane)
    {
        if (_paneTerminals.TryGetValue(pane.Id, out var cached)) return cached;

        var term = CreateTerminal(sess);
        _paneTerminals[pane.Id] = term;

        // WPF's default Tab handler steals Tab for focus navigation before
        // the native HWND sees it. Forward Tab into the conhost HWND
        // ourselves so PSReadLine suggestions / cmd path completion work.
        var capturedId = pane.Id;
        term.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Tab && _paneClickHook.TryGetValue(capturedId, out var hook))
            {
                if (hook.ForwardTab()) e.Handled = true;
            }
        };

        // Per-pane state for the push-based sidebar:
        //   * LastActivity timestamp (throttled to 1Hz so typing doesn't flood).
        //   * OSC 9 parser scans the same stream for `\e]9;...\a` notifications
        //     that agents emit; result is routed to the owning Session.
        var capturedSess = sess;
        var oscParser = new OscParser();
        EventHandler<Microsoft.Terminal.Wpf.TerminalOutputEventArgs>? onOut = null;
        onOut = (_, args) =>
        {
            try
            {
                var data = args.Data;
                if (!string.IsNullOrEmpty(data))
                {
                    var events = oscParser.Feed(data.AsSpan());
                    if (events.Count > 0)
                    {
                        // Apply each event on the UI thread. Later events
                        // overwrite earlier ones of the same kind, which is
                        // what we want — only the latest notification / cwd
                        // matters to the sidebar.
                        var snapshot = new List<OscEvent>(events);
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                        {
                            foreach (var ev in snapshot)
                            {
                                switch (ev.Kind)
                                {
                                    case OscKind.Notification:
                                        capturedSess.NotificationLevel = ev.Level;
                                        capturedSess.NotificationText = ev.Text;
                                        break;
                                    case OscKind.Cwd:
                                        capturedSess.Cwd = ev.Text;
                                        break;
                                }
                            }
                        });
                    }
                }
            }
            catch (Exception ex) { Log.Error("OscParser.Feed", ex); }

            var now = DateTime.UtcNow;
            if (capturedSess.LastActivity is DateTime prev && (now - prev).TotalSeconds < 1) return;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                () => capturedSess.LastActivity = now);
        };

        var capturedPane = pane;
        var beforePids = new HashSet<int>(EnumerateOwnShellPids());
        RoutedEventHandler? once = null;
        once = async (_, _) =>
        {
            term.Loaded -= once;
            ApplyDefaultTheme(term);
            AttachClickHook(capturedPane.Id, term);
            // Wire activity tracking once the TermPTY is up.
            if (term.ConPTYTerm != null) term.ConPTYTerm.TerminalOutput += onOut;
            await System.Threading.Tasks.Task.Delay(800);
            var pid = await Dispatcher.InvokeAsync(() => PickNewShellPid(beforePids));
            if (pid > 0) _paneShellPid[capturedPane.Id] = pid;
        };
        term.Loaded += once;
        return term;
    }

    private Microsoft.Web.WebView2.Wpf.WebView2 GetOrCreateWebView(PaneNode pane)
    {
        if (_paneWebViews.TryGetValue(pane.Id, out var cached)) return cached;

        var wv = new Microsoft.Web.WebView2.Wpf.WebView2();
        try { wv.Source = new Uri(pane.Url!); }
        catch (Exception ex) { Log.Error("WebView2.Source", ex); }
        _paneWebViews[pane.Id] = wv;
        return wv;
    }

    private static string WebViewLabel(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        }
        catch { return url; }
    }

    private FrameworkElement BuildPaneHeader(Session sess, PaneNode pane)
    {
        // Transparent header so Mica reads through the gutter between the
        // titlebar and the terminal HwndHost. Reference apps (Windows Terminal)
        // don't put a per-pane filled strip here.
        var bar = new Grid
        {
            Background = System.Windows.Media.Brushes.Transparent,
        };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new System.Windows.Controls.TextBlock
        {
            Text = pane.IsWebView ? WebViewLabel(pane.Url!) : sess.DisplayShell,
            FontSize = 11,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
        };
        label.SetResourceReference(System.Windows.Controls.TextBlock.FontFamilyProperty, "Type.Family");
        Grid.SetColumn(label, 0);
        bar.Children.Add(label);

        // The close button lives in a Popup. Without this, the per-pane
        // header occupies WPF airspace next to the conhost HwndHost, and
        // WPF mouse-button events (WM_LBUTTONDOWN/UP) are routed by Windows
        // to whichever child HWND sits topmost at the cursor — the native
        // HwndTerminalClass — so the WPF Button never sees the click.
        // A Popup is its own top-level HWND, sits above the conhost child,
        // and receives mouse-button events normally.
        var close = new System.Windows.Controls.Button
        {
            Content = "\u2715",
            Width = 28,
            Height = 22,
            Padding = new Thickness(0),
            FontSize = 10,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Close pane",
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorTertiaryBrush"),
        };
        close.Click += (_, _) => ClosePane(sess, pane.Id);

        // Header keeps an invisible anchor in column 1 so layout reserves
        // 28px on the right; the Popup positions itself against the anchor.
        var closeAnchor = new System.Windows.Controls.Border
        {
            Width = 28,
            Height = 22,
            Background = System.Windows.Media.Brushes.Transparent,
            Margin = new Thickness(0, 0, 4, 0),
        };
        var closePopup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = closeAnchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Center,
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = false,
            Child = close,
        };
        // Tie IsOpen to the anchor's visibility. Without this, the Popup is a
        // top-level HWND that outlives its parent: when you close the app,
        // the WPF window tears down but the popup HWND lingers a beat;
        // switching sessions hides the anchor but the popup stays put.
        // Binding to IsVisible folds the popup back into normal WPF lifecycle.
        closePopup.SetBinding(System.Windows.Controls.Primitives.Popup.IsOpenProperty,
            new System.Windows.Data.Binding("IsVisible")
            {
                Source = closeAnchor,
                Mode = System.Windows.Data.BindingMode.OneWay,
            });
        // Belt-and-suspenders: when the anchor unloads (pane closed), force
        // the popup shut so its HWND is destroyed immediately.
        closeAnchor.Unloaded += (_, _) => closePopup.IsOpen = false;
        Grid.SetColumn(closeAnchor, 1);
        bar.Children.Add(closeAnchor);
        bar.Children.Add(closePopup);

        return bar;
    }

    private EasyTerminalControl CreateTerminal(Session s)
    {
        var baseShell = string.IsNullOrEmpty(s.Shell)
            ? Shell.DefaultCommandLine(_settings.DefaultShell)
            : s.Shell;
        // Pick the cwd in priority order: per-session remembered cwd (set by
        // OSC 7 or last-known), then the global default, then no override.
        // This is what stops us launching in the install folder.
        var cwd = !string.IsNullOrWhiteSpace(s.Cwd) && System.IO.Directory.Exists(s.Cwd)
            ? s.Cwd
            : _settings.ResolveDefaultCwd();
        var startCmd = Shell.WithStartupCwd(baseShell, cwd);

        return new EasyTerminalControl
        {
            StartupCommandLine = startCmd,
            FontFamilyWhenSettingTheme = new System.Windows.Media.FontFamily(_settings.FontFamily),
            FontSizeWhenSettingTheme = _settings.FontSize,
            LogConPTYOutput = true,
        };
    }

    // ----- Zoom: increase/decrease/reset font size live across all panes -----

    private void AdjustFontSize(int delta) => SetFontSize(_settings.FontSize + delta);

    private void SetFontSize(int newSize)
    {
        newSize = System.Math.Max(FontSizeMin, System.Math.Min(FontSizeMax, newSize));
        if (newSize == _settings.FontSize) return;
        _settings.FontSize = newSize;
        _settings.Save();
        ApplyFontToAllTerminals();
    }

    private void ApplyFontToAllTerminals()
    {
        var family = new System.Windows.Media.FontFamily(_settings.FontFamily);
        var theme = MakeDefaultTheme();
        foreach (var term in _paneTerminals.Values)
        {
            try
            {
                term.FontFamilyWhenSettingTheme = family;
                term.FontSizeWhenSettingTheme = _settings.FontSize;
                // Assigning Theme triggers Terminal.SetTheme(theme, fontFamily, fontSize) on the
                // underlying WPF terminal control — re-applies font WITHOUT killing the shell.
                term.Theme = theme;
            }
            catch (Exception ex) { Log.Error("ApplyFontToAllTerminals", ex); }
        }
    }

    /// Windows Terminal "Campbell" defaults in COLORREF (0x00BBGGRR) format.
    /// Used on every newly-created terminal and re-applied during zoom so colors stay consistent.
    private static readonly uint[] CampbellColors =
    {
        0x000000, 0x1F0FC5, 0x0EA113, 0x009CC1,
        0xDA3700, 0x981788, 0xDD963A, 0xCCCCCC,
        0x767676, 0x5648E7, 0x0CC616, 0xA5F1F9,
        0xFF783B, 0x9E00B4, 0xD6D661, 0xF2F2F2,
    };

    private static TerminalTheme MakeDefaultTheme() => new()
    {
        DefaultBackground = 0x0C0C0C,
        DefaultForeground = 0xCCCCCC,
        DefaultSelectionBackground = 0x383838,
        CursorStyle = CursorStyle.BlinkingBar,
        ColorTable = CampbellColors,
    };

    private static void ApplyDefaultTheme(EasyTerminalControl term)
    {
        try { term.Theme = MakeDefaultTheme(); }
        catch (Exception ex) { Log.Error("ApplyDefaultTheme", ex); }
    }

    /// Enumerates shell PIDs. The Microsoft.Terminal.Wpf control launches the shell as
    /// a DIRECT child of our process (with conhost as a separate sibling acting as the
    /// pseudo-console host). So shells = direct children of CmuxWin minus conhost.exe.
    private IEnumerable<int> EnumerateOwnShellPids()
    {
        var ownPid = Environment.ProcessId;
        var result = new List<int>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT ProcessId, Name FROM Win32_Process WHERE ParentProcessId = {ownPid}");
            foreach (var mo in searcher.Get())
            {
                var name = mo["Name"]?.ToString() ?? "";
                if (string.Equals(name, "conhost.exe", System.StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(System.Convert.ToInt32(mo["ProcessId"]));
            }
        }
        catch (Exception ex) { Log.Error("EnumerateOwnShellPids", ex); }
        return result;
    }

    private int PickNewShellPid(HashSet<int> beforePids)
    {
        var current = EnumerateOwnShellPids().ToList();
        var alreadyAssigned = new HashSet<int>(_paneShellPid.Values);
        foreach (var pid in current)
        {
            if (beforePids.Contains(pid)) continue;
            if (alreadyAssigned.Contains(pid)) continue;
            return pid;
        }
        return 0;
    }

    // ----- Active pane indicator -----

    private void SetActivePane(Guid paneId)
    {
        if (_activePaneId == paneId) return;
        _activePaneId = paneId;
        var accent = (System.Windows.Media.Brush)FindResource("AccentFillColorDefaultBrush");
        foreach (var (id, border) in _paneBorders)
            border.BorderBrush = (id == paneId) ? accent : System.Windows.Media.Brushes.Transparent;
        UpdateStatusBar();
    }

    // ----- Tree helpers -----

    private static PaneNode? FirstLeaf(PaneNode node)
    {
        if (node.IsLeaf) return node;
        foreach (var c in node.Children) { var leaf = FirstLeaf(c); if (leaf != null) return leaf; }
        return null;
    }
    private static PaneNode? FindLeaf(PaneNode root, Guid id)
    {
        if (root.IsLeaf) return root.Id == id ? root : null;
        foreach (var c in root.Children) { var f = FindLeaf(c, id); if (f != null) return f; }
        return null;
    }
    private static PaneNode? FindParent(PaneNode root, Guid childId)
    {
        if (root.IsLeaf) return null;
        foreach (var c in root.Children)
        {
            if (c.Id == childId) return root;
            var f = FindParent(c, childId);
            if (f != null) return f;
        }
        return null;
    }
    private static IEnumerable<Guid> AllLeafIds(PaneNode node)
    {
        if (node.IsLeaf) { yield return node.Id; yield break; }
        foreach (var c in node.Children) foreach (var id in AllLeafIds(c)) yield return id;
    }

    // ----- Split / close panes -----

    private void SplitActive(SplitOrientation orientation) => SplitActive(orientation, null);

    private void SplitActive(SplitOrientation orientation, string? url)
    {
        if (_active == null || _activePaneId == null) return;
        var leaf = FindLeaf(_active.Root, _activePaneId.Value);
        if (leaf == null) return;

        var newLeaf = new PaneNode { Url = url };
        var parent = FindParent(_active.Root, leaf.Id);

        if (parent != null && parent.Split == orientation)
        {
            var idx = parent.Children.IndexOf(leaf);
            parent.Children.Insert(idx + 1, newLeaf);
        }
        else
        {
            var split = new PaneNode { Split = orientation, Children = { leaf, newLeaf } };
            if (parent == null) _active.Root = split;
            else
            {
                var idx = parent.Children.IndexOf(leaf);
                parent.Children[idx] = split;
            }
        }

        RebuildSessionContainer(_active);
        SetActivePane(newLeaf.Id);
        FocusPane(newLeaf.Id);
    }

    /// Move keyboard focus to a pane. Deferred to Loaded priority because
    /// freshly-split panes haven't been measured/arranged yet, and HwndHost-
    /// based controls (both EasyTerminalControl and WebView2) won't accept
    /// focus until their native HWND exists.
    private void FocusPane(Guid paneId)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            try
            {
                if (_paneTerminals.TryGetValue(paneId, out var term))
                {
                    term.Focus();
                    System.Windows.Input.Keyboard.Focus(term);
                }
                else if (_paneWebViews.TryGetValue(paneId, out var wv))
                {
                    wv.Focus();
                    System.Windows.Input.Keyboard.Focus(wv);
                }
            }
            catch (Exception ex) { Log.Error("FocusPane", ex); }
        });
    }

    private void CloseActivePane()
    {
        if (_active == null || _activePaneId == null) return;
        ClosePane(_active, _activePaneId.Value);
    }

    private void ClosePane(Session sess, Guid paneId)
    {
        var leaf = FindLeaf(sess.Root, paneId);
        if (leaf == null) return;

        var parent = FindParent(sess.Root, paneId);
        if (parent == null) { CloseSession(sess); return; }

        parent.Children.Remove(leaf);

        if (_paneBorders.Remove(paneId, out var border))
            if (border.Parent is System.Windows.Controls.Panel bp) bp.Children.Remove(border);
        if (_paneTerminals.Remove(paneId, out var term))
            if (term.Parent is System.Windows.Controls.Panel tp) tp.Children.Remove(term);
        if (_paneWebViews.Remove(paneId, out var wv))
        {
            if (wv.Parent is System.Windows.Controls.Panel wp) wp.Children.Remove(wv);
            try { wv.Dispose(); } catch (Exception ex) { Log.Error("WebView2.Dispose", ex); }
        }
        _paneToSession.Remove(paneId);
        _paneShellPid.Remove(paneId);
        _paneClickHook.Remove(paneId);

        if (parent.Children.Count == 1)
        {
            var only = parent.Children[0];
            var grand = FindParent(sess.Root, parent.Id);
            if (grand == null) sess.Root = only;
            else
            {
                var idx = grand.Children.IndexOf(parent);
                grand.Children[idx] = only;
            }
        }

        RebuildSessionContainer(sess);
        var newActive = FirstLeaf(sess.Root);
        if (newActive != null) SetActivePane(newActive.Id);
    }

    // ----- Session add / close / rename -----

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        var fontBefore = (_settings.FontFamily, _settings.FontSize, _settings.DefaultShell);
        dlg.ShowDialog();
        if (!dlg.Saved) return;

        // If the default shell changed, future sessions pick it up via Shell.DefaultCommandLine.
        // Font changes only apply to terminals created after this point — existing ones keep theirs.
        // Nothing to actively refresh here yet.
        _ = fontBefore;
    }

    private void OnCloseSessionMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        var session = FindSessionFromMenuItem(mi);
        if (session != null) CloseSession(session);
    }

    private void OnRenameMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        var session = FindSessionFromMenuItem(mi);
        if (session != null) session.IsEditing = true;
    }

    private static Session? FindSessionFromMenuItem(System.Windows.Controls.MenuItem mi)
    {
        var ctx = mi;
        while (ctx?.Parent != null && ctx.Parent is not System.Windows.Controls.ContextMenu)
            ctx = ctx.Parent as System.Windows.Controls.MenuItem;
        if (ctx?.Parent is System.Windows.Controls.ContextMenu cm && cm.PlacementTarget is FrameworkElement fe)
            return fe.DataContext as Session;
        return null;
    }

    private void CloseSession(Session s)
    {
        if (_sessionRoots.TryGetValue(s.Id, out var host))
        {
            TerminalHost.Children.Remove(host);
            _sessionRoots.Remove(s.Id);
        }
        foreach (var leafId in AllLeafIds(s.Root).ToList())
        {
            _paneBorders.Remove(leafId);
            if (_paneTerminals.Remove(leafId, out var term))
                if (term.Parent is System.Windows.Controls.Panel pp) pp.Children.Remove(term);
            if (_paneWebViews.Remove(leafId, out var wp))
            {
                if (wp.Parent is System.Windows.Controls.Panel par) par.Children.Remove(wp);
                try { wp.Dispose(); } catch (Exception ex) { Log.Error("WebView2.Dispose", ex); }
            }
            _paneToSession.Remove(leafId);
            _paneShellPid.Remove(leafId);
            _paneClickHook.Remove(leafId);
        }

        var wasActive = _active?.Id == s.Id;
        var next = _store.Remove(s);
        if (wasActive)
        {
            _active = null;
            _activePaneId = null;
            SessionList.SelectedItem = next;
        }
    }

    // ----- Rename via double-click + inline TextBox -----

    private void OnRenameTextBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }

    private void OnRenameTextBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;
        if (tb.DataContext is not Session s) return;
        if (e.Key == Key.Enter || e.Key == Key.Escape)
        {
            s.IsEditing = false;
            // Focus back to the SessionList so subsequent keystrokes don't go into the now-hidden box.
            SessionList.Focus();
            e.Handled = true;
        }
    }

    private void OnRenameTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb && tb.DataContext is Session s)
            s.IsEditing = false;
    }

    // ----- Drag reorder -----

    private void OnSessionItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListViewItem lvi && lvi.DataContext is Session s)
        {
            _dragStart = e.GetPosition(null);
            _dragSession = s;
        }
    }

    private void OnSessionItemMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragSession == null || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
         && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var data = new System.Windows.DataObject("cmux-session", _dragSession);
        try { DragDrop.DoDragDrop(SessionList, data, System.Windows.DragDropEffects.Move); }
        finally { _dragSession = null; }
    }

    private void OnSessionItemDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListViewItem lvi) return;
        if (lvi.DataContext is not Session target) return;
        if (e.Data.GetData("cmux-session") is not Session source) return;
        if (source.Id == target.Id) return;

        var oldIdx = _store.Sessions.IndexOf(source);
        var newIdx = _store.Sessions.IndexOf(target);
        if (oldIdx < 0 || newIdx < 0) return;
        _store.Sessions.Move(oldIdx, newIdx);
        e.Handled = true;
    }

    // ----- Shell health polling -----
    //
    // We used to tail GetConsoleText every 750ms to populate a "preview"
    // string on each session card. That copy ran on the UI thread, was heavy
    // (full screen buffer per pane), caused visible typing lag, and the
    // resulting text was rarely useful — the reference cmux for macOS doesn't
    // do this at all (their sidebar shows metadata + push notifications, not
    // a buffer tail). Dropped entirely. The timer now only watches for shell
    // processes that have exited so we can auto-close their pane.
    private void StartShellHealthPolling()
    {
        if (_previewTimer != null) return;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _previewTimer.Tick += (_, _) =>
        {
            PollShellHealth();
            // Nudge the sidebar's relative-time labels ("5m ago" → "6m ago")
            // without touching the PTY. Cheap — just fires PropertyChanged on
            // the computed string.
            foreach (var s in _store.Sessions)
                if (s.LastActivity != null) s.RaiseLastActivityRelativeChanged();
        };
        _previewTimer.Start();
    }

    private void PollShellHealth()
    {
        // A tracked shell PID that's no longer in our shell-set means the user
        // ran `exit` (or the shell otherwise died). Conhost may linger briefly
        // to show "Session Terminated" — that's why we track shell, not conhost.
        var liveShells = new HashSet<int>(EnumerateOwnShellPids());
        foreach (var (paneId, shellPid) in _paneShellPid.ToList())
        {
            if (liveShells.Contains(shellPid)) continue;
            _paneShellPid.Remove(paneId);
            if (_paneToSession.TryGetValue(paneId, out var sessId))
            {
                var sess = _store.Sessions.FirstOrDefault(x => x.Id == sessId);
                if (sess != null) ClosePane(sess, paneId);
            }
            break;  // one closure per tick — defense in depth
        }
    }

    // ----- Window / settings -----

    private void ApplySettings()
    {
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;

        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop)
            && IsOnScreen(_settings.WindowLeft, _settings.WindowTop, _settings.WindowWidth, _settings.WindowHeight))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var rect = new System.Drawing.Rectangle((int)left, (int)top, (int)width, (int)height);
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            if (screen.WorkingArea.IntersectsWith(rect)) return true;
        return false;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _previewTimer?.Stop();

        if (WindowState == WindowState.Normal)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowMaximized = false;
        }
        else if (WindowState == WindowState.Maximized)
        {
            var rb = RestoreBounds;
            if (!rb.IsEmpty)
            {
                _settings.WindowLeft = rb.Left;
                _settings.WindowTop = rb.Top;
                _settings.WindowWidth = rb.Width;
                _settings.WindowHeight = rb.Height;
            }
            _settings.WindowMaximized = true;
        }

        _settings.Save();
        _store.Save();
        _clipboard?.Dispose();
    }

    // ----- URL actions -----
    //
    // Microsoft.Terminal.Wpf doesn't expose its cell grid (see
    // docs/RENDERER_NOTES.md), so we can't hit-test arbitrary clicks against
    // URLs. Two entry points instead:
    //   * Double-click on a URL — conhost's existing word-selection gesture
    //     selects the URL; the click hook reads it and pops the action menu.
    //     Right-click is left alone so conhost's default paste still works.
    //   * Ctrl+Shift+U — palette listing every URL in the current pane's
    //     buffer. Covers URLs that scrolled past or live in a long log line.

    private void AttachClickHook(Guid paneId, EasyTerminalControl term)
    {
        var hook = new TerminalClickHook(term);
        hook.OnUrlAction = (url, screenPt) => ShowUrlMenu(paneId, url, screenPt);
        hook.Attach();
        _paneClickHook[paneId] = hook;
    }

    private void ShowUrlMenu(Guid paneId, string url, System.Windows.Point screenPt)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint,
            HorizontalOffset = screenPt.X,
            VerticalOffset = screenPt.Y,
        };

        // Header row previews the URL the actions will run against. Truncated
        // so a long URL doesn't stretch the menu off the edge of the screen.
        var preview = url.Length > 64 ? url.Substring(0, 61) + "…" : url;
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = preview,
            IsHitTestVisible = false,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
        });
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(MakeMenuItem("Open in browser", () => OpenInBrowser(url)));
        menu.Items.Add(MakeMenuItem("Open in pane to right",
            () => OpenUrlInNewPane(paneId, url, SplitOrientation.Vertical)));
        menu.Items.Add(MakeMenuItem("Open in pane below",
            () => OpenUrlInNewPane(paneId, url, SplitOrientation.Horizontal)));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(MakeMenuItem("Copy URL", () =>
        {
            try { System.Windows.Clipboard.SetText(url); ShowToast("URL copied"); }
            catch (Exception ex) { Log.Error("ShowUrlMenu.CopyUrl", ex); }
        }));

        menu.IsOpen = true;
    }

    private static System.Windows.Controls.MenuItem MakeMenuItem(string label, Action onClick)
    {
        var mi = new System.Windows.Controls.MenuItem { Header = label };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private void ShowUrlPalette()
    {
        // Source: the active pane if it's a terminal, otherwise the first
        // terminal leaf in the current session. Falls back to no-op for
        // sessions that contain only webview panes.
        if (_active == null) return;

        var sourcePaneId = _activePaneId;
        if (sourcePaneId == null || !_paneTerminals.ContainsKey(sourcePaneId.Value))
        {
            var firstTerm = AllLeafIds(_active.Root).FirstOrDefault(id => _paneTerminals.ContainsKey(id));
            if (firstTerm == default) return;
            sourcePaneId = firstTerm;
        }

        if (!_paneTerminals.TryGetValue(sourcePaneId.Value, out var term)) return;
        string text;
        try { text = term.ConPTYTerm?.GetConsoleText() ?? ""; }
        catch (Exception ex) { Log.Error("ShowUrlPalette.GetConsoleText", ex); return; }

        var urls = UrlScanner.AllUrls(text);
        if (urls.Count == 0) { ShowToast("No URLs in buffer"); return; }

        var menu = new System.Windows.Controls.ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.Center,
            PlacementTarget = this,
        };
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = urls.Count == 1 ? "1 URL in buffer" : $"{urls.Count} URLs in buffer",
            IsHitTestVisible = false,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
        });
        menu.Items.Add(new System.Windows.Controls.Separator());

        var paneIdForActions = sourcePaneId.Value;
        foreach (var url in urls)
        {
            var preview = url.Length > 64 ? url.Substring(0, 61) + "…" : url;
            var item = new System.Windows.Controls.MenuItem { Header = preview };
            // Each URL is a submenu with the same 3 actions ShowUrlMenu offers.
            item.Items.Add(MakeMenuItem("Open in browser", () => OpenInBrowser(url)));
            item.Items.Add(MakeMenuItem("Open in pane to right",
                () => OpenUrlInNewPane(paneIdForActions, url, SplitOrientation.Vertical)));
            item.Items.Add(MakeMenuItem("Open in pane below",
                () => OpenUrlInNewPane(paneIdForActions, url, SplitOrientation.Horizontal)));
            item.Items.Add(new System.Windows.Controls.Separator());
            item.Items.Add(MakeMenuItem("Copy URL", () =>
            {
                try { System.Windows.Clipboard.SetText(url); ShowToast("URL copied"); }
                catch (Exception ex) { Log.Error("UrlPalette.CopyUrl", ex); }
            }));
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }


    private void OpenInBrowser(string url)
    {
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
            Log.Error("OpenInBrowser", ex);
            ShowToast("Couldn't open URL");
        }
    }

    private void OpenUrlInNewPane(Guid sourcePaneId, string url, SplitOrientation orientation)
    {
        if (_active == null) return;
        SetActivePane(sourcePaneId);
        SplitActive(orientation, url);
    }

    // ----- Toast -----

    public void ShowToast(string text)
    {
        ToastText.Text = text;
        ToastPopup.IsOpen = true;

        var fadeIn = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = ToastHost.Opacity,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(150)),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            },
        };
        ToastHost.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            };
            fadeOut.Completed += (_, _) => ToastPopup.IsOpen = false;
            ToastHost.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        };
        _toastTimer.Start();
    }

    private string _lastClipboardText = "";

    private void OnClipboardChanged()
    {
        // ClipboardWatcher already gates on "our window is foreground", so any
        // text-format change at this point is a copy the user just did inside
        // cmux (terminal pane, sidebar rename, settings dialog). conhost clears
        // the selection immediately after copying, so comparing GetSelectedText
        // to the clipboard would miss every terminal copy — just toast on text
        // changes and dedupe identical payloads to ignore re-renders.
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) return;
            var clip = System.Windows.Clipboard.GetText();
            if (string.IsNullOrEmpty(clip)) return;
            if (clip == _lastClipboardText) return;
            _lastClipboardText = clip;
            ShowToast("Copied");
        }
        catch (Exception ex) { Log.Error("OnClipboardChanged", ex); }
    }
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _exec;
    public RelayCommand(Action<object?> exec) { _exec = exec; }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _exec(parameter);
#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
}
