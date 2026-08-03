using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
    public void ClosingAMiddleSession_ActivatesTheNearestLiveNeighbour()
    {
        var store = new SessionStore();
        var a = store.AddNew();
        var b = store.AddNew();
        var c = store.AddNew();
        a.Root.AgentState = AgentState.Working;
        c.Root.AgentState = AgentState.Done;

        var next = store.Remove(b);

        // c is the row that slid up into b's index — downward wins the tie.
        Assert.NotNull(next);
        Assert.Equal(c.Id, next!.Id);
        Assert.Equal(c.Id, store.ActiveSessionId);
        Assert.Equal(2, store.Sessions.Count);
    }

    // The rule the user asked for: closing a tab must not WAKE one. A dormant
    // sibling (bare shell, or an agent that already exited — every pane Idle)
    // is skipped, and "nothing live left in this project" is a legitimate
    // landing place: the workspace shows its empty state.
    [Fact]
    public void ClosingASession_SkipsDormantNeighbours_AndGoesEmptyWhenAllAreDormant()
    {
        var store = new SessionStore();
        store.AddNew();                       // dormant
        var b = store.AddNew();
        store.AddNew();                       // dormant

        var next = store.Remove(b);

        Assert.Null(next);
        Assert.Null(store.ActiveSessionId);
        Assert.Equal(2, store.Sessions.Count);   // still open, just not activated
    }

    [Fact]
    public void ClosingASession_ReachesPastADormantTabForALiveOne()
    {
        var store = new SessionStore();
        var live = store.AddNew();
        live.Root.AgentState = AgentState.Working;
        store.AddNew();                       // dormant, sits between
        var closing = store.AddNew();

        var next = store.Remove(closing);

        Assert.Equal(live.Id, next?.Id);
    }

    // Selection is project-scoped. A live tab in ANOTHER project is not a
    // neighbour — the old index clamp happily jumped across the boundary when
    // you closed the last tab of a project.
    [Fact]
    public void ClosingASession_NeverJumpsToAnotherProject()
    {
        var store = new SessionStore();
        var mine = store.AddNew();
        mine.ProjectId = Guid.NewGuid();
        var theirs = store.AddNew();
        theirs.ProjectId = Guid.NewGuid();
        theirs.Root.AgentState = AgentState.Working;

        var next = store.Remove(mine);

        Assert.Null(next);
        Assert.Null(store.ActiveSessionId);
    }

    // New tabs land at the top of their OWN project's run, not index 0 — the
    // sidebar groups by a stable partition of this array, so jumping the whole
    // list would shuffle unrelated rows.
    [Fact]
    public void PlaceAtProjectTop_SeatsTheNewTabAboveItsSiblingsOnly()
    {
        var store = new SessionStore();
        var pid = Guid.NewGuid();
        var other = store.AddNew();                       // unfiled, stays put
        var first = store.AddNew(); first.ProjectId = pid;
        var second = store.AddNew(); second.ProjectId = pid;
        var fresh = store.AddNew(); fresh.ProjectId = pid;

        store.PlaceAtProjectTop(fresh);

        Assert.Equal(
            new[] { other.Id, fresh.Id, first.Id, second.Id },
            store.Sessions.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void PlaceAtProjectTop_LeavesTheFirstTabOfAProjectWhereItIs()
    {
        var store = new SessionStore();
        var existing = store.AddNew();
        existing.ProjectId = Guid.NewGuid();
        var fresh = store.AddNew();
        fresh.ProjectId = Guid.NewGuid();       // different project — no siblings

        store.PlaceAtProjectTop(fresh);

        Assert.Equal(new[] { existing.Id, fresh.Id }, store.Sessions.Select(s => s.Id).ToArray());
    }

    // ── Dormant tabs ──────────────────────────────────────────────────────
    // Dormant is an EXPLICIT persisted flag, never derived from AgentState: a
    // woken bare shell reports Idle the instant it spawns, so a derived flag
    // would drop the tab back into the Idle drawer the moment you opened it.

    [Fact]
    public void Sleeping_SeatsTheTabAtTheTopOfItsProjectsDormantRun()
    {
        var store = new SessionStore();
        var pid = Guid.NewGuid();
        var a = store.AddNew(); a.ProjectId = pid;
        var b = store.AddNew(); b.ProjectId = pid;
        var c = store.AddNew(); c.ProjectId = pid;

        store.SetDormant(a, true);      // first sleeper: no dormant sibling to sit above
        store.SetDormant(c, true);      // newest sleeper must land ABOVE a

        // Array order IS render order, and the page partitions live-then-dormant:
        // live = [b], dormant = [c, a] — newest-slept first, no timestamp needed.
        Assert.Equal(
            new[] { c.Id, a.Id, b.Id },
            store.Sessions.Select(s => s.Id).ToArray());
        Assert.Equal(
            new[] { c.Id, a.Id },
            store.Sessions.Where(s => s.Dormant).Select(s => s.Id).ToArray());
    }

    [Fact]
    public void Waking_SeatsTheTabBackAtTheTopOfTheActiveRun()
    {
        var store = new SessionStore();
        var pid = Guid.NewGuid();
        var a = store.AddNew(); a.ProjectId = pid;
        var b = store.AddNew(); b.ProjectId = pid;
        var c = store.AddNew(); c.ProjectId = pid;
        store.SetDormant(c, true);

        store.SetDormant(c, false);

        Assert.False(c.Dormant);
        Assert.Equal(
            new[] { c.Id, a.Id, b.Id },
            store.Sessions.Select(s => s.Id).ToArray());
    }

    // A dormant tab is never a landing place — waking one is exactly the thing
    // closing (or sleeping) another tab must not do. It's skipped even though
    // its panes would otherwise look live.
    [Fact]
    public void ClosingASession_SkipsADormantTab_EvenWhenItsPanesLookBusy()
    {
        var store = new SessionStore();
        var pid = Guid.NewGuid();
        var asleep = store.AddNew(); asleep.ProjectId = pid;
        asleep.Root.AgentState = AgentState.Working;   // stale state on a slept tab
        store.SetDormant(asleep, true);
        var closing = store.AddNew(); closing.ProjectId = pid;

        var next = store.Remove(closing);

        Assert.Null(next);
        Assert.Null(store.ActiveSessionId);
    }

    [Fact]
    public void PickActiveAfter_IgnoresTheTabBeingSlept_AndFindsALiveSibling()
    {
        var store = new SessionStore();
        var pid = Guid.NewGuid();
        var live = store.AddNew(); live.ProjectId = pid;
        live.Root.AgentState = AgentState.Done;
        var sleeping = store.AddNew(); sleeping.ProjectId = pid;
        sleeping.Root.AgentState = AgentState.Working;

        Assert.Equal(live.Id, store.PickActiveAfter(sleeping)?.Id);
    }

    [Fact]
    public void PickActiveAfter_ReturnsNull_WhenTheProjectHasNothingElseLive()
    {
        var store = new SessionStore();
        var pid = Guid.NewGuid();
        var dormant = store.AddNew(); dormant.ProjectId = pid;
        store.SetDormant(dormant, true);
        var sleeping = store.AddNew(); sleeping.ProjectId = pid;
        sleeping.Root.AgentState = AgentState.Working;

        Assert.Null(store.PickActiveAfter(sleeping));
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

    // ---- persisted shape ---------------------------------------------------
    //
    // Nothing else pins what sessions.json actually contains, and both board
    // fields are load-bearing across a restart: without them a restored tab
    // forgets it had a board and its board leaf renders as a terminal, which
    // would then spawn a shell. Serializing through the real source-gen context
    // rather than SessionStore.Save so the test never touches the user's store.

    [Fact]
    public void PaneNode_IsBoardSurvivesTheJsonRoundTrip()
    {
        var json = JsonSerializer.Serialize(
            new PaneNode { Name = "login-bug", IsBoard = true },
            SessionStoreJsonContext.Default.PaneNode);
        Assert.Contains("\"IsBoard\": true", json);

        var back = JsonSerializer.Deserialize(json, SessionStoreJsonContext.Default.PaneNode)!;
        Assert.True(back.IsBoard);
        Assert.True(back.IsBoardPane);
        Assert.False(back.IsTerminal);
    }

    [Fact]
    public void Session_BoardPathSurvivesTheJsonRoundTrip()
    {
        var dto = new SessionStoreDto
        {
            Version = 3,
            Sessions = new List<Session>
            {
                new() { Title = "login bug", BoardPath = @"C:\dev\perch\.perch\boards\login-bug" },
            },
        };
        var json = JsonSerializer.Serialize(dto, SessionStoreJsonContext.Default.SessionStoreDto);
        var back = JsonSerializer.Deserialize(json, SessionStoreJsonContext.Default.SessionStoreDto)!;
        Assert.Equal(@"C:\dev\perch\.perch\boards\login-bug", back.Sessions![0].BoardPath);
    }

    [Fact]
    public void OlderStoreWithoutTheBoardFields_LoadsWithSaneDefaults()
    {
        // Exactly what a v3 file written before boards existed looks like. The
        // additive-field contract is that this needs no migration.
        const string legacy = """
        {
          "Version": 3,
          "Sessions": [
            { "Id": "11111111-2222-3333-4444-555555555555", "Title": "main",
              "Root": { "Id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "Split": null,
                        "Children": [], "Url": null, "Name": "pane-1", "ColorIndex": 3, "Weight": 1 } }
          ]
        }
        """;
        var back = JsonSerializer.Deserialize(legacy, SessionStoreJsonContext.Default.SessionStoreDto)!;
        var sess = back.Sessions![0];
        Assert.Equal("", sess.BoardPath);          // not null — the page reads it as a string
        Assert.False(sess.Root.IsBoard);
        Assert.True(sess.Root.IsTerminal);         // still a terminal, still spawns
    }
}
