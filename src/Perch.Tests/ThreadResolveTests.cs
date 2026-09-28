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

// Like Claude's project chats: a thread with nothing new for a week is
// resolved on its own; one that failed waits on the user.
public class ThreadStaleAndFailedTests
{
    private const long Day = 86_400_000;
    private const long Now = 100 * Day;

    private static Session Thread(AgentState state, long replyAt = 0, long startedAt = 0) =>
        new Session { ThreadOf = Guid.NewGuid(), ThreadReplyAtMs = replyAt, ThreadStartedAtMs = startedAt, Root = { AgentState = state } };

    [Fact]
    public void AWeekQuietIsStale()
    {
        Assert.True(ThreadController.IsStale(Thread(AgentState.Done, replyAt: Now - 8 * Day), Now));
        Assert.True(ThreadController.IsStale(Thread(AgentState.Idle, startedAt: Now - 7 * Day), Now));
        Assert.False(ThreadController.IsStale(Thread(AgentState.Done, replyAt: Now - 6 * Day), Now));
        Assert.False(ThreadController.IsStale(Thread(AgentState.Done, replyAt: Now - 1 * Day, startedAt: Now - 30 * Day), Now));
    }

    [Fact]
    public void NotWhenBusyAlreadyResolvedUntimedOrNotAThread()
    {
        Assert.False(ThreadController.IsStale(Thread(AgentState.Working, replyAt: Now - 30 * Day), Now));
        Assert.False(ThreadController.IsStale(Thread(AgentState.Permission, replyAt: Now - 30 * Day), Now));
        var resolved = Thread(AgentState.Done, replyAt: Now - 30 * Day);
        resolved.ThreadResolved = true;
        Assert.False(ThreadController.IsStale(resolved, Now));
        Assert.False(ThreadController.IsStale(Thread(AgentState.Done), Now));   // no time known
        var tab = Thread(AgentState.Done, replyAt: Now - 30 * Day);
        tab.ThreadOf = null;
        Assert.False(ThreadController.IsStale(tab, Now));
    }

    [Fact]
    public void AnApiErrorIsAFailure()
    {
        Assert.True(ThreadController.IsApiError("API Error: 529 Overloaded"));
        Assert.True(ThreadController.IsApiError("  api error: rate limited"));
        Assert.False(ThreadController.IsApiError("Fixed the API error in the client."));
        Assert.False(ThreadController.IsApiError(null));
    }
}
