using System;
using System.IO;
using Xunit;

namespace Perch.Tests;

/// What a board does with TEXT. This is the file that exists because of one bug:
/// pasting an ordinary sentence onto a board produced "That path is outside this
/// project, so the agent couldn't open it" and added nothing. The classifier had
/// exactly two outcomes for a paste — a URL, or a file path — so every note the
/// user ever tried to paste was reported as a broken path, and the error blanked
/// the whole surface on its way out.
///
/// The rule these tests pin: for an "auto" add (a paste), a NOTE is the fallback.
/// Only an explicit pick ("path") may fail.
///
/// The SHAPE question — is this text even meant to be a path — belongs to
/// BoardPaths and is pinned by BoardPathsTests. These tests are about what the
/// board does with the answer.
public class BoardAddTests
{
    // ---- the classifier, against a real board on disk ----------------------

    [Fact]
    public void Auto_PlainTextBecomesANoteInsteadOfAFailedPath()
    {
        using var repo = new TempRepo();
        var (ctrl, pane) = repo.Controller();
        var failures = repo.CaptureFailures(ctrl);

        ctrl.OnAdd(new BoardAddMsg
        {
            PaneId = pane, Kind = "auto", X = 16, Y = 16,
            Text = "the login flow breaks on the second attempt",
        });

        var node = Assert.Single(repo.Store(ctrl).Doc.Nodes);
        Assert.Equal("note", node.Kind);
        Assert.Equal("the login flow breaks on the second attempt", node.Text);
        Assert.Null(node.Ref);
        Assert.Empty(failures);          // and NOTHING was reported at the user
    }

    [Fact]
    public void Auto_PathShapedTextThatIsntThereStillBecomesANote()
    {
        // "src/gone.ts" looks like a path and isn't one. Refusing it would lose
        // what the user pasted; a note keeps the text and stays out of the way.
        using var repo = new TempRepo();
        var (ctrl, pane) = repo.Controller();
        var failures = repo.CaptureFailures(ctrl);

        ctrl.OnAdd(new BoardAddMsg { PaneId = pane, Kind = "auto", Text = "src/gone.ts", X = 0, Y = 0 });

        var node = Assert.Single(repo.Store(ctrl).Doc.Nodes);
        Assert.Equal("note", node.Kind);
        Assert.Equal("src/gone.ts", node.Text);
        Assert.Empty(failures);
    }

    [Fact]
    public void Auto_AFileThatExistsInTheRepoStillBecomesAPathNode()
    {
        // The reason the path branch exists at all — pasting a real path from a
        // terminal has to stage the live file, repo-relative.
        using var repo = new TempRepo();
        var (ctrl, pane) = repo.Controller();
        var abs = repo.WriteFile("src/app.ts", "export {}\n");

        ctrl.OnAdd(new BoardAddMsg { PaneId = pane, Kind = "auto", Text = abs, X = 0, Y = 0 });

        var node = Assert.Single(repo.Store(ctrl).Doc.Nodes);
        Assert.Equal("path", node.Kind);
        Assert.Equal("src/app.ts", node.Ref);
    }

    [Fact]
    public void Auto_ARepoRelativePathResolvesAgainstTheREPONotTheProcess()
    {
        // The commonest paste there is: copy a path out of a terminal, where it
        // is printed relative to the repo. Containment used to be judged on the
        // raw text, so Path.GetFullPath resolved it against Perch's own working
        // directory (the install folder) and a file plainly inside the project
        // came back "outside this project" — or, once external files were
        // allowed, was stored as a non-portable absolute path.
        using var repo = new TempRepo();
        var (ctrl, pane) = repo.Controller();
        repo.WriteFile("src/auth/session.ts", "export {}\n");

        ctrl.OnAdd(new BoardAddMsg
        {
            PaneId = pane, Kind = "auto", Text = "src/auth/session.ts", X = 0, Y = 0,
        });

        var node = Assert.Single(repo.Store(ctrl).Doc.Nodes);
        Assert.Equal("path", node.Kind);
        Assert.Equal("src/auth/session.ts", node.Ref);   // portable, not absolute
        Assert.Null(node.ExtRef);
    }

    [Fact]
    public void Auto_ATraversalOutOfTheRepoIsStillCaught()
    {
        // Resolving before the containment check must not become a way past it:
        // GetFullPath normalizes the "..", and ToRepoRelative then judges the
        // real destination — which is the whole reason it compares full paths.
        using var repo = new TempRepo();
        var (ctrl, pane) = repo.Controller();
        var failures = repo.CaptureFailures(ctrl);

        ctrl.OnAdd(new BoardAddMsg
        {
            PaneId = pane, Kind = "auto", X = 0, Y = 0,
            Text = "src/../../outside-the-repo.txt",
        });

        Assert.Empty(repo.Store(ctrl).Doc.Nodes);
        Assert.Contains("outside this project", Assert.Single(failures));
    }

    [Fact]
    public void Auto_AWebAddressStillBecomesAReference()
    {
        using var repo = new TempRepo();
        var (ctrl, pane) = repo.Controller();

        ctrl.OnAdd(new BoardAddMsg
        {
            PaneId = pane, Kind = "auto", Text = "https://example.com/oauth", X = 0, Y = 0,
        });

        var node = Assert.Single(repo.Store(ctrl).Doc.Nodes);
        Assert.Equal("url", node.Kind);
        Assert.Equal("https://example.com/oauth", node.Source);
    }

    [Fact]
    public void Note_KeepsTextThatWouldOtherwiseClassifyAsSomethingElse()
    {
        // The toolbar's "add a note" is a promise: what you typed is a note,
        // even when it happens to be a URL or a filename.
        using var repo = new TempRepo();
        var (ctrl, pane) = repo.Controller();

        ctrl.OnAdd(new BoardAddMsg
        {
            PaneId = pane, Kind = "note", Text = "https://example.com/oauth", X = 0, Y = 0,
        });

        var node = Assert.Single(repo.Store(ctrl).Doc.Nodes);
        Assert.Equal("note", node.Kind);
        Assert.Equal("https://example.com/oauth", node.Text);
    }

    [Fact]
    public void Path_OutsideTheProjectFollowsProvenanceNotTheKind()
    {
        // The provenance gate (v1.43.0) still governs out-of-repo files, and the
        // note fallback must NOT become a way around it: an agent-staged
        // external path is refused, the same path staged by the user lands as an
        // ExtRef. Anything else and "a note is the fallback" would have quietly
        // widened what an agent can put in front of itself.
        using var repo = new TempRepo();
        var (ctrl, pane) = repo.Controller();
        var failures = repo.CaptureFailures(ctrl);

        var outside = Path.Combine(Path.GetTempPath(), "perch-outside-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        File.WriteAllText(outside, "a reference file\n");
        try
        {
            ctrl.OnAdd(new BoardAddMsg { PaneId = pane, Kind = "path", Text = outside, X = 0, Y = 0 });
            Assert.Empty(repo.Store(ctrl).Doc.Nodes);
            Assert.Contains("outside this project", Assert.Single(failures));

            // Same file, same kind, human gesture (the file picker and a paste
            // both set this).
            ctrl.OnAdd(new BoardAddMsg
            {
                PaneId = pane, Kind = "path", Text = outside, X = 0, Y = 0, Origin = "user",
            });
            var node = Assert.Single(repo.Store(ctrl).Doc.Nodes);
            Assert.Equal("path", node.Kind);
            Assert.Null(node.Ref);
            Assert.Equal(Path.GetFullPath(outside), node.ExtRef);
        }
        finally { try { File.Delete(outside); } catch { } }
    }

    [Fact]
    public void Path_TextThatIsntEvenAPathIsRefusedForAPickButKeptForAPaste()
    {
        using var repo = new TempRepo();
        var (ctrl, pane) = repo.Controller();
        var failures = repo.CaptureFailures(ctrl);

        ctrl.OnAdd(new BoardAddMsg { PaneId = pane, Kind = "path", Text = "! git reset --hard", X = 0, Y = 0 });
        Assert.Empty(repo.Store(ctrl).Doc.Nodes);
        Assert.Contains("doesn't look like a file path", Assert.Single(failures));

        ctrl.OnAdd(new BoardAddMsg { PaneId = pane, Kind = "auto", Text = "! git reset --hard", X = 0, Y = 0 });
        Assert.Equal("note", Assert.Single(repo.Store(ctrl).Doc.Nodes).Kind);
    }

    [Fact]
    public void Path_AnExplicitPickThatIsMissingNamesTheFile()
    {
        using var repo = new TempRepo();
        var (ctrl, pane) = repo.Controller();
        var failures = repo.CaptureFailures(ctrl);

        ctrl.OnAdd(new BoardAddMsg
        {
            PaneId = pane, Kind = "path", X = 0, Y = 0,
            Text = Path.Combine(repo.Root, "src", "gone.ts"),
        });

        Assert.Empty(repo.Store(ctrl).Doc.Nodes);
        Assert.Contains("No such file: src/gone.ts", Assert.Single(failures));
    }

    // ---- editing -----------------------------------------------------------

    [Fact]
    public void Edit_ReplacesANotesTextAndPersists()
    {
        using var repo = new TempRepo();
        var (ctrl, pane) = repo.Controller();
        ctrl.OnAdd(new BoardAddMsg { PaneId = pane, Kind = "note", Text = "frist draft", X = 0, Y = 0 });
        var id = repo.Store(ctrl).Doc.Nodes[0].Id;

        ctrl.OnEdit(new BoardEditMsg { PaneId = pane, NodeId = id, Text = "  first draft  " });

        Assert.Equal("first draft", repo.Store(ctrl).Doc.Nodes[0].Text);
        // And it reached disk, not just the in-memory doc.
        Assert.Contains("first draft", File.ReadAllText(Path.Combine(repo.BoardDir, "board.md")));
    }

    [Fact]
    public void Edit_AddsACaptionToAnImageWithoutTouchingItsRef()
    {
        // Captions on artifacts are the point of edit-on-anything: board.md is
        // all an agent gets, and "assets/pasted-1.png" alone says nothing about
        // which screenshot this is.
        using var repo = new TempRepo();
        var (ctrl, pane) = repo.Controller();
        ctrl.OnPaste(pane, new byte[] { 1, 2, 3 }, null, 0, 0);
        var node = repo.Store(ctrl).Doc.Nodes[0];
        Assert.Equal("image", node.Kind);

        ctrl.OnEdit(new BoardEditMsg { PaneId = pane, NodeId = node.Id, Text = "broken state after login" });

        var after = repo.Store(ctrl).Doc.Nodes[0];
        Assert.Equal("broken state after login", after.Text);
        Assert.Equal(node.Ref, after.Ref);
    }

    [Fact]
    public void Edit_AnUnchangedTextIsNotAWrite()
    {
        // Opening an editor and closing it must not rewrite board.md — that
        // would be a git-visible change for a glance.
        using var repo = new TempRepo();
        var (ctrl, pane) = repo.Controller();
        ctrl.OnAdd(new BoardAddMsg { PaneId = pane, Kind = "note", Text = "unchanged", X = 0, Y = 0 });
        var id = repo.Store(ctrl).Doc.Nodes[0].Id;
        var path = Path.Combine(repo.BoardDir, "board.md");
        var before = File.GetLastWriteTimeUtc(path);

        ctrl.OnEdit(new BoardEditMsg { PaneId = pane, NodeId = id, Text = "unchanged" });

        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Edit_AMissingNodeIsIgnored()
        => Assert.True(new Func<bool>(() =>
        {
            using var repo = new TempRepo();
            var (ctrl, pane) = repo.Controller();
            ctrl.OnEdit(new BoardEditMsg { PaneId = pane, NodeId = "n99", Text = "x" });
            return repo.Store(ctrl).Doc.Nodes.Count == 0;
        })());

    /// A throwaway repo with a board in it, shaped exactly as the real thing —
    /// &lt;repo&gt;/.perch/boards/&lt;slug&gt; — because RepoRootFor walks three levels up
    /// from the board dir and a flatter fixture wouldn't exercise it.
    private sealed class TempRepo : IDisposable
    {
        public string Root { get; }
        public string BoardDir { get; }
        private readonly Session _sess;

        public TempRepo()
        {
            Root = Path.Combine(Path.GetTempPath(), "perch-board-" + Guid.NewGuid().ToString("N")[..8]);
            BoardDir = Path.Combine(Root, ".perch", "boards", "login-bug");
            Directory.CreateDirectory(BoardDir);
            _sess = new Session
            {
                Title = "login bug",
                BoardPath = BoardDir,
                Root = new PaneNode { Split = SplitOrientation.Vertical, Children = { new PaneNode() } },
            };
        }

        /// The controller plus the pane id to address it with. Posted work runs
        /// inline: these tests have no dispatcher and don't hit the fetch path.
        public (BoardController, Guid) Controller()
            => (new BoardController(_ => _sess, a => a()), _sess.Root.Children[0].Id);

        public BoardStore Store(BoardController ctrl) => ctrl.StoreFor(_sess)!;

        /// Collect what the pane would have been told, so a test can assert that
        /// nothing was reported as well as that something was. Only the NON-fatal
        /// ones: a per-action refusal is what these tests are about, and a fatal
        /// "this board can't be opened" would mean the fixture is broken.
        public System.Collections.Generic.List<string> CaptureFailures(BoardController ctrl)
        {
            var seen = new System.Collections.Generic.List<string>();
            ctrl.Failed += (_, message, fatal) => { if (!fatal) seen.Add(message); };
            return seen;
        }

        public string WriteFile(string relative, string content)
        {
            var abs = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, content);
            return abs;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
