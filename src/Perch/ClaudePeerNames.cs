using System;
using System.IO;
using System.Text;

namespace Perch;

/// Per-pane cross-session names, persisted to the same tiny temp-file channel
/// as ClaudeModelState: the host writes %TEMP%\perch-claude-name-&lt;paneId&gt;.txt
/// whenever the assigned name changes, and the `wrap-claude` shim re-reads it
/// at every `claude` launch, appending `--name &lt;value&gt;`.
///
/// Why names at all: cross-session messaging addresses sessions BY NAME.
/// Left to itself cc derives one from the working directory — which collides
/// the moment two tabs share a repo (the normal Perch case). Keeping the name
/// derived from the tab title means `/list-agents` inside any session shows
/// names the user recognises from the sidebar, and "tell weekly-digest about
/// the rename" needs no lookup. The host dedupes across live panes ("-2",
/// "-3") so a shared title can't make the address ambiguous.
///
/// ## One spelling, everywhere
///
/// A name is always the SLUG of the title (lowercase, dashes): "Loc diff fix"
/// → `loc-diff-fix`. That is what a project tab passes on its command line
/// (a slug needs no quoting through the shell splice), what the sweep writes
/// to the temp file, and what a teammate types into SendMessage. The earlier
/// scheme kept spaces and case in the host's copy while the launch used the
/// slug, so an observed `peer.msg` target never matched a row and pair notes
/// were silently dropped. `Matches` compares through the slug so any legacy
/// spelling still resolves.
internal static class ClaudePeerNames
{
    /// The path wrap-claude and the session-start hook read. Keyed by the pane
    /// id in "N" format to match PERCH_PANE_ID.
    public static string PathFor(Guid paneId)
        => Path.Combine(Path.GetTempPath(), $"perch-claude-name-{paneId:N}.txt");

    /// Write the name, or clear the file for empty → cc auto-names from cwd.
    /// Never throws — a peer name must never break a pane.
    public static void Write(Guid paneId, string? name)
    {
        try
        {
            var path = PathFor(paneId);
            var n = (name ?? "").Trim();
            if (n.Length == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            File.WriteAllText(path, n);
        }
        catch (Exception ex) { Log.Info("ClaudePeerNames.Write", $"skipped: {ex.Message}"); }
    }

    /// The session name a tab title launches under: its slug, or "tab" when
    /// the title has no alphanumerics at all (never an empty --name).
    public static string ForTitle(string? title)
    {
        var slug = GitProc.Slugify(title ?? "");
        return slug.Length == 0 ? "tab" : slug;
    }

    /// Do two spellings name the same session? Compared through the slug, so
    /// "Loc diff fix", "loc-diff-fix" and "LOC DIFF FIX" all agree, while
    /// empty never matches anything.
    public static bool Matches(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var sa = GitProc.Slugify(a);
        var sb = GitProc.Slugify(b);
        return sa.Length > 0 && string.Equals(sa, sb, StringComparison.Ordinal);
    }

    /// A tab title reduced to a safe, readable display form: control chars and
    /// quotes dropped, whitespace collapsed, capped at 40 chars. Empty in →
    /// "tab" out. Used for prose ("paired with …"), never for addresses.
    public static string Sanitize(string? title)
    {
        var sb = new StringBuilder();
        var lastSpace = true;
        foreach (var c in (title ?? "").Trim())
        {
            // Whitespace BEFORE the control check: a tab is both, and it
            // should collapse to a space, not vanish and weld its neighbors.
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace) { sb.Append(' '); lastSpace = true; }
                continue;
            }
            if (char.IsControl(c) || c is '"' or '\'') continue;
            sb.Append(c);
            lastSpace = false;
            if (sb.Length >= 40) break;
        }
        var s = sb.ToString().TrimEnd();
        return s.Length == 0 ? "tab" : s;
    }
}
