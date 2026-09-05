using System;

namespace PerchCli;

/// <summary>
/// Did a cross-session SendMessage actually deliver?
///
/// Shared with the host (linked into Perch.csproj, the GcloudLabels pattern)
/// so the room's "a bot messaged a bot" row and the hook agree, and so this
/// can be unit-tested against real responses.
///
/// cc answers a SendMessage with a JSON object carrying an explicit
/// `success` boolean:
///
///   {"success":true,"message":"“…” → joe (another Claude session…)","msg_id":"…"}
///   {"success":false,"message":"2 agents are named 'shabtay'. Re-send with the ref…"}
///   {"success":false,"message":"No agent named 'joe [4910a2]' is reachable."}
///
/// That boolean is the answer whenever it is present — the older prose sniff
/// read "No agent named 'x' is reachable" as a SUCCESS (none of its markers
/// appear in that sentence), which is how a bot's failed attempt and its
/// retry both landed in the room as delivered messages, reading as duplicates.
/// The sniff stays only as the fallback for a response with no flag at all,
/// and is biased toward true: a wrong "failed" would put a scary note on a
/// healthy pair.
/// </summary>
internal static class PeerVerdict
{
    /// The raw `tool_response` text of a SendMessage PostToolUse hook — a JSON
    /// object, or that object as an escaped JSON string, or plain prose.
    internal static bool Ok(string? rawToolResponse)
    {
        var raw = rawToolResponse ?? "";
        if (raw.Length == 0) return true;
        if (SuccessFlag(raw) is bool flag) return flag;
        var lower = raw.ToLowerInvariant();
        return !(lower.Contains("failed") || lower.Contains("not found")
                 || lower.Contains("no session") || lower.Contains("unable to")
                 || lower.Contains("no agent named") || lower.Contains("is not reachable")
                 || lower.Contains("agents are named") || lower.Contains("re-send with")
                 || lower.Contains("\"is_error\":true"));
    }

    /// Why a send failed, in one short line for the room: cc's own `message`
    /// field when the response carries one, else a cut of the raw text. Only
    /// the first sentence — the full answer walks the sender through refs and
    /// ListAgents, which is the bot's problem, not the owner's.
    internal static string Reason(string? rawToolResponse, int max = 160)
    {
        var raw = (rawToolResponse ?? "").Trim();
        if (raw.Length == 0) return "";
        var text = MessageField(raw) ?? raw;
        text = text.Replace("\\n", " ").Replace("\\\"", "\"").Replace("\\r", " ");
        text = text.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ");
        var stop = text.IndexOf(". ", StringComparison.Ordinal);
        if (stop > 20) text = text[..(stop + 1)];
        if (text.Length > max) text = text[..max].TrimEnd() + "…";
        return text.Trim('"', ' ');
    }

    /// The value of the response's `message` field, escaped or not; null when
    /// there is none.
    private static string? MessageField(string raw)
    {
        foreach (var key in new[] { "\"message\":", "\\\"message\\\":" })
        {
            var i = raw.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) continue;
            var j = i + key.Length;
            while (j < raw.Length && (raw[j] == ' ' || raw[j] == '\\')) j++;
            if (j >= raw.Length || raw[j] != '"') continue;
            j++;
            var sb = new System.Text.StringBuilder();
            while (j < raw.Length)
            {
                var c = raw[j];
                if (c == '\\' && j + 1 < raw.Length)
                {
                    var n = raw[j + 1];
                    if (n == '"') { j += 2; break; }              // \" closes an escaped string
                    if (n == '\\' && j + 2 < raw.Length && raw[j + 2] == '"') { sb.Append('"'); j += 3; continue; }
                    if (n == 'n' || n == 'r' || n == 't') { sb.Append(' '); j += 2; continue; }
                    sb.Append(n); j += 2; continue;
                }
                if (c == '"') break;
                sb.Append(c); j++;
            }
            var s = sb.ToString().Trim();
            if (s.Length > 0) return s;
        }
        return null;
    }

    /// The first `"success": true|false` in the text, reading through JSON
    /// string escaping (`\"success\":true`). Null when the response carries no
    /// such flag.
    private static bool? SuccessFlag(string raw)
    {
        var i = raw.IndexOf("success", StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            var j = i + "success".Length;
            // …success" : true  /  …success\" : false
            while (j < raw.Length && (raw[j] == '"' || raw[j] == '\\' || raw[j] == ':' || raw[j] == ' ' || raw[j] == '\t')) j++;
            if (j < raw.Length)
            {
                if (string.CompareOrdinal(raw, j, "true", 0, 4) == 0) return true;
                if (string.CompareOrdinal(raw, j, "false", 0, 5) == 0) return false;
            }
            i = raw.IndexOf("success", i + 1, StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }
}
