using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perch;

/// The data model for a board — the context staging surface described in
/// docs/BOARDS.md. A board is a FOLDER, not a file:
///
///     &lt;repo&gt;/.perch/boards/&lt;slug&gt;/
///       board.md                     the index an agent reads
///       assets/login-broken.png      pasted images
///       refs/oauth-browser-apps.md   fetched pages
///
/// Everything the user throws at a board is resolved into one of these nodes,
/// and every node that has a file behind it stores that file's path
/// REPO-RELATIVE. That is the whole point: an agent whose cwd is the repo can
/// open any of it directly, with no fetch tool and no absolute paths that only
/// make sense on this machine.
internal enum BoardNodeKind
{
    /// Free text the user typed. No artifact on disk.
    Note,
    /// A reference to a file that already exists in the repo. NOT copied — the
    /// board stores the path so the agent reads the live file.
    Path,
    /// An image pasted from the clipboard, written into assets/.
    Image,
    /// A web page fetched and cached into refs/, so the agent can read it with
    /// no network access.
    Url,
}

internal sealed class BoardNode
{
    /// Stable within one board; referenced by links. Not a Guid — these end up
    /// in a file a human may read, and "n3" is kinder than a Guid.
    public string Id { get; set; } = "";

    /// Lowercase BoardNodeKind. A string on the wire and on disk so an unknown
    /// kind from a newer version degrades to "skip it" instead of throwing.
    public string Kind { get; set; } = "note";

    /// Repo-relative path to this node's artifact. Null for notes.
    public string? Ref { get; set; }

    /// Absolute path to a file the user deliberately staged from OUTSIDE this
    /// project. Separate from Ref rather than folded into it, for two reasons.
    ///
    /// Portability: Ref is the repo-relative list that survives a board being
    /// shared, committed, or opened on another machine. An absolute path is
    /// meaningful on exactly one box, so mixing it into Ref would quietly
    /// break that guarantee for every consumer.
    ///
    /// Legibility: "which files here are outside the project" is a question the
    /// UI, board.md, and any future review step all need to answer, and a
    /// separate field answers it by construction instead of by re-testing every
    /// path for containment.
    public string? ExtRef { get; set; }

    /// Note body, or the one-line caption shown under a file/image/reference.
    public string Text { get; set; } = "";

    /// For Url nodes: where it came from, and when we fetched it. Both are
    /// written into board.md, because a cached page with no provenance and no
    /// date is a page an agent should not trust.
    public string? Source { get; set; }
    public string? FetchedUtc { get; set; }

    /// Position on the canvas. Carried in the layout block only — the prose
    /// body has no geometry in it.
    public double X { get; set; }
    public double Y { get; set; }

    /// Card size. 0 means "use the default for this kind", which is what an
    /// un-resized node stores — so the default can be retuned later without
    /// rewriting every board, and a board written before resizing existed
    /// still opens correctly.
    public double W { get; set; }
    public double H { get; set; }
}

/// A relationship the user drew between two nodes. Deliberately weak: this is
/// "these go together", not a dependency. One line each under "## Related".
internal sealed class BoardLink
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Label { get; set; } = "";
}

/// The authoritative content of a board, serialized into the trailing
/// `perch:layout` comment in board.md.
internal sealed class BoardDoc
{
    public int V { get; set; } = 1;
    public string Title { get; set; } = "";
    public List<BoardNode> Nodes { get; set; } = new();
    public List<BoardLink> Links { get; set; } = new();

    public BoardNode? Find(string id) => Nodes.Find(n => n.Id == id);

    /// Next free "nN" id. Scans rather than counting so that removing a node
    /// and adding another can't collide with a surviving id.
    public string NextId()
    {
        var max = 0;
        foreach (var n in Nodes)
        {
            if (n.Id.Length > 1 && n.Id[0] == 'n' && int.TryParse(n.Id[1..], out var v) && v > max)
                max = v;
        }
        return "n" + (max + 1);
    }
}

[JsonSerializable(typeof(BoardDoc))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class BoardJsonContext : JsonSerializerContext { }
