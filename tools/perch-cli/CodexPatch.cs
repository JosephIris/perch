using System;
using System.Collections.Generic;

namespace PerchCli;

/// Reading codex's patch format.
///
/// Codex edits files with one tool, `apply_patch`, whose entire input is a
/// script like:
///
///     *** Begin Patch
///     *** Update File: C:\tmp\notes.md
///     @@
///     +line two
///     *** End Patch
///
/// The header lines are the ONLY place a codex edit names its files, so this
/// parse feeds both halves of what Perch shows about an edit: the activity line
/// ("editing notes.md") and the per-pane claim that splits a shared working
/// tree's line counts between the agents editing it.
///
/// Lives in its own file — like PeerVerdict — so Perch.csproj can link it and
/// the tests can pin it against a real captured payload.
internal static class CodexPatch
{
    /// Every file the patch touches, in order, without duplicates. Paths are
    /// returned exactly as codex wrote them (absolute, in practice), because a
    /// claim that has been "normalised" no longer matches what git reports.
    public static List<string> Files(string? patch)
    {
        var files = new List<string>();
        if (string.IsNullOrEmpty(patch)) return files;
        foreach (var raw in patch!.Split('\n'))
        {
            // The marker is the LINE's own prefix. A patch that adds a line
            // mentioning "*** Add File:" must not have that content read as a
            // header, which is why this is a StartsWith and not a Contains.
            var line = raw.TrimEnd('\r').Trim();
            if (!line.StartsWith("*** ", StringComparison.Ordinal)) continue;
            foreach (var verb in Verbs)
            {
                if (!line.StartsWith("*** " + verb, StringComparison.Ordinal)) continue;
                var path = line.Substring(4 + verb.Length).Trim();
                if (path.Length > 0 && !files.Contains(path, StringComparer.OrdinalIgnoreCase))
                    files.Add(path);
                break;
            }
        }
        return files;
    }

    /// "Move to:" is the rename half of an update — the destination is the file
    /// that ends up changed, so it is claimed like the rest.
    private static readonly string[] Verbs =
        { "Add File:", "Update File:", "Delete File:", "Move to:" };
}
