using System;
using System.Text.RegularExpressions;

namespace CmuxWin;

internal static class UrlScanner
{
    // Standard http(s)/ftp URL. Conservative: stops at whitespace and common
    // bracketing chars so a selection like "(https://example.com)" with the
    // parens caught still resolves to the URL.
    private static readonly Regex UrlRegex = new(
        @"\b(?:https?|ftp)://[^\s<>""'`()\[\]{}]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly char[] TrailingPunctuation = { '.', ',', ';', ':', '!', '?' };

    /// Returns the URL inside <paramref name="selection"/> if the selection
    /// contains exactly one and nothing else of substance, else null.
    public static string? TryGetUrl(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection)) return null;
        var trimmed = selection.Trim();
        var m = UrlRegex.Match(trimmed);
        if (!m.Success) return null;
        // Require the URL to span the bulk of the selection so a giant log
        // line containing one URL doesn't pop the menu on every right-click.
        if (m.Length < trimmed.Length - 4) return null;
        return m.Value.TrimEnd(TrailingPunctuation);
    }

    /// Pulls every URL out of a body of text (e.g. the console buffer),
    /// preserving first-seen order, deduplicating. Used by the Ctrl+Shift+U
    /// URL palette.
    public static System.Collections.Generic.List<string> AllUrls(string? text)
    {
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        var ordered = new System.Collections.Generic.List<string>();
        if (string.IsNullOrEmpty(text)) return ordered;
        foreach (Match m in UrlRegex.Matches(text))
        {
            var url = m.Value.TrimEnd(TrailingPunctuation);
            if (url.Length == 0) continue;
            if (seen.Add(url)) ordered.Add(url);
        }
        return ordered;
    }
}
