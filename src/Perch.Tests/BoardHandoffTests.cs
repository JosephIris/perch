using System;
using System.IO;
using Xunit;

namespace Perch.Tests;

/// The board → agent handoff.
///
/// The host half writes a marker file per TERMINAL pane; the CLI half (the
/// UserPromptSubmit hook in perch-cli) reads it and prints additionalContext.
/// These tests pin the host half plus the contract between them, because the
/// two live in different processes and nothing else connects them.
///
/// The single most important property here is the KEY. PERCH_PANE_ID inside a
/// pane's shell is that TERMINAL's id — the hook has no idea a board pane
/// exists — so a marker keyed by the board's id would be a file nobody ever
/// reads, and the failure would be completely silent.
public class BoardHandoffTests
{
    private static BoardController NewController(Func<Guid, Session?> lookup)
        // Run posted work inline: these tests have no dispatcher and don't
        // exercise the async fetch path.
        => new(lookup, a => a());

    private static Session SessionWithBoard(string boardPath, out PaneNode term, out PaneNode board)
    {
        term = new PaneNode { Name = "auth-refactor" };
        board = new PaneNode { Name = "login-bug", IsBoard = true };
        return new Session
        {
            Title = "login bug",
            BoardPath = boardPath,
            Root = new PaneNode
            {
                Split = SplitOrientation.Vertical,
                Children = { term, board },
            },
        };
    }

    private static string TempBoardDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "perch-handoff-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void MarkerPath_MatchesTheNFormatTheShellExports()
    {
        // Shell.BuildStartupCommandLine sets PERCH_PANE_ID = paneId.ToString("N"),
        // and the hook builds its filename from that. A format mismatch here is
        // a file nobody reads, with no error anywhere.
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.EndsWith("perch-board-11111111222233334444555555555555.txt",
            BoardController.MarkerPathFor(id));
        Assert.StartsWith(Path.GetTempPath(), BoardController.MarkerPathFor(id));
    }

    [Fact]
    public void PublishMarkers_WritesForTerminalsOnly()
    {
        var dir = TempBoardDir();
        var sess = SessionWithBoard(dir, out var term, out var board);
        var web = new PaneNode { Url = "https://example.com" };
        sess.Root.Children.Add(web);
        try
        {
            NewController(_ => sess).PublishMarkers(sess);

            Assert.True(File.Exists(BoardController.MarkerPathFor(term.Id)));
            Assert.Equal(dir, File.ReadAllText(BoardController.MarkerPathFor(term.Id)).Trim());
            // A board pane has no shell and a browser pane has no agent; a
            // marker for either is a file that can never be read.
            Assert.False(File.Exists(BoardController.MarkerPathFor(board.Id)));
            Assert.False(File.Exists(BoardController.MarkerPathFor(web.Id)));
        }
        finally { CleanUp(dir, term, board, web); }
    }

    [Fact]
    public void PublishMarkers_EveryTerminalInTheTabGetsTheSameBoard()
    {
        // Session scoping means exactly this: two agents in one tab share it.
        var dir = TempBoardDir();
        var sess = SessionWithBoard(dir, out var a, out var board);
        var b = new PaneNode { Name = "tests" };
        sess.Root.Children.Add(b);
        try
        {
            NewController(_ => sess).PublishMarkers(sess);
            Assert.Equal(dir, File.ReadAllText(BoardController.MarkerPathFor(a.Id)).Trim());
            Assert.Equal(dir, File.ReadAllText(BoardController.MarkerPathFor(b.Id)).Trim());
        }
        finally { CleanUp(dir, a, b, board); }
    }

    [Fact]
    public void PublishMarkers_RemovesTheMarkerWhenTheBoardIsGone()
    {
        var dir = TempBoardDir();
        var sess = SessionWithBoard(dir, out var term, out var board);
        var ctrl = NewController(_ => sess);
        try
        {
            ctrl.PublishMarkers(sess);
            Assert.True(File.Exists(BoardController.MarkerPathFor(term.Id)));

            // Folder deleted underneath us: the agent must stop being told
            // about a board that isn't there, rather than being pointed at a
            // path whose read will fail every turn.
            Directory.Delete(dir, recursive: true);
            ctrl.PublishMarkers(sess);
            Assert.False(File.Exists(BoardController.MarkerPathFor(term.Id)));

            // And likewise when the session simply has no board.
            Directory.CreateDirectory(dir);
            ctrl.PublishMarkers(sess);
            Assert.True(File.Exists(BoardController.MarkerPathFor(term.Id)));
            sess.BoardPath = "";
            ctrl.PublishMarkers(sess);
            Assert.False(File.Exists(BoardController.MarkerPathFor(term.Id)));
        }
        finally { CleanUp(dir, term, board); }
    }

    [Fact]
    public void ClearMarker_IsSafeWhenThereIsNothingToClear()
    {
        // Called on every pane close, including panes that never had a board.
        BoardController.ClearMarker(Guid.NewGuid());
        BoardController.ClearMarker(Guid.Empty);
    }

    [Fact]
    public void PublishMarkers_SurvivesASessionWithNoPanesAndAJunkPath()
    {
        // Runs on every spawn and every board create; it must never be the
        // thing that throws.
        var empty = new Session { Root = new PaneNode { Split = SplitOrientation.Vertical } };
        NewController(_ => empty).PublishMarkers(empty);

        var junk = SessionWithBoard("C:\\<not>|a*path", out var t, out var b);
        NewController(_ => junk).PublishMarkers(junk);
        CleanUp(null, t, b);
    }

    private static void CleanUp(string? dir, params PaneNode[] panes)
    {
        foreach (var p in panes) BoardController.ClearMarker(p.Id);
        if (dir != null) try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
