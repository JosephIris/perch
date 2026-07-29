using Perch;
using Xunit;

namespace Perch.Tests;

/// The board used to treat any non-note, non-URL text as a file path. Staging
/// a shell command therefore came back as "That path is outside this project,
/// so the agent couldn't open it: ! git reset --hard origin/main" — a
/// containment error for something that was never a path.
public class BoardPathsTests
{
    /// The exact string that produced the confusing error.
    [Fact]
    public void TheShellCommandThatCausedThisIsNotAPath()
    {
        Assert.False(BoardPaths.LooksLikeAPath("! git reset --hard origin/main"));
    }

    [Theory]
    [InlineData("git reset --hard origin/main")]   // no sigil, still a command
    [InlineData("npm --prefix src/web run build")]
    [InlineData("$env:FOO = 'bar'")]
    [InlineData("# a markdown heading")]
    [InlineData("this and/or that")]
    [InlineData("just some prose about the thing")]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsTextThatWasNeverAPath(string text)
        => Assert.False(BoardPaths.LooksLikeAPath(text));

    [Fact]
    public void RejectsMultilineText()
        => Assert.False(BoardPaths.LooksLikeAPath("src/a.ts\nsrc/b.ts"));

    [Theory]
    [InlineData("src/web/src/board-pane.ts")]
    [InlineData("src\\Perch\\BoardController.cs")]
    [InlineData("README.md")]
    [InlineData("C:\\Users\\josep\\notes.md")]
    [InlineData("design-loop/report.html")]
    [InlineData("a file with spaces.md")]
    public void AcceptsThingsThatPlausiblyNameAFile(string text)
        => Assert.True(BoardPaths.LooksLikeAPath(text));

    [Fact]
    public void ShapeIsIndependentOfExistence()
    {
        // Shape only. Whether it exists is a different question with a
        // different message, which is the distinction that was missing.
        Assert.True(BoardPaths.LooksLikeAPath("does/not/exist.ts"));
    }

    [Fact]
    public void TryAbsoluteResolvesRelativeToTheRepo()
    {
        var got = BoardPaths.TryAbsolute("src/a.ts", @"C:\repo");
        Assert.Equal(@"C:\repo\src\a.ts", got);
    }

    [Fact]
    public void TryAbsoluteKeepsAnAlreadyRootedPath()
    {
        var got = BoardPaths.TryAbsolute(@"D:\elsewhere\notes.md", @"C:\repo");
        Assert.Equal(@"D:\elsewhere\notes.md", got);
    }

    [Fact]
    public void TryAbsoluteRefusesARelativePathWithNoRepo()
        => Assert.Null(BoardPaths.TryAbsolute("src/a.ts", null));
}

/// The scope rule itself: WHO staged a path decides whether it may leave the
/// project, not merely WHERE it points.
public class BoardScopeTests
{
    [Fact]
    public void AnOmittedOriginIsNotTreatedAsAUserGesture()
    {
        // The restrictive default is the whole safety property: a replayed or
        // synthesized message must never be able to widen an agent's read scope
        // by simply leaving the field out.
        var msg = new BoardAddMsg { PaneId = System.Guid.NewGuid(), Kind = "auto", Text = "x.md" };
        Assert.False(msg.IsUserStaged);
    }

    [Fact]
    public void OnlyTheExactUserOriginCounts()
    {
        var id = System.Guid.NewGuid();
        Assert.True(new BoardAddMsg { PaneId = id, Kind = "auto", Text = "x", Origin = "user" }.IsUserStaged);
        Assert.False(new BoardAddMsg { PaneId = id, Kind = "auto", Text = "x", Origin = "agent" }.IsUserStaged);
        Assert.False(new BoardAddMsg { PaneId = id, Kind = "auto", Text = "x", Origin = "User" }.IsUserStaged);
        Assert.False(new BoardAddMsg { PaneId = id, Kind = "auto", Text = "x", Origin = "" }.IsUserStaged);
    }
}
