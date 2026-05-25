using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using EasyWindowsTerminalControl;
using Microsoft.Terminal.Wpf;

namespace CmuxWin;

/// Subclasses the native conhost HWND so we can intercept mouse messages.
/// HwndHost.MessageHook doesn't fire for messages routed to the hosted HWND's
/// own WndProc (TerminalContainer overrides it), so we install a comctl32
/// subclass directly on the render HWND.
///
/// Two behaviors on top of conhost's defaults:
///  1. Auto-copy on WM_LBUTTONUP — Microsoft.Terminal.Wpf ships with no copy
///     gesture, so a drag-select would otherwise be inert.
///  2. URL action menu on WM_LBUTTONDBLCLK — conhost double-click selects
///     the word under the cursor; if the word is a URL we fire OnUrlAction.
///     Right-click is left untouched so conhost's default paste still works.
internal sealed class TerminalClickHook
{
    public delegate void UrlActionHandler(string url, System.Windows.Point screenPoint);

    private readonly EasyTerminalControl _easy;
    private HwndHost? _hwndHost;
    private TerminalControl? _terminal;
    private bool _attached;

    private SUBCLASSPROC? _subclassProc;
    private readonly List<IntPtr> _subclassedHwnds = new();
    private static int _nextSubclassId = 1;
    private int _subclassId;

    // Manual double-click detection: HwndTerminalClass may not be registered
    // with CS_DBLCLKS (in which case Windows never synthesizes WM_LBUTTONDBLCLK),
    // so we track press timing/position ourselves.
    private int _lastLeftDownTick;
    private int _lastLeftDownX, _lastLeftDownY;

    public UrlActionHandler? OnUrlAction;

    public TerminalClickHook(EasyTerminalControl easy) { _easy = easy; }

    public TerminalControl? Terminal => _terminal;

    public void Attach()
    {
        if (_attached) return;
        _terminal = _easy.Terminal;
        if (_terminal == null) { _easy.Loaded += (_, _) => Attach(); return; }

        _hwndHost = FindDescendant<HwndHost>(_terminal);
        if (_hwndHost == null) { _terminal.Loaded += (_, _) => Attach(); return; }

        _subclassId = System.Threading.Interlocked.Increment(ref _nextSubclassId);
        _subclassProc = SubclassWndProc;
        SubclassRenderHwnd(_hwndHost.Handle);
        _attached = true;
    }

    private void SubclassRenderHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (_subclassedHwnds.Contains(hwnd)) return;
        if (SetWindowSubclass(hwnd, _subclassProc!, (IntPtr)_subclassId, IntPtr.Zero))
            _subclassedHwnds.Add(hwnd);
    }

    /// Send a Tab into the terminal's PTY. WPF's default Tab handler steals
    /// the key for focus navigation, so we write the Tab byte directly to
    /// the PTY input — bypassing the entire keyboard input chain and the
    /// re-entry that PostMessage WM_KEYDOWN would cause (we'd see the
    /// synthesized Tab back in PreviewKeyDown → infinite recursion → crash).
    public bool ForwardTab()
    {
        try
        {
            var pty = _easy.ConPTYTerm;
            if (pty == null) return false;
            pty.WriteToTerm("\t".AsSpan());
            return true;
        }
        catch (Exception ex) { Log.Error("ForwardTab", ex); return false; }
    }

    private IntPtr SubclassWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        // Auto-copy on selection. Deferred to a background dispatcher tick to
        // avoid re-entering conhost mid-settle (we hit WM_CLIPBOARDUPDATE
        // ourselves via ClipboardWatcher, which made conhost clear the
        // highlight on a synchronous SetText). The deferral doesn't perfectly
        // preserve the highlight either, but it keeps copy reliable and lets
        // double-click read the selection that conhost briefly stages.
        if (msg == WM_LBUTTONUP)
        {
            var result = DefSubclassProc(hwnd, msg, wParam, lParam);
            try
            {
                var sel = _terminal?.GetSelectedText() ?? "";
                if (sel.Length > 0)
                {
                    var dispatcher = _easy.Dispatcher;
                    dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                    {
                        try { System.Windows.Clipboard.SetText(sel); }
                        catch (Exception ex) { Log.Error("AutoCopy.SetText", ex); }
                    });
                }
            }
            catch (Exception ex) { Log.Error("AutoCopy.GetSelectedText", ex); }
            return result;
        }

        // Manual double-click detection on WM_LBUTTONDOWN. Conhost does its
        // own word-selection on the second click of a double regardless of
        // CS_DBLCLKS, so we don't need DBLCLK from Windows — we just need to
        // know "this DOWN is the second one in a short, near-stationary
        // sequence" so we can read the selection that conhost is about to
        // make and pop the URL menu if it matches.
        if (msg == WM_LBUTTONDOWN)
        {
            var now = Environment.TickCount;
            int x = GetX(lParam), y = GetY(lParam);
            bool isDouble =
                (uint)(now - _lastLeftDownTick) <= GetDoubleClickTime() &&
                Math.Abs(x - _lastLeftDownX) <= 4 &&
                Math.Abs(y - _lastLeftDownY) <= 4;
            _lastLeftDownTick = now;
            _lastLeftDownX = x; _lastLeftDownY = y;

            var result = DefSubclassProc(hwnd, msg, wParam, lParam);

            if (isDouble)
            {
                try
                {
                    var sel = _terminal?.GetSelectedText() ?? "";
                    var url = UrlScanner.TryGetUrl(sel);
                    if (url != null)
                    {
                        var screen = ClientToScreenDip(hwnd, x, y);
                        OnUrlAction?.Invoke(url, screen);
                    }
                }
                catch (Exception ex) { Log.Error("DblClick.UrlAction", ex); }
                _lastLeftDownTick = 0;  // reset so a third quick click doesn't re-fire
            }
            return result;
        }

        // Belt-and-suspenders: if Windows DOES synthesize DBLCLK on this
        // class, also handle it the same way (cheap, harmless if never fires).
        if (msg == WM_LBUTTONDBLCLK)
        {
            var result = DefSubclassProc(hwnd, msg, wParam, lParam);
            try
            {
                var sel = _terminal?.GetSelectedText() ?? "";
                var url = UrlScanner.TryGetUrl(sel);
                if (url != null)
                {
                    var screen = ClientToScreenDip(hwnd, GetX(lParam), GetY(lParam));
                    OnUrlAction?.Invoke(url, screen);
                }
            }
            catch (Exception ex) { Log.Error("DblClick.UrlAction.dblclk", ex); }
            return result;
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private static System.Windows.Point ClientToScreenDip(IntPtr hwnd, int x, int y)
    {
        var pt = new POINT { X = x, Y = y };
        ClientToScreen(hwnd, ref pt);
        var src = HwndSource.FromHwnd(hwnd);
        if (src?.CompositionTarget != null)
        {
            var m = src.CompositionTarget.TransformFromDevice;
            return new System.Windows.Point(pt.X * m.M11, pt.Y * m.M22);
        }
        return new System.Windows.Point(pt.X, pt.Y);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            var deep = FindDescendant<T>(c);
            if (deep != null) return deep;
        }
        return null;
    }

    private static int GetX(IntPtr lParam) => unchecked((short)(lParam.ToInt32() & 0xFFFF));
    private static int GetY(IntPtr lParam) => unchecked((short)((lParam.ToInt32() >> 16) & 0xFFFF));

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);

    private delegate IntPtr SUBCLASSPROC(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}
