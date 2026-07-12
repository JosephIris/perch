using System.Linq;
using Xunit;

namespace Perch.Tests;

// Closing sessions. The rule worth pinning: ZERO sessions is a legitimate state.
//
// Remove() used to seed a fresh "main" session the instant the last one closed,
// so "close the last thing" was impossible — the pane you closed was replaced by
// a new one in the same breath, and the app could never show an empty state.
// In project mode it was worse: that auto-seeded session belonged to no project,
// so it had no row anywhere in the sidebar while still owning a live pane. A ghost.
public class SessionStoreTests
{
    [Fact]
    public void ClosingTheLastSession_LeavesTheStoreEmpty_AndDoesNotConjureANewOne()
    {
        var store = new SessionStore();
        var only = store.AddNew();
        store.ActiveSessionId = only.Id;

        var next = store.Remove(only);

        Assert.Null(next);                    // nothing to make active
        Assert.Empty(store.Sessions);         // and NOT a fresh "main"
        Assert.Null(store.ActiveSessionId);
    }

    [Fact]
    public void ClosingTheLastSession_StillArchivesIt_SoItCanBeRestored()
    {
        var store = new SessionStore();
        var only = store.AddNew();
        only.Title = "the last one";

        store.Remove(only);

        // Empty workspace, but the work isn't gone — it's in Recently closed.
        Assert.Empty(store.Sessions);
        Assert.Contains(store.ClosedSessions, s => s.Title == "the last one");
    }

    [Fact]
    public void ClosingAMiddleSession_ActivatesANeighbour()
    {
        var store = new SessionStore();
        var a = store.AddNew();
        var b = store.AddNew();
        var c = store.AddNew();

        var next = store.Remove(b);

        Assert.NotNull(next);
        Assert.Equal(next!.Id, store.ActiveSessionId);
        Assert.Contains(next.Id, new[] { a.Id, c.Id });
        Assert.Equal(2, store.Sessions.Count);
    }

    // Load() must tell a FRESH INSTALL (no file) from "I closed everything" (a
    // file that says zero sessions). It couldn't, and seeded a "main" shell in
    // both cases — so closing the last session and restarting handed you a new
    // one anyway, and the empty state was unreachable a second way.
    [Fact]
    public void Load_RespectsAFileThatSaysZeroSessions()
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "perch-store-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "perch"));
        var prev = System.Environment.GetEnvironmentVariable("PERCH_DATA_DIR");
        try
        {
            System.Environment.SetEnvironmentVariable("PERCH_DATA_DIR", dir);

            // A store that has been emptied by the user, then saved.
            var store = new SessionStore();
            var s = store.AddNew();
            store.Remove(s);
            Assert.Empty(store.Sessions);
            store.Save();

            var reloaded = SessionStore.Load();

            Assert.Empty(reloaded.Sessions);                      // NOT a fresh "main"
            Assert.Null(reloaded.ActiveSessionId);
            // …and the closed one is still restorable, which the old code dropped
            // on the floor: it skipped the whole load block when Sessions was
            // empty, taking Recently-closed with it.
            Assert.Single(reloaded.ClosedSessions);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("PERCH_DATA_DIR", prev);
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Load_SeedsAFirstSessionOnAFreshInstall()
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "perch-store-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "perch"));
        var prev = System.Environment.GetEnvironmentVariable("PERCH_DATA_DIR");
        try
        {
            System.Environment.SetEnvironmentVariable("PERCH_DATA_DIR", dir);

            // No sessions.json at all → a first-run user still gets a terminal.
            var fresh = SessionStore.Load();

            Assert.Single(fresh.Sessions);
            Assert.NotNull(fresh.ActiveSessionId);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("PERCH_DATA_DIR", prev);
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void RestoreBringsTheLastSessionBack_FromAnEmptyStore()
    {
        var store = new SessionStore();
        var only = store.AddNew();
        only.Title = "brought back";
        store.Remove(only);
        Assert.Empty(store.Sessions);

        var back = store.Restore(only.Id);

        Assert.NotNull(back);
        Assert.Equal("brought back", back!.Title);
        Assert.Single(store.Sessions);
    }
}
