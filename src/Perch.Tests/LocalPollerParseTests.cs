using System;
using System.Collections.Generic;
using Perch;
using Xunit;

/// Attribution used to be reachable only through a PowerShell subprocess whose
/// JSON we re-parsed, so these tests were written against that JSON. The
/// subprocess is gone (WindowsSystemProbe does the same work in-process), and
/// with it the failure they originally pinned: a raw BEL in some unrelated
/// process's command line reaching ConvertTo-Json verbatim, the strict parser
/// refusing the whole document, and every scan for the rest of the session
/// silently returning nothing.
///
/// That specific fault is now unrepresentable — there is no JSON hop to poison.
/// The scrub survives because these strings still cross into the webview as
/// JSON, so it is tested at its new home (the probe boundary) instead, and the
/// attribution behaviour it protected is tested directly against typed input.
namespace Perch.Tests;

public class LocalPollerParseTests
{
    private static IReadOnlyList<RawListener> OneNodeListener() =>
        new[] { new RawListener(5173, 200, "127.0.0.1") };

    /// node(200) → shell(100), plus an unrelated process that used to be able to
    /// poison the whole scan.
    private static IReadOnlyList<RawProc> Procs(string weirdArg) =>
        new[]
        {
            new RawProc(200, 100, "node.exe", "node vite", 1),
            new RawProc(300, 100, "weird.exe", $"weird {weirdArg}arg", 1),
        };

    [Fact]
    public void AttributesAListenerToThePaneThatOwnsItsAncestor()
    {
        var panes = new[] { new PaneProc(100, "pane1", "web", "working") };
        var got = new LocalPoller().Build(OneNodeListener(), Procs("x"), panes);
        var l = Assert.Single(got);
        Assert.Equal(5173, l.Port);
        Assert.Equal("pane1", l.OwnerPaneId);   // ancestry: node(200) → shell(100)
        Assert.Equal("Vite", l.Framework);
    }

    /// The bystander that used to cost us the whole scan now costs us nothing:
    /// it is not a listener, so it never even reaches the output.
    [Fact]
    public void APoisonedBystanderNoLongerCostsTheAttributableServer()
    {
        var panes = new[] { new PaneProc(100, "pane1", "web", "working") };
        var got = new LocalPoller().Build(OneNodeListener(), Procs("\a"), panes);
        var l = Assert.Single(got);
        Assert.Equal(5173, l.Port);
        Assert.Equal("pane1", l.OwnerPaneId);
    }

    /// A listener no pane owns and no dev runtime explains is system noise.
    [Fact]
    public void DropsUnownedNonDevListeners()
    {
        var listeners = new[] { new RawListener(445, 900, "0.0.0.0") };
        var procs = new[] { new RawProc(900, 4, "svchost.exe", "svchost -k netsvcs", 1) };
        Assert.Empty(new LocalPoller().Build(listeners, procs, Array.Empty<PaneProc>()));
    }

    /// An unowned listener IS kept when a dev runtime explains it — that is the
    /// "other" bucket the panel shows.
    [Fact]
    public void KeepsUnownedDevRuntimeListeners()
    {
        var listeners = new[] { new RawListener(8000, 901, "127.0.0.1") };
        var procs = new[] { new RawProc(901, 1, "python.exe", "python -m http.server", 1) };
        var l = Assert.Single(new LocalPoller().Build(listeners, procs, Array.Empty<PaneProc>()));
        Assert.Equal(8000, l.Port);
        Assert.Null(l.OwnerPaneId);
        Assert.Equal("http.server", l.Framework);
    }

    /// A dev server binds 127.0.0.1 AND ::1: two kernel rows, one server.
    [Fact]
    public void CollapsesDualStackRowsToOne()
    {
        var listeners = new[]
        {
            new RawListener(5173, 200, "127.0.0.1"),
            new RawListener(5173, 200, "::1"),
        };
        var panes = new[] { new PaneProc(100, "pane1", "web", "working") };
        Assert.Single(new LocalPoller().Build(listeners, procs: Procs("x"), panes: panes));
    }

    /// System/Idle are never dev servers.
    [Fact]
    public void SkipsSystemPids()
    {
        var listeners = new[] { new RawListener(135, 4, "0.0.0.0") };
        Assert.Empty(new LocalPoller().Build(listeners, Array.Empty<RawProc>(), Array.Empty<PaneProc>()));
    }

    /// A corrupt ppid chain must not spin the ancestry walk.
    [Fact]
    public void SurvivesACyclicParentChain()
    {
        var listeners = new[] { new RawListener(3000, 10, "127.0.0.1") };
        var procs = new[]
        {
            new RawProc(10, 11, "node.exe", "node server.js", 1),
            new RawProc(11, 10, "node.exe", "node server.js", 1),
        };
        var l = Assert.Single(new LocalPoller().Build(listeners, procs, Array.Empty<PaneProc>()));
        Assert.Null(l.OwnerPaneId);   // terminated, not hung
    }

    [Fact]
    public void StripControlCharsReplacesThemAndLeavesCleanTextAlone()
    {
        Assert.Equal("weird  arg", LocalPoller.StripControlChars("weird \aarg"));
        var clean = "weird xarg";
        Assert.Same(clean, LocalPoller.StripControlChars(clean));
    }
}
