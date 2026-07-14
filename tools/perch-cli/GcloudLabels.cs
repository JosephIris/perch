using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace PerchCli;

/// Rewrites `gcloud ... create` commands to carry agent-attribution labels, so a
/// VM or Dataproc cluster records WHO made it on the resource itself rather than
/// only in a file on this machine. A local ledger dies on reboot; a GCP label
/// survives, and it lets the poller filter server-side instead of listing every
/// instance in the project.
///
/// Deliberately tool-neutral (`agent-*`, not `perch-*`): any agent harness can
/// stamp the same convention and Perch will read it.
///
/// Labels carry only the JOIN KEYS. GCP label values are lowercase
/// [a-z0-9_-], max 63 chars — so the pane's name and the prompt that caused the
/// resource cannot live here. Those go in the host-written ledger, keyed by
/// agent-session.
public static class GcloudLabels
{
    /// Matches the two resource kinds that bill by the hour and get abandoned.
    /// `alpha`/`beta` surfaces are accepted because they're the same command.
    private static readonly Regex CreateRe = new(
        @"\bgcloud\s+(?:alpha\s+|beta\s+)?(?:(compute)\s+instances|(dataproc)\s+clusters)\s+create\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// An existing --labels flag, in either `--labels=a=b` or `--labels a=b` form.
    private static readonly Regex LabelsRe = new(
        @"--labels(?:=|\s+)(?<val>""[^""]*""|'[^']*'|[^\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public enum Kind { None, Instance, Cluster }

    /// What kind of billable resource, if any, this command creates.
    public static Kind Detect(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return Kind.None;
        var m = CreateRe.Match(command);
        if (!m.Success) return Kind.None;
        return m.Groups[1].Success ? Kind.Instance : Kind.Cluster;
    }

    /// Coerce to the GCP label charset: lowercase letters, digits, `-`, `_`,
    /// max 63 chars. Invalid input is coerced or dropped rather than passed
    /// through — a malformed label makes the entire `create` call fail, and
    /// breaking the agent's actual work to satisfy our bookkeeping would be a
    /// far worse outcome than a missing label.
    private static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new StringBuilder(raw!.Length);
        foreach (var ch in raw.ToLowerInvariant())
        {
            if (ch < 128 && char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is '-' or '_') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        if (s.Length > 63) s = s.Substring(0, 63).TrimEnd('-');
        return s;
    }

    /// Label KEYS must additionally begin with a lowercase letter.
    public static string SanitizeKey(string? raw)
    {
        var s = Clean(raw);
        while (s.Length > 0 && !char.IsLetter(s[0])) s = s.Substring(1);
        return s;
    }

    /// Label VALUES have no leading-character rule — only the charset and the
    /// length cap. Keeping this distinct from SanitizeKey matters: a Claude
    /// session id is a UUID that frequently starts with a digit, and stripping
    /// that digit would silently corrupt the join key we use to decide whether
    /// a running machine still belongs to a live pane.
    public static string SanitizeValue(string? raw) => Clean(raw);

    /// Appends our labels to `command`, merging with any --labels the caller
    /// already wrote. Returns the original string unchanged when there's nothing
    /// to stamp, so callers can cheaply detect "no rewrite needed".
    ///
    /// The flag is inserted immediately after the `create` verb rather than at
    /// the end of the command. gcloud accepts flags in any position, and
    /// appending at the end is actively unsafe: in
    /// `gcloud compute instances create vm && echo done` the flag would land on
    /// `echo`. Inserting at `create` sidesteps shell operators, line
    /// continuations and trailing redirections entirely.
    public static string Stamp(string command, IReadOnlyList<KeyValuePair<string, string>> labels)
    {
        if (string.IsNullOrEmpty(command) || labels.Count == 0) return command;
        var m = CreateRe.Match(command);
        if (!m.Success) return command;

        var pairs = new List<string>();
        foreach (var kv in labels)
        {
            var k = SanitizeKey(kv.Key);
            var v = SanitizeValue(kv.Value);
            if (k.Length > 0 && v.Length > 0) pairs.Add($"{k}={v}");
        }
        if (pairs.Count == 0) return command;
        var ours = string.Join(",", pairs);

        // Only look for an existing --labels inside THIS gcloud invocation, not
        // in some later command chained after `&&`.
        var segEnd = SegmentEnd(command, m.Index);
        var existing = LabelsRe.Match(command, m.Index, segEnd - m.Index);
        if (existing.Success)
        {
            // Merge into the caller's flag. Their values win on key collision
            // (we append after), which keeps an explicit user label authoritative.
            var g = existing.Groups["val"];
            var raw = g.Value;
            var quote = raw.Length > 1 && (raw[0] == '"' || raw[0] == '\'') ? raw[0] : '\0';
            var inner = quote == '\0' ? raw : raw.Substring(1, raw.Length - 2);
            var merged = inner.Length == 0 ? ours : $"{inner},{ours}";
            var replacement = quote == '\0' ? merged : $"{quote}{merged}{quote}";
            return command.Substring(0, g.Index) + replacement + command.Substring(g.Index + g.Length);
        }

        // No existing flag — insert right after `create`.
        var insertAt = m.Index + m.Length;
        return command.Substring(0, insertAt) + $" --labels={ours}" + command.Substring(insertAt);
    }

    /// Index just past the end of the shell "command segment" starting at
    /// `start` — i.e. the first unquoted `&&`, `||`, `;`, `|`, `&`, newline or
    /// redirection. A backslash-newline is a line continuation, not a break.
    /// Used only to scope the --labels search; the insert point doesn't need it.
    private static int SegmentEnd(string s, int start)
    {
        char quote = '\0';
        for (int i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length) { i++; continue; }   // escaped char / line continuation
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c is ';' or '|' or '&' or '\n' or '\r' or '>' or '<') return i;
        }
        return s.Length;
    }
}
