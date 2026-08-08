namespace Perch;

/// Host-agnostic rectangle in desktop coordinates. WPF's System.Windows.Rect
/// stayed behind in the Windows host; this is the two fields of it the
/// reachability rule needs.
internal readonly record struct ScreenRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// The one genuinely tricky bit of restoring a window's position: deciding
/// whether a SAVED position is still usable.
///
/// Monitors come and go — you undock, you unplug the second screen, you change
/// the display arrangement. A saved rect that pointed at that screen now points
/// into empty space, and a naive restore drops the window somewhere you cannot
/// see or reach: it's not just off-screen, it's unrecoverable without editing
/// settings.json by hand, because you can't grab a title bar you can't see.
///
/// Pure and static so it can be tested without standing up a real window.
internal static class WindowPlacement
{
    /// How much of the window has to be on the desktop for it to count as
    /// reachable. This is really "can I grab the title bar and drag it back" —
    /// a 1px sliver technically intersects the desktop and is useless.
    private const double GrabMargin = 80;

    /// Is <paramref name="window"/> reachable on the current desktop
    /// (<paramref name="virtualScreen"/> = the union of all monitors)?
    ///
    /// Deliberately an INTERSECTION test, not containment: a window you left
    /// deliberately hanging off the right edge, or straddling two monitors, is
    /// perfectly fine and must be restored as-is. We only reject a window that
    /// has effectively nothing left on the desktop.
    public static bool IsReachable(ScreenRect window, ScreenRect virtualScreen)
    {
        if (window.Width <= 0 || window.Height <= 0) return false;
        if (virtualScreen.Width <= 0 || virtualScreen.Height <= 0) return false;

        // Enough of the window's body inside each edge to grab and drag.
        return window.Right - GrabMargin > virtualScreen.Left
            && window.Left + GrabMargin < virtualScreen.Right
            && window.Bottom - GrabMargin > virtualScreen.Top
            && window.Top + GrabMargin < virtualScreen.Bottom;
    }
}
