using System.Windows;
using Xunit;

namespace Perch.Tests;

// Restoring the window where you left it — including on a second monitor.
//
// The failure this guards against is nasty and silent: you close Perch on your
// right-hand monitor, unplug it, relaunch — and a naive restore puts the window
// at x=2560, which is now nowhere. It isn't just off-screen, it's unrecoverable:
// you can't drag back a title bar you can't see. So a saved rect is only honored
// if enough of it lands on the CURRENT desktop to grab.
public class WindowPlacementTests
{
    // A typical dual-monitor desktop: primary 1920x1080, second one to the RIGHT.
    private static readonly Rect DualScreen = new(0, 0, 3840, 1080);
    // …and what's left after the second monitor is unplugged.
    private static readonly Rect SingleScreen = new(0, 0, 1920, 1080);

    [Fact]
    public void AWindowOnTheSecondMonitorIsRestored()
    {
        // The whole point of the feature: maximize on the right-hand screen, come
        // back tomorrow, still there.
        Assert.True(WindowPlacement.IsReachable(new Rect(2000, 100, 1040, 640), DualScreen));
    }

    [Fact]
    public void ThatSameWindowIsRejectedOnceTheMonitorIsGone()
    {
        // Same saved rect, second monitor unplugged → it would land in the void.
        // Rejecting it means we keep the SIZE and let Windows place the window.
        Assert.False(WindowPlacement.IsReachable(new Rect(2000, 100, 1040, 640), SingleScreen));
    }

    [Fact]
    public void AWindowStraddlingTwoMonitorsIsFine()
    {
        // Intersection, not containment: a window deliberately spanning the seam
        // is a normal thing to want and must come back exactly as left.
        Assert.True(WindowPlacement.IsReachable(new Rect(1700, 100, 1040, 640), DualScreen));
    }

    [Fact]
    public void AWindowHangingOffAnEdgeIsKept_ButASliverIsNot()
    {
        // Deliberately hung off the right edge, most of it still visible → keep.
        Assert.True(WindowPlacement.IsReachable(new Rect(1500, 100, 1040, 640), SingleScreen));
        // Only a hairline left on screen → you couldn't grab it. Reject.
        Assert.False(WindowPlacement.IsReachable(new Rect(1900, 100, 1040, 640), SingleScreen));
    }

    [Fact]
    public void NegativeCoordinatesWork_ASecondMonitorToTheLEFT()
    {
        // The virtual screen starts at a negative X when the second monitor is on
        // the left. Nothing here may assume the desktop starts at 0,0.
        var leftOfPrimary = new Rect(-1920, 0, 3840, 1080);
        Assert.True(WindowPlacement.IsReachable(new Rect(-1500, 100, 1040, 640), leftOfPrimary));
        Assert.False(WindowPlacement.IsReachable(new Rect(-1500, 100, 1040, 640), SingleScreen));
    }

    [Fact]
    public void AWindowAboveTheDesktopIsRejected()
    {
        // Title bar off the top = unreachable, even though the rect overlaps.
        Assert.False(WindowPlacement.IsReachable(new Rect(400, -700, 1040, 640), SingleScreen));
    }

    [Fact]
    public void DegenerateRectsAreRejectedRatherThanCrashing()
    {
        Assert.False(WindowPlacement.IsReachable(new Rect(0, 0, 0, 0), SingleScreen));
        Assert.False(WindowPlacement.IsReachable(new Rect(0, 0, 1040, 640), new Rect(0, 0, 0, 0)));
    }
}
