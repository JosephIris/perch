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
}
