using System;
using Xunit;

namespace Perch.Tests;

// A resolved thread goes to sleep at once instead of keeping its terminal and
// its Claude running until the idle timer, hours later — unless it is still
// busy, because resolving must never cut work off mid-turn.
public class ThreadResolveTests
{
    private static Session Thread(AgentState state, bool resolved = true, bool dormant = false)
    {
        var s = new Session { ThreadOf = Guid.NewGuid(), ThreadResolved = resolved, Dormant = dormant };
        s.Root.AgentState = state;   // a plain leaf is a terminal pane
        return s;
    }

    [Fact]
    public void AResolvedIdleThreadSleeps()
    {
        Assert.True(ThreadController.ShouldSleepOnResolve(Thread(AgentState.Done)));
        Assert.True(ThreadController.ShouldSleepOnResolve(Thread(AgentState.Idle)));
    }

    [Fact]
    public void ABusyThreadIsLeftRunning()
    {
        Assert.False(ThreadController.ShouldSleepOnResolve(Thread(AgentState.Working)));
        Assert.False(ThreadController.ShouldSleepOnResolve(Thread(AgentState.Permission)));
        Assert.False(ThreadController.ShouldSleepOnResolve(Thread(AgentState.Waiting)));
    }

    [Fact]
    public void OnlyAResolvedThreadThatIsAwake()
    {
        Assert.False(ThreadController.ShouldSleepOnResolve(Thread(AgentState.Done, resolved: false)));
        Assert.False(ThreadController.ShouldSleepOnResolve(Thread(AgentState.Done, dormant: true)));
        var notAThread = Thread(AgentState.Done);
        notAThread.ThreadOf = null;
        Assert.False(ThreadController.ShouldSleepOnResolve(notAThread));
    }
}
