using System.Text.Json;
using Xunit;

namespace Perch.Tests;

// The sidebar/session aggregation rules. These encode deliberate product
// decisions (Done outranks Working so "your move" surfaces; a working pane's
// stale turn-end must never leak into "finished Xm ago") that have each been
// the subject of a fix commit — pin them here.
public class StateProjectionTests
{
    private static PaneNode Pane(AgentState state, long turnStart = 0, long doneAt = 0) =>
        new() { AgentState = state, TurnStartUnixMs = turnStart, DoneAtUnixMs = doneAt };

    private static Session SessionWith(params PaneNode[] panes)
    {
        var root = panes.Length == 1
            ? panes[0]
            : new PaneNode { Split = SplitOrientation.Vertical, Children = panes.ToList() };
        return new Session { Root = root, Shell = "pwsh.exe" };
    }

    private static JsonElement Project(Session s) =>
        JsonSerializer.SerializeToElement(StateProjection.ProjectSession(s));

    // ---- AggregateState priority: Permission > Waiting > Done > Working > Idle

    [Fact]
    public void Aggregate_PermissionDominatesEverything()
    {
        var st = StateProjection.AggregateState(new[]
        {
            Pane(AgentState.Working), Pane(AgentState.Waiting), Pane(AgentState.Permission),
        });
        Assert.Equal(AgentState.Permission, st);
    }

    [Fact]
    public void Aggregate_WaitingOutranksDone()
    {
        var st = StateProjection.AggregateState(new[] { Pane(AgentState.Done), Pane(AgentState.Waiting) });
        Assert.Equal(AgentState.Waiting, st);
    }

    [Fact]
    public void Aggregate_DoneOutranksWorking_YourMoveSurfaces()
    {
        // A session with one finished pane reads "ready" even while another
        // pane still churns — deliberate, so a free agent isn't hidden.
        var st = StateProjection.AggregateState(new[] { Pane(AgentState.Working), Pane(AgentState.Done) });
        Assert.Equal(AgentState.Done, st);
    }

    [Fact]
    public void Aggregate_EmptyIsIdle()
    {
        Assert.Equal(AgentState.Idle, StateProjection.AggregateState(Array.Empty<PaneNode>()));
    }

    // ---- Session-row projection ---------------------------------------------

    [Fact]
    public void TurnStart_IsEarliestWorkingPaneOnly()
    {
        var s = SessionWith(
            Pane(AgentState.Working, turnStart: 2000),
            Pane(AgentState.Working, turnStart: 1000),
            Pane(AgentState.Done, turnStart: 500));   // stale value on a done pane must not win

        Assert.Equal(1000, Project(s).GetProperty("turnStartMs").GetInt64());
    }

    [Fact]
    public void DoneAt_OnlyCountsPanesCurrentlyDone()
    {
        // A working pane's PRIOR turn-end (doneAt stays stamped after leaving
        // Done) must not leak into the live "finished Xm ago".
        var s = SessionWith(
            Pane(AgentState.Working, doneAt: 99999),
            Pane(AgentState.Done, doneAt: 1234));

        Assert.Equal(1234, Project(s).GetProperty("doneAtMs").GetInt64());
    }

    [Fact]
    public void GitSignals_SumDiffButMaxAhead()
    {
        // No cwd on either pane → they can't be correlated, so they keep
        // their own terms in the sum.
        var a = Pane(AgentState.Idle);
        a.LinesAdded = 100; a.LinesDeleted = 10; a.FilesChanged = 3; a.Ahead = 2;
        var b = Pane(AgentState.Idle);
        b.LinesAdded = 50; b.LinesDeleted = 5; b.FilesChanged = 1; b.Ahead = 5;
        var s = SessionWith(a, b);

        var row = Project(s);
        Assert.Equal(150, row.GetProperty("linesAdded").GetInt32());
        Assert.Equal(15, row.GetProperty("linesDeleted").GetInt32());
        Assert.Equal(4, row.GetProperty("filesChanged").GetInt32());
        // Panes usually share a branch → max, NOT sum (summing double-counts).
        Assert.Equal(5, row.GetProperty("ahead").GetInt32());
    }

    [Fact]
    public void GitSignals_SameCwdPanesDedupeToTheLargestMeasurement()
    {
        // Two panes in one working tree measure the SAME repo footprint;
        // summing them double-counted every line (the "+180k on a session
        // that changed nothing" bug). The row takes the single largest
        // measurement — a coherent triple from ONE pane, never a per-field
        // max mixing A's adds with B's deletes. Cwd matching is
        // case-insensitive (Windows paths).
        var a = Pane(AgentState.Idle);
        a.Cwd = @"C:\dev\repo";
        a.LinesAdded = 500; a.LinesDeleted = 10; a.FilesChanged = 3;
        var b = Pane(AgentState.Idle);
        b.Cwd = @"c:\DEV\repo";
        b.LinesAdded = 20; b.LinesDeleted = 50; b.FilesChanged = 1;
        var s = SessionWith(a, b);

        var row = Project(s);
        Assert.Equal(500, row.GetProperty("linesAdded").GetInt32());
        Assert.Equal(10, row.GetProperty("linesDeleted").GetInt32());
        Assert.Equal(3, row.GetProperty("filesChanged").GetInt32());
    }

    [Fact]
    public void GitSignals_AttributedSameCwdPanesSumInsteadOfDedupe()
    {
        // The shared-tree case AFTER hook attribution: each pane's stats were
        // already filtered to its own agent's touched files, so they're
        // disjoint by construction. Deduping (largest-wins) here would drop
        // one agent's real work; attributed panes sum.
        var a = Pane(AgentState.Idle);
        a.Cwd = @"C:\dev\repo";
        a.DiffAttributed = true;
        a.LinesAdded = 100; a.LinesDeleted = 90; a.FilesChanged = 3;
        var b = Pane(AgentState.Idle);
        b.Cwd = @"C:\dev\repo";
        b.DiffAttributed = true;
        b.LinesAdded = 43; b.LinesDeleted = 41; b.FilesChanged = 2;
        var s = SessionWith(a, b);

        var row = Project(s);
        Assert.Equal(143, row.GetProperty("linesAdded").GetInt32());
        Assert.Equal(131, row.GetProperty("linesDeleted").GetInt32());
        Assert.Equal(5, row.GetProperty("filesChanged").GetInt32());
    }

    [Fact]
    public void GitSignals_DistinctCwdsStillSum()
    {
        // Panes in different working trees (e.g. worktree-per-pane) are
        // genuinely independent footprints — those keep summing.
        var a = Pane(AgentState.Idle);
        a.Cwd = @"C:\dev\repo-a";
        a.LinesAdded = 100; a.LinesDeleted = 10; a.FilesChanged = 3;
        var b = Pane(AgentState.Idle);
        b.Cwd = @"C:\dev\repo-b";
        b.LinesAdded = 50; b.LinesDeleted = 5; b.FilesChanged = 1;
        var s = SessionWith(a, b);

        var row = Project(s);
        Assert.Equal(150, row.GetProperty("linesAdded").GetInt32());
        Assert.Equal(15, row.GetProperty("linesDeleted").GetInt32());
        Assert.Equal(4, row.GetProperty("filesChanged").GetInt32());
    }

    [Fact]
    public void PaneCounts_WaitingIncludesPermission()
    {
        var s = SessionWith(
            Pane(AgentState.Waiting), Pane(AgentState.Permission),
            Pane(AgentState.Working), Pane(AgentState.Idle));

        var row = Project(s);
        Assert.Equal(4, row.GetProperty("paneCount").GetInt32());
        Assert.Equal(2, row.GetProperty("waitingCount").GetInt32());
        Assert.Equal(1, row.GetProperty("workingCount").GetInt32());
    }

    // ---- Snapshot / pane projection wire shape --------------------------------

    [Fact]
    public void ProjectPane_LeafCarriesTheFieldsThePageReads()
    {
        var leaf = new PaneNode { Name = "api fix", ColorIndex = 3, Weight = 1.5 };
        leaf.AgentState = AgentState.Working;
        var el = JsonSerializer.SerializeToElement(StateProjection.ProjectPane(leaf));

        Assert.Equal("leaf", el.GetProperty("kind").GetString());
        Assert.Equal(leaf.Id.ToString("D"), el.GetProperty("paneId").GetString());
        Assert.Equal("api fix", el.GetProperty("name").GetString());
        Assert.Equal("working", el.GetProperty("agentState").GetString());
        Assert.Equal(1.5, el.GetProperty("weight").GetDouble());
        Assert.Equal(3, el.GetProperty("colorIndex").GetInt32());
    }

    [Fact]
    public void ProjectPane_SplitCarriesOrientationAndChildren()
    {
        var split = new PaneNode
        {
            Split = SplitOrientation.Horizontal,
            Children = new List<PaneNode> { new(), new() },
        };
        var el = JsonSerializer.SerializeToElement(StateProjection.ProjectPane(split));

        Assert.Equal("split", el.GetProperty("kind").GetString());
        Assert.Equal("h", el.GetProperty("orientation").GetString());
        Assert.Equal(2, el.GetProperty("children").GetArrayLength());
    }

    [Fact]
    public void BuildSnapshot_ClosedSessionsCarryResumableCount()
    {
        var store = new SessionStore();
        var live = new Session { Title = "main" };
        store.Sessions.Add(live);
        store.ActiveSessionId = live.Id;

        var closed = new Session { Title = "old work", ClosedAtUnixMs = 42 };
        closed.Root = new PaneNode
        {
            Split = SplitOrientation.Vertical,
            Children = new List<PaneNode>
            {
                new() { ClaudeSessionId = "abc123" },
                new(),   // plain shell — not resumable
            },
        };
        store.ClosedSessions.Add(closed);

        var snap = JsonSerializer.SerializeToElement(
            StateProjection.BuildSnapshot(store, live.Root.Id, fontSize: 14, onboardingSeen: true));

        Assert.Equal("state", snap.GetProperty("type").GetString());
        Assert.Equal(live.Id.ToString("D"), snap.GetProperty("activeSessionId").GetString());
        Assert.Equal(14, snap.GetProperty("prefs").GetProperty("fontSize").GetInt32());

        var closedRow = snap.GetProperty("closedSessions")[0];
        Assert.Equal(2, closedRow.GetProperty("paneCount").GetInt32());
        Assert.Equal(1, closedRow.GetProperty("resumableCount").GetInt32());
        Assert.Equal(42, closedRow.GetProperty("closedAtMs").GetInt64());
    }

    // ---- Project mode -----------------------------------------------------

    [Fact]
    public void Snapshot_CarriesProjectsAndSidebarMode()
    {
        var store = new SessionStore();
        var projects = new ProjectStore();
        // Add() requires the folder to exist on disk (it refuses a path that's
        // gone), so register the repo we're running in.
        var here = System.IO.Directory.GetCurrentDirectory();
        var p = projects.Add(here, "cmux-win")!;

        var filed = SessionWith(Pane(AgentState.Idle));
        filed.ProjectId = p.Id;
        var unfiled = SessionWith(Pane(AgentState.Idle));
        store.Sessions.Add(filed);
        store.Sessions.Add(unfiled);

        var snap = JsonSerializer.SerializeToElement(StateProjection.BuildSnapshot(
            store, null, fontSize: 13, onboardingSeen: true,
            projects: projects, sidebarMode: "projects"));

        Assert.Equal("projects", snap.GetProperty("prefs").GetProperty("sidebarMode").GetString());

        var row = snap.GetProperty("projects")[0];
        Assert.Equal(p.Id.ToString("D"), row.GetProperty("id").GetString());
        Assert.Equal("cmux-win", row.GetProperty("name").GetString());

        // The filed tab carries its project; the unfiled one reports "" so the
        // page can drop it into "Other" rather than losing it.
        Assert.Equal(p.Id.ToString("D"), Project(filed).GetProperty("projectId").GetString());
        Assert.Equal("", Project(unfiled).GetProperty("projectId").GetString());
    }

    [Fact]
    public void Snapshot_DefaultsToSessionModeWithNoProjects()
    {
        var store = new SessionStore();
        store.Sessions.Add(SessionWith(Pane(AgentState.Idle)));

        var snap = JsonSerializer.SerializeToElement(
            StateProjection.BuildSnapshot(store, null, fontSize: 13, onboardingSeen: true));

        Assert.Equal("sessions", snap.GetProperty("prefs").GetProperty("sidebarMode").GetString());
        Assert.Empty(snap.GetProperty("projects").EnumerateArray());
    }
}
