using Xunit;

namespace Perch.Tests;

// What a torn-down pane's leftovers may and may not include. The measured
// cost of getting this wrong: an idle agent session held four processes, and
// its MCP plugin server (two bun processes, ~280-390 MB) was as expensive as
// Claude itself. A teardown that stops at claude.exe recovers half.
//
// The line it must not cross is PaneJob's deliberate design — a dev server
// backgrounded from a pane has to outlive the pane, because that is the whole
// point of the Local panel. So "serving" is the exemption, and it is defined
// here exactly as the Local panel defines it.
public class JobSweepTests
{
    private static RawProc P(int pid, int ppid, string name) => new(pid, ppid, name, name, 0);
    private static RawListener L(int port, int pid) => new(port, pid, "127.0.0.1");

    [Fact]
    public void AListenerIsServing()
    {
        var serving = JobSweep.ServingPids(
            new[] { L(5173, 900) },
            new[] { P(900, 100, "node") });
        Assert.Contains(900, serving);
    }

    // `npm run dev` wrapping the `node` that actually listens: killing the
    // parent would ORPHAN the listener, which is strictly worse than leaving
    // both alone. So every ancestor of a listener is spared too.
    [Fact]
    public void TheParentOfAListenerIsServing()
    {
        var serving = JobSweep.ServingPids(
            new[] { L(5173, 900) },
            new[] { P(900, 800, "node"), P(800, 100, "npm"), P(100, 4, "pwsh") });
        Assert.Contains(800, serving);
        Assert.Contains(100, serving);
    }

    // The whole reason the sweep exists: an MCP stdio server has no port, so
    // nothing exempts it, so it gets reclaimed.
    [Fact]
    public void AnMcpServerWithNoPortIsNotServing()
    {
        var serving = JobSweep.ServingPids(
            new[] { L(5173, 900) },
            new[] { P(900, 100, "node"), P(555, 400, "bun"), P(400, 100, "claude") });
        Assert.DoesNotContain(555, serving);
        Assert.DoesNotContain(400, serving);
    }

    [Fact]
    public void NothingListening_NothingIsSpared()
    {
        var serving = JobSweep.ServingPids(
            Array.Empty<RawListener>(),
            new[] { P(900, 100, "node"), P(100, 4, "pwsh") });
        Assert.Empty(serving);
    }

    // System/Idle pids are never attributed to anything.
    [Fact]
    public void SystemPidsAreIgnored()
    {
        var serving = JobSweep.ServingPids(
            new[] { L(445, 4) },
            new[] { P(4, 0, "System") });
        Assert.Empty(serving);
    }

    // A corrupt or cyclic ppid chain must terminate rather than spin.
    [Fact]
    public void ACyclicParentChainTerminates()
    {
        var serving = JobSweep.ServingPids(
            new[] { L(5173, 900) },
            new[] { P(900, 800, "a"), P(800, 900, "b") });
        Assert.Contains(900, serving);
        Assert.Contains(800, serving);
    }

    // With no probe there is nothing to reap and, crucially, nothing to kill
    // by mistake — the host without a probe stays inert.
    [Fact]
    public async Task NoProbe_ReapsNothing()
    {
        var sweep = new JobSweep(null);
        var snap = new JobSnapshot(Guid.NewGuid(), new[] { new JobMember(1234, 1, "bun") });
        Assert.Equal(0, await sweep.ReapAsync(snap, TimeSpan.Zero, "test"));
    }

    [Fact]
    public async Task AnEmptySnapshotIsANoOp()
    {
        var sweep = new JobSweep(null);
        Assert.Equal(0, await sweep.ReapAsync(JobSnapshot.Empty, TimeSpan.Zero, "test"));
    }

    // Capture is unanswerable without a scope (the job handle dies with the
    // PTY) — it must return empty rather than guess.
    [Fact]
    public async Task CaptureWithoutAScopeIsEmpty()
    {
        var sweep = new JobSweep(null);
        var snap = await sweep.CaptureAsync(Guid.NewGuid(), null);
        Assert.Empty(snap.Members);
    }
}
