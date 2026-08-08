using System;
using System.IO;
using System.Linq;

namespace Perch;

/// Deciding whether a piece of text was even MEANT to be a file path.
///
/// The board's staging used to skip this question entirely: text that wasn't a
/// note and wasn't a URL was assumed to be a path, handed to the containment
/// check, and reported back with a message written for paths. Staging the shell
/// command "! git reset --hard origin/main" produced "That path is outside this
/// project, so the agent couldn't open it" — a scope error for something that
/// was never a path, which reads as a permissions problem and sends you looking
/// in the wrong place.
///
/// The test is intentionally shape-only. Whether the file EXISTS is a separate
/// question with a separate message ("No such file"), and whether it is inside
/// the project is a third one. Collapsing them is what produced the confusing
/// error in the first place.
internal static class BoardPaths
{
    /// Characters Windows forbids in a filename. A string containing one was
    /// never a path, whatever else it might be.
    private static readonly char[] Illegal = { '<', '>', '"', '|', '?', '*' };

    /// Does this text have the SHAPE of a file path? Deliberately permissive
    /// about what a path can look like and strict about what it can't:
    /// prose, shell commands, and multi-line text are rejected; anything that
    /// plausibly names a file is passed on to the checks that can answer
    /// definitively.
    public static bool LooksLikeAPath(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();

        // A path is one line. Multi-line text is a note.
        if (t.IndexOf('\n') >= 0 || t.IndexOf('\r') >= 0) return false;

        // Long enough to be prose is prose. (MAX_PATH is 260; extended-length
        // paths exist but nobody pastes one onto a board.)
        if (t.Length > 260) return false;

        if (t.IndexOfAny(Illegal) >= 0) return false;
        if (t.Any(char.IsControl)) return false;

        // A leading shell sigil is the tell that made this function necessary.
        if (t[0] is '!' or '$' or '#' or '>' or '|' or '&' or '`') return false;

        // Reject anything with an argument-looking token: "git reset --hard x"
        // has spaces AND a flag, which no filename someone stages does.
        var toks = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (toks.Length > 1 && toks.Skip(1).Any(x => x.StartsWith('-'))) return false;

        // Must name something. An extension is the strong signal — a staged
        // file essentially always has one, and it permits spaces ("my notes.md",
        // "C:\Program Files\x.md"). Failing that, a separator with no spaces at
        // all is still a plausible path ("src/bin/tool").
        //
        // The spaces clause is what rejects prose containing a slash: "this
        // and/or that" has a separator but no extension and does have spaces,
        // which no staged filename combination does.
        var hasSep = t.Contains('/') || t.Contains('\\');
        var hasExt = Path.GetExtension(t).Length is > 1 and <= 12;
        if (!hasExt && !(hasSep && !t.Contains(' '))) return false;

        return true;
    }

    /// Resolve staged text to an absolute path, relative to the pane's repo
    /// when it isn't already rooted. Null when it can't be resolved — a
    /// malformed path must fail here rather than throwing deeper in.
    public static string? TryAbsolute(string text, string? repoRoot)
    {
        try
        {
            var t = text.Trim().Trim('"');
            if (Path.IsPathRooted(t)) return Path.GetFullPath(t);
            if (string.IsNullOrEmpty(repoRoot)) return null;
            return Path.GetFullPath(Path.Combine(repoRoot, t));
        }
        catch { return null; }
    }
}
