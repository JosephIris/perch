using System;
using Perch;
using Xunit;

namespace Perch.Tests;

/// Lines left unsent when Perch closed go out again a few seconds after
/// launch. A line queued in those seconds must go in once, and must not push
/// the previous run's lines out of the save.
public class LaunchUnsentTests
{
    private static readonly Guid Tab = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    [Fact]
    public void ALineQueuedBeforeTheRestoreIsNotQueuedAgain()
    {
        var l = new LaunchUnsent(new[] { (Tab, Array.Empty<string>()) });
        // The phone sends while the tab's Claude starts: saved with the live queue…
        Assert.Equal(new[] { "run the tests" }, l.Persisted(Tab, new[] { "run the tests" }));
        // …and the restore has nothing of the previous run's to queue.
        Assert.Empty(l.Take());
    }

    [Fact]
    public void ThePreviousRunsLinesSurviveANewLineAndGoOutOnce()
    {
        var l = new LaunchUnsent(new[] { (Tab, new[] { "old one" }), (Other, Array.Empty<string>()) });
        // A save before the restore keeps them, ahead of the new line.
        Assert.Equal(new[] { "old one", "new one" }, l.Persisted(Tab, new[] { "new one" }));
        var (id, lines) = Assert.Single(l.Take());
        Assert.Equal(Tab, id);
        Assert.Equal(new[] { "old one" }, lines);
        // Restored: from now on only the live queue is saved, and nothing comes back.
        Assert.Equal(new[] { "new one" }, l.Persisted(Tab, new[] { "new one" }));
        Assert.Empty(l.Take());
    }
}
