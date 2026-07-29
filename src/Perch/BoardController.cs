using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Perch;

/// Owns the live BoardStore for each session that has a board, and answers the
/// page's board.request with the board's contents.
///
/// Sits between MainWindow and BoardStore for the same reason UrlPaneController
/// does: MainWindow is already the message-dispatch and session-management
/// file, and board lifetime is its own concern. The only dependency is a way to
/// look up a session by pane id, which the host passes in.
///
/// Stores are cached per session id and dropped when the session's board path
/// changes, so a reopened board is re-read from disk rather than served from a
/// stale in-memory copy.
internal sealed class BoardController
{
    /// Cached store per session, with the board path and the index's
    /// last-write time it was read at. The timestamp is what makes an edit
    /// from outside Perch — a hand edit, or an agent that decided to tidy the
    /// file — show up instead of being masked by our copy.
    private readonly Dictionary<Guid, (string Path, DateTime Stamp, BoardStore Store)> _bySession = new();

    /// Resolve the session that owns a pane. Supplied by the host so this class
    /// doesn't need the whole SessionStore.
    private readonly Func<Guid, Session?> _sessionOfPane;

    /// Marshal back to the UI thread. Board state is touched from the message
    /// handlers (UI thread) and from background fetches, and the store is not
    /// thread-safe; funnelling every mutation through here means it doesn't
    /// have to be.
    private readonly Action<Action> _uiPost;

    /// Send a board's contents to the page.
    public event Action<Guid, BoardDoc>? StateReady;

    /// Tell the page a board can't be read, with a reason the pane will show.
    public event Action<Guid, string>? Failed;

    public BoardController(Func<Guid, Session?> sessionOfPane, Action<Action> uiPost)
    {
        _sessionOfPane = sessionOfPane;
        _uiPost = uiPost;
    }

    /// Handle board.request: the pane asking for its session's board.
    public void OnRequest(PaneRef msg)
    {
        var sess = _sessionOfPane(msg.PaneId);
        if (sess == null) return;

        if (string.IsNullOrEmpty(sess.BoardPath))
        {
            Failed?.Invoke(msg.PaneId, "This tab has no board.");
            return;
        }

        var store = StoreFor(sess);
        if (store == null)
        {
            // The folder is gone — the tab was restored after the repo moved,
            // or someone deleted it. Say which path we looked for; a bare "not
            // found" leaves the user with nothing to act on.
            Log.Info("Board.missing", $"session={sess.Id:N} path={sess.BoardPath}");
            Failed?.Invoke(msg.PaneId, $"The board folder is missing: {sess.BoardPath}");
            return;
        }
        if (!store.Readable)
        {
            Failed?.Invoke(msg.PaneId, store.Problem);
            return;
        }
        StateReady?.Invoke(msg.PaneId, store.Doc);
    }

    /// The session's board, opening (and caching) it on first use. Null when
    /// the session has no board or its folder is gone.
    public BoardStore? StoreFor(Session sess)
    {
        if (string.IsNullOrEmpty(sess.BoardPath)) return null;

        // Re-check the FOLDER and the index's timestamp, not just the path:
        //   - A board deleted underneath a running app would otherwise keep
        //     serving its cached contents forever, showing nodes whose
        //     artifacts are gone and never surfacing that the folder went away.
        //   - The file is ours to write, but it is also a plain markdown file
        //     sitting in the user's repo. Anything that edits it from outside
        //     should show up rather than being masked by our copy.
        // Both checks are a stat call, and requests are rare.
        if (_bySession.TryGetValue(sess.Id, out var cached))
        {
            if (cached.Path == sess.BoardPath
                && System.IO.Directory.Exists(cached.Path)
                && IndexStamp(cached.Path) == cached.Stamp)
                return cached.Store;
            _bySession.Remove(sess.Id);   // moved, gone, or edited elsewhere
        }

        var store = BoardStore.Open(sess.BoardPath);
        if (store == null) return null;
        _bySession[sess.Id] = (sess.BoardPath, IndexStamp(sess.BoardPath), store);
        return store;
    }

    /// Last-write time of a board's index, or default when it has none yet.
    private static DateTime IndexStamp(string dir)
    {
        try
        {
            var path = System.IO.Path.Combine(dir, "board.md");
            return System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTimeUtc(path) : default;
        }
        catch { return default; }
    }

    /// Record the on-disk state as current after WE wrote it, so our own save
    /// doesn't look like an outside edit and force a pointless re-read.
    public void NoteSaved(Session sess)
    {
        if (_bySession.TryGetValue(sess.Id, out var c))
            _bySession[sess.Id] = (c.Path, IndexStamp(c.Path), c.Store);
    }

    /// Forget a session's cached store (session closed, board detached).
    public void Forget(Guid sessionId)
    {
        _bySession.Remove(sessionId);
    }

    // ---- the agent handoff -------------------------------------------------

    /// Marker file the CLI-side hook reads to learn its tab's board.
    /// %TEMP%\perch-board-&lt;paneId&gt;.txt, matching ClaudeModelState's shape and
    /// existing for the same reason its header gives: the per-pane pipe is
    /// one-way (host reads only), so a temp file is how the host tells the CLI
    /// side anything.
    ///
    /// Keyed by the TERMINAL pane's id, not the board's, because PERCH_PANE_ID
    /// in a pane's shell is that terminal's — the hook has no idea a board pane
    /// exists.
    public static string MarkerPathFor(Guid paneId) =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"perch-board-{paneId:N}.txt");

    /// Publish (or clear) the board marker for every terminal leaf in a
    /// session. Call whenever the board or the session's pane set changes.
    ///
    /// Every terminal in the tab gets the same board — that is what session
    /// scoping means. Panes that aren't terminals are skipped: a browser pane
    /// has no agent, and a board pane has no shell.
    public void PublishMarkers(Session sess)
    {
        var path = sess.BoardPath ?? "";
        foreach (var pane in PaneTree.AllLeaves(sess.Root))
        {
            if (!pane.IsTerminal) continue;
            var marker = MarkerPathFor(pane.Id);
            try
            {
                if (path.Length == 0 || !System.IO.Directory.Exists(path))
                {
                    if (System.IO.File.Exists(marker)) System.IO.File.Delete(marker);
                }
                else
                {
                    AtomicFile.WriteAllText(marker, path);
                }
            }
            catch (Exception ex)
            {
                // Best-effort: a missing marker costs the agent a hint, not the
                // turn. Never let it reach the caller.
                Log.Error("Board.marker", ex);
            }
        }
    }

    /// Drop a pane's marker when it closes, so a recycled temp file can't point
    /// a future pane at a board that isn't its own.
    public static void ClearMarker(Guid paneId)
    {
        try
        {
            var marker = MarkerPathFor(paneId);
            if (System.IO.File.Exists(marker)) System.IO.File.Delete(marker);
        }
        catch { /* best-effort */ }
    }

    // ---- mutations ---------------------------------------------------------
    //
    // Every mutation follows the same three steps: resolve the session's store,
    // change the doc, then persist and re-push. `persist` is false for the
    // continuous half of a drag or resize — the page sends a stream of those
    // and writing board.md on each would rewrite the file hundreds of times per
    // gesture (the gutter drag already makes the same distinction).

    /// Run `mutate` against the pane's board, then save and push. Returns false
    /// when the board can't be reached, having already told the page why.
    private bool Mutate(Guid paneId, Func<BoardStore, bool> mutate, bool persist = true, bool push = true)
    {
        var sess = _sessionOfPane(paneId);
        if (sess == null) return false;
        var store = StoreFor(sess);
        if (store == null || !store.Readable)
        {
            Failed?.Invoke(paneId, store?.Problem ?? "This tab's board can't be opened.");
            return false;
        }
        if (!mutate(store)) return false;
        if (persist) { store.Save(); NoteSaved(sess); }
        if (push) PushToAllBoardPanes(sess, store.Doc);
        return true;
    }

    /// Push state to EVERY board leaf in the session, not just the one that
    /// asked. A tab can have two windows onto the same board, and they must not
    /// disagree about what is on it.
    private void PushToAllBoardPanes(Session sess, BoardDoc doc)
    {
        foreach (var id in BoardPanes(sess)) StateReady?.Invoke(id, doc);
    }

    /// Add a text-derived node: a typed note, a file path, or a URL. Which one
    /// is decided HERE, not by the page — classification needs the repo root
    /// (to make a path repo-relative and to reject one that escapes it), and
    /// the page has neither.
    public void OnAdd(BoardAddMsg msg)
    {
        var text = (msg.Text ?? "").Trim();
        if (text.Length == 0) return;

        Mutate(msg.PaneId, store =>
        {
            var node = new BoardNode { Id = store.Doc.NextId(), X = msg.X, Y = msg.Y };
            var repoRoot = RepoRootFor(msg.PaneId);

            if (msg.Kind == "note")
            {
                node.Kind = "note";
                node.Text = text;
            }
            else if (WebUrlPolicy.Classify(text) == WebUrlKind.Web)
            {
                node.Kind = "url";
                node.Source = text;
                node.Text = msg.Note ?? "";
                // The card appears immediately, showing the URL, and the page
                // is fetched in the background. A link paste must not block the
                // UI thread on someone else's server for fifteen seconds.
                _ = FetchIntoAsync(msg.PaneId, node.Id, text);
            }
            else if (!BoardPaths.LooksLikeAPath(text))
            {
                // Everything that isn't a note and isn't a URL used to be
                // ASSUMED to be a path, so arbitrary text fell through to the
                // containment check and came back wearing a message written for
                // paths — a shell command staged as "That path is outside this
                // project". Text that was never a path isn't a scope failure and
                // must not be reported as one.
                Failed?.Invoke(msg.PaneId,
                    "That doesn't look like a file path or a URL. " +
                    "Stage it as a note if you meant to keep the text.");
                return false;
            }
            else
            {
                var rel = repoRoot == null ? null : BoardStore.ToRepoRelative(repoRoot, text);
                if (rel != null && rel.Length > 0)
                {
                    if (!System.IO.File.Exists(System.IO.Path.Combine(repoRoot!, rel)))
                    {
                        Failed?.Invoke(msg.PaneId, $"No such file: {rel}");
                        return false;
                    }
                    node.Kind = "path";
                    node.Ref = rel;
                    node.Text = msg.Note ?? "";
                }
                else
                {
                    // Outside the project. Allowed only as a deliberate human
                    // gesture: board.md is an agent's read list, so letting an
                    // AGENT stage an external path would let it widen its own
                    // read scope — the exact thing containment prevents. A
                    // person referencing a file they already have open is a
                    // different act, and refusing it was over-broad.
                    if (!msg.IsUserStaged)
                    {
                        Failed?.Invoke(msg.PaneId,
                            "That file is outside this project, so it can only be added by you, " +
                            "not by an agent.");
                        return false;
                    }
                    var full = BoardPaths.TryAbsolute(text, repoRoot);
                    if (full == null || !System.IO.File.Exists(full))
                    {
                        Failed?.Invoke(msg.PaneId, $"No such file: {text}");
                        return false;
                    }
                    node.Kind = "path";
                    node.ExtRef = full;
                    node.Text = msg.Note ?? "";
                }
            }
            store.Doc.Nodes.Add(node);
            return true;
        });
    }

    /// Reposition a node. `final` false is the continuous part of a drag: the
    /// in-memory doc moves so a later save is correct, but nothing is written
    /// and nothing is pushed back (the page is already drawing it).
    public void OnMove(BoardMoveMsg msg) =>
        Mutate(msg.PaneId, store =>
        {
            var n = store.Doc.Find(msg.NodeId);
            if (n == null) return false;
            n.X = msg.X; n.Y = msg.Y;
            return true;
        }, persist: msg.Final, push: msg.Final);

    /// Resize a node. Same final-flag rule as OnMove. Clamping to a sane
    /// minimum happens here rather than in the page so a hand-edited board with
    /// a 2px card still opens usable.
    public void OnResize(BoardResizeMsg msg) =>
        Mutate(msg.PaneId, store =>
        {
            var n = store.Doc.Find(msg.NodeId);
            if (n == null) return false;
            n.W = Math.Clamp(msg.W, MinNodeW, MaxNodeW);
            n.H = Math.Clamp(msg.H, MinNodeH, MaxNodeH);
            return true;
        }, persist: msg.Final, push: msg.Final);

    public const double MinNodeW = 120, MinNodeH = 64;
    public const double MaxNodeW = 1200, MaxNodeH = 1200;

    /// Remove a node, its links, and — for an image — the file it owns.
    public void OnRemove(BoardNodeRefMsg msg) =>
        Mutate(msg.PaneId, store =>
        {
            var n = store.Doc.Find(msg.NodeId);
            if (n == null) return false;
            // Only delete artifacts the BOARD created. A `path` node points at a
            // file in the user's project that the board never owned, and
            // removing the card must never remove their source file. Resolve
            // inside the board's own assets/ by FILENAME rather than trusting
            // the stored relative path, so a hand-edited ref can't aim the
            // delete at something else.
            if (n.Kind == "image" && !string.IsNullOrEmpty(n.Ref))
            {
                try
                {
                    var asset = System.IO.Path.Combine(store.AssetsDir, System.IO.Path.GetFileName(n.Ref!));
                    if (System.IO.File.Exists(asset)) System.IO.File.Delete(asset);
                }
                catch (Exception ex) { Log.Error("Board.removeAsset", ex); }
            }
            store.Doc.Nodes.Remove(n);
            store.Doc.Links.RemoveAll(l => l.From == msg.NodeId || l.To == msg.NodeId);
            return true;
        });

    /// Fetch a URL node's page, cache it as markdown under refs/, and point the
    /// node at it.
    ///
    /// Runs detached from the message handler so a slow site can't hold the UI
    /// thread. It re-resolves the node by id afterwards rather than closing
    /// over it, because the board can have been edited (or the node removed)
    /// while the request was in flight.
    private async Task FetchIntoAsync(Guid paneId, string nodeId, string url)
    {
        var res = await WebFetch.GetAsync(url).ConfigureAwait(false);

        string? markdown = null, title = null, problem = null;
        if (!res.Ok)
        {
            problem = res.Error;
        }
        else
        {
            title = HtmlToMarkdown.ExtractTitle(res.Html);
            Uri.TryCreate(res.FinalUrl, UriKind.Absolute, out var baseUri);
            markdown = HtmlToMarkdown.Convert(res.Html, baseUri);
            if (HtmlToMarkdown.LooksThin(markdown))
            {
                // Do not pretend. A page that renders its content with
                // JavaScript gives us a shell, and writing that as a confident
                // little .md is worse than saying nothing came back.
                problem = "That page didn't have readable text in it (it may need JavaScript). "
                        + "The link is on the board, but there's no cached copy.";
                markdown = null;
            }
        }

        _uiPost(() =>
        {
            Mutate(paneId, store =>
            {
                var node = store.Doc.Find(nodeId);
                if (node == null) return false;          // removed while fetching

                if (!string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(node.Text))
                    node.Text = title!.Length > 120 ? title[..120].TrimEnd() + "…" : title;

                if (markdown == null)
                {
                    node.FetchedUtc = null;
                    if (problem != null) Failed?.Invoke(paneId, problem);
                    // Still worth persisting: the title may have landed.
                    return true;
                }

                var name = NextRefName(store, title ?? url);
                var abs = System.IO.Path.Combine(store.RefsDir, name);
                // Front matter so the cached copy carries its own provenance —
                // an undated copy of a page with no link back is a copy an
                // agent should not trust.
                var body = $"<!-- fetched by Perch from {res.FinalUrl} on {DateTime.UtcNow:yyyy-MM-dd} -->\n\n"
                         + (string.IsNullOrWhiteSpace(title) ? "" : $"# {title}\n\n")
                         + $"Source: {res.FinalUrl}\n\n---\n\n{markdown}\n";
                try { AtomicFile.WriteAllText(abs, body); }
                catch (Exception ex)
                {
                    Log.Error("Board.writeRef", ex);
                    Failed?.Invoke(paneId, "Couldn't save the fetched page.");
                    return false;
                }

                node.Ref = RelFromRepo(store, "refs/" + name);
                node.FetchedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd");
                Log.Info("Board.fetch.ok", $"pane={paneId:N} {res.FinalUrl} -> {name} ({markdown.Length} chars)");
                return true;
            });
        });
    }

    /// An unused "<slug>.md" inside the board's refs/.
    private static string NextRefName(BoardStore store, string titleOrUrl)
    {
        var slug = GitProc.Slugify(titleOrUrl);
        if (slug.Length == 0) slug = "reference";
        if (!System.IO.File.Exists(System.IO.Path.Combine(store.RefsDir, slug + ".md")))
            return slug + ".md";
        for (var i = 2; i < 1000; i++)
        {
            var name = $"{slug}-{i}.md";
            if (!System.IO.File.Exists(System.IO.Path.Combine(store.RefsDir, name))) return name;
        }
        return $"{slug}-{Guid.NewGuid():N}.md";
    }

    /// Paste whatever is on the clipboard onto the board.
    ///
    /// `bitmap` and `text` are read by the CALLER on the UI thread (the
    /// clipboard is STA-only) and handed in, so this stays testable and the
    /// threading requirement lives in one place. An image wins over text: when
    /// you copy a screenshot, Windows often also puts a path or some HTML on
    /// the clipboard, and the picture is what you meant.
    ///
    /// Deliberately NO bytes cross the page↔host bridge for this. The page
    /// sends "a paste happened here" and the host reads the clipboard itself —
    /// a multi-megabyte base64 dataURL over PostWebMessage is several transient
    /// copies of itself in a process that has already died of OOM once.
    public void OnPaste(Guid paneId, byte[]? pngBytes, string? text, double x, double y)
    {
        if (pngBytes != null && pngBytes.Length > 0)
        {
            Mutate(paneId, store =>
            {
                var name = NextAssetName(store, "pasted");
                var abs = System.IO.Path.Combine(store.AssetsDir, name);
                try { AtomicFile.WriteAllBytes(abs, pngBytes); }
                catch (Exception ex)
                {
                    Log.Error("Board.writeAsset", ex);
                    Failed?.Invoke(paneId, "Couldn't save the pasted image.");
                    return false;
                }
                store.Doc.Nodes.Add(new BoardNode
                {
                    Id = store.Doc.NextId(),
                    Kind = "image",
                    // Repo-relative, like every other ref on a board: the
                    // agent's cwd is the repo, not the board folder.
                    Ref = RelFromRepo(store, "assets/" + name),
                    X = x, Y = y,
                });
                Log.Info("Board.paste.image", $"pane={paneId:N} bytes={pngBytes.Length} -> {name}");
                return true;
            });
            return;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            // A paste is a person pressing Ctrl+V on a canvas they are looking
            // at — the one gesture in this file that is unambiguously human. It
            // is therefore the only path allowed to stage a file from outside
            // the project; see BoardAddMsg.Origin for why that distinction is
            // where the security boundary belongs.
            OnAdd(new BoardAddMsg
            {
                PaneId = paneId, Kind = "auto", Text = text, X = x, Y = y,
                Origin = "user",
            });
            return;
        }

        Failed?.Invoke(paneId, "There's nothing on the clipboard to add.");
    }

    /// An unused "<stem>-N.png" inside the board's assets/.
    private static string NextAssetName(BoardStore store, string stem)
    {
        for (var i = 1; i < 10000; i++)
        {
            var name = $"{stem}-{i}.png";
            if (!System.IO.File.Exists(System.IO.Path.Combine(store.AssetsDir, name))) return name;
        }
        return $"{stem}-{Guid.NewGuid():N}.png";
    }

    /// Turn a board-relative path ("assets/x.png") into a repo-relative one.
    private static string RelFromRepo(BoardStore store, string boardRelative)
    {
        var abs = System.IO.Path.Combine(store.Dir, boardRelative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var repo = RepoRootOf(store.Dir);
        var rel = repo == null ? null : BoardStore.ToRepoRelative(repo, abs);
        // Falling back to the board-relative form keeps the node usable even if
        // the board somehow isn't under a repo root.
        return rel ?? boardRelative;
    }

    /// A preview of an image node, sized for the card. Answers with base64 JPEG
    /// rather than a file:// URL because the page is served from a virtual host
    /// and cannot read the user's disk — and mapping the repo into the page's
    /// origin to make it could would be a much bigger door to open.
    public void OnImage(BoardNodeRefMsg msg)
    {
        var sess = _sessionOfPane(msg.PaneId);
        if (sess == null) return;
        var store = StoreFor(sess);
        var node = store?.Doc.Find(msg.NodeId);
        if (store == null || node == null || string.IsNullOrEmpty(node.Ref)) return;

        var abs = System.IO.Path.Combine(store.AssetsDir, System.IO.Path.GetFileName(node.Ref!));
        var data = ImageThumb.JpegBase64FromFile(abs, 640);
        // Null (missing or undecodable) is reported too: the card then shows
        // that its picture is gone rather than an empty frame.
        ImageReady?.Invoke(msg.PaneId, msg.NodeId, data);
    }

    /// Preview bytes for an image node, or null when it can't be read.
    public event Action<Guid, string, string?>? ImageReady;

    private static string? RepoRootOf(string boardDir)
    {
        try
        {
            var boards = System.IO.Path.GetDirectoryName(boardDir);
            var perch = boards == null ? null : System.IO.Path.GetDirectoryName(boards);
            return perch == null ? null : System.IO.Path.GetDirectoryName(perch);
        }
        catch { return null; }
    }

    /// The repo the pane's board lives in — the board dir is
    /// &lt;repo&gt;/.perch/boards/&lt;slug&gt;, so the root is three levels up.
    private string? RepoRootFor(Guid paneId)
    {
        var sess = _sessionOfPane(paneId);
        if (sess == null || string.IsNullOrEmpty(sess.BoardPath)) return null;
        return RepoRootOf(sess.BoardPath);
    }

    /// Every pane in `sess` that is a board leaf. Used to push state to all of
    /// them after a mutation — a tab can have more than one window onto the
    /// same board, and they must not disagree.
    public static IEnumerable<Guid> BoardPanes(Session sess) =>
        PaneTree.AllLeaves(sess.Root).Where(p => p.IsBoard).Select(p => p.Id);
}
