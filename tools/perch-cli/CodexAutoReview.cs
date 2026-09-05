using System.Text;

namespace PerchCli;

/// Whether a codex thread hands its approvals to codex's own reviewer model
/// instead of the person at the keyboard.
///
/// Codex resolves an approval in a fixed order: hooks first, then — when the
/// thread runs `approval_policy = "on-request"` with `approvals_reviewer =
/// "auto_review"` — its automatic reviewer, and only otherwise the user. So
/// our PermissionRequest hook fires for every escalation, including the ones
/// the reviewer is about to answer on its own a second later. Treating those
/// as "Codex needs you" put a red row and a warn note on a pane that was
/// never waiting on anyone (the note then outlived the moment, because no
/// dialog ever appeared for the on-screen probe to see close).
///
/// The hook payload doesn't say which reviewer is next, but the thread's
/// journal does: every `turn_context` record (one per turn) and every
/// `thread_settings_applied` event carries the effective `approval_policy`
/// and `approvals_reviewer`. The last one written is the one in force.
internal static class CodexAutoReview
{
    /// True when the reviewer model, not the user, will answer this thread's
    /// next approval. False when the journal can't be read or never says.
    public static bool IsOn(string? transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath)) return false;
        try
        {
            using var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            // Read from the tail: the setting is restated at every turn, so the
            // answer is almost always in the last chunk. A record split across
            // two chunks is carried into the next round whole.
            const int Chunk = 256 * 1024;
            var end = fs.Length;
            var carry = "";
            while (end > 0)
            {
                var start = Math.Max(0, end - Chunk);
                var buf = new byte[end - start];
                fs.Seek(start, SeekOrigin.Begin);
                fs.ReadExactly(buf);
                var text = Encoding.UTF8.GetString(buf) + carry;
                var verdict = Detect(text);
                if (verdict != null) return verdict.Value;
                var nl = text.IndexOf('\n');
                carry = nl < 0 ? text : text[..nl];
                end = start;
            }
        }
        catch { /* unreadable journal → assume a person answers */ }
        return false;
    }

    /// The pure decision: reads the LAST record in `text` that states a
    /// reviewer. Null when no record does (caller keeps looking further back).
    public static bool? Detect(string text)
    {
        const string key = "\"approvals_reviewer\":";
        var at = text.LastIndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;
        var lineStart = text.LastIndexOf('\n', at) + 1;
        var lineEnd = text.IndexOf('\n', at);
        if (lineEnd < 0) lineEnd = text.Length;
        var line = text[lineStart..lineEnd];
        if (Field(line, "approvals_reviewer") != "auto_review") return false;
        // codex routes to the reviewer only under on-request (or a granular
        // policy, serialized as an object — Field reads null for it). The
        // other policies keep asking the person even with the reviewer set.
        var policy = Field(line, "approval_policy");
        return policy is null or "on-request";
    }

    /// The string value of `"name":"value"` in one JSON line, or null when the
    /// field is absent or not a string.
    private static string? Field(string line, string name)
    {
        var k = "\"" + name + "\":";
        var i = line.IndexOf(k, StringComparison.Ordinal);
        if (i < 0) return null;
        i += k.Length;
        if (i >= line.Length || line[i] != '"') return null;
        var close = line.IndexOf('"', i + 1);
        return close < 0 ? null : line[(i + 1)..close];
    }
}
