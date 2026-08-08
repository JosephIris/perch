using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Perch;

/// Turn a fetched web page into markdown an agent can read.
///
/// ## Why this is hand-rolled
///
/// Perch has four NuGet references and ships as a Store-signed MSIX. Adding an
/// HTML parser plus its transitive dependencies to that supply chain — and to
/// THIRD-PARTY-NOTICES.md — is a real cost for one feature, and the job here is
/// narrow: docs pages, RFCs, blog posts. We are not rendering the web.
///
/// ## What it deliberately is not
///
/// Not a parser. It is a tag-aware scanner: strip the parts that are never
/// content, prefer &lt;main&gt;/&lt;article&gt; when the page offers one, then walk the
/// rest emitting markdown for the dozen tags that carry meaning. Malformed HTML
/// degrades to slightly worse text rather than throwing, which is the right
/// trade when the input is the open internet.
///
/// ## The honesty rule
///
/// Extraction can fail quietly — a JS-rendered page yields a shell with no
/// prose, and the result looks like a successful fetch of an empty document.
/// LooksThin() exists so the caller can SAY that happened instead of writing a
/// confident-looking empty file into the board.
internal static class HtmlToMarkdown
{
    /// Blocks whose contents are never page content. Removed whole, with their
    /// markup, before anything else runs.
    private static readonly string[] DropBlocks =
        { "script", "style", "svg", "noscript", "iframe", "head", "nav", "footer", "form", "template" };

    /// A page shorter than this almost certainly didn't extract — a real doc
    /// page is thousands of characters. Used by LooksThin.
    private const int ThinThreshold = 240;

    /// Convert `html` to markdown. `baseUri` resolves relative links; pass null
    /// to leave them as-is.
    public static string Convert(string html, Uri? baseUri = null)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var s = StripComments(html);
        foreach (var tag in DropBlocks) s = StripBlock(s, tag);

        // Prefer the page's own "this is the content" marker when it has one.
        // On a docs site this is the difference between the article and the
        // article wrapped in three columns of navigation.
        s = NarrowToMain(s);

        var sb = new StringBuilder(s.Length / 2);
        Walk(s, sb, baseUri);
        return Tidy(sb.ToString());
    }

    /// The page's &lt;title&gt;, or "". Read from the ORIGINAL html, because
    /// Convert strips &lt;head&gt; before it gets there.
    public static string ExtractTitle(string html)
    {
        var m = Regex.Match(html ?? "", @"<title[^>]*>(.*?)</title\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? Tidy(DecodeEntities(m.Groups[1].Value)).Trim() : "";
    }

    /// True when the conversion produced too little to be a real page — a
    /// JS-rendered shell, a consent wall, a 200 that was actually an error.
    /// The caller reports this rather than pretending the fetch worked.
    public static bool LooksThin(string markdown) =>
        (markdown ?? "").Trim().Length < ThinThreshold;

    // ---- stripping ---------------------------------------------------------

    private static string StripComments(string s) =>
        Regex.Replace(s, "<!--.*?-->", " ", RegexOptions.Singleline);

    /// Remove `<tag ...> ... </tag>` including the markup. Non-greedy so two
    /// sibling blocks don't collapse into one giant match; RegexOptions.Singleline
    /// so a block spanning lines still matches.
    private static string StripBlock(string s, string tag) =>
        Regex.Replace(s, $@"<{tag}\b[^>]*>.*?</{tag}\s*>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// Narrow to <main> or <article> when present and substantial. The size
    /// guard matters: some pages have an empty <main> and the real content
    /// elsewhere, and narrowing into that would throw the page away.
    private static string NarrowToMain(string s)
    {
        foreach (var tag in new[] { "main", "article" })
        {
            var m = Regex.Match(s, $@"<{tag}\b[^>]*>(.*?)</{tag}\s*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (m.Success && m.Groups[1].Value.Length > s.Length / 8)
                return m.Groups[1].Value;
        }
        return s;
    }

    // ---- the walk ----------------------------------------------------------

    private static void Walk(string s, StringBuilder sb, Uri? baseUri)
    {
        var listDepth = 0;
        var inPre = false;
        string? linkHref = null;
        var linkText = new StringBuilder();

        void Emit(string text)
        {
            if (linkHref != null) linkText.Append(text);
            else sb.Append(text);
        }

        var i = 0;
        while (i < s.Length)
        {
            var lt = s.IndexOf('<', i);
            if (lt < 0) { Emit(DecodeEntities(s[i..])); break; }
            if (lt > i) Emit(DecodeEntities(s[i..lt]));

            var gt = s.IndexOf('>', lt);
            if (gt < 0) break;                       // truncated tag; stop cleanly
            var raw = s[(lt + 1)..gt];
            i = gt + 1;

            var closing = raw.StartsWith("/");
            var name = TagName(closing ? raw[1..] : raw);
            if (name.Length == 0) continue;

            switch (name)
            {
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                    sb.Append(closing ? "\n\n" : "\n\n" + new string('#', name[1] - '0') + " ");
                    break;
                case "p": case "div": case "section": case "blockquote":
                    sb.Append("\n\n");
                    if (!closing && name == "blockquote") sb.Append("> ");
                    break;
                case "br":
                    sb.Append("\n");
                    break;
                case "hr":
                    sb.Append("\n\n---\n\n");
                    break;
                case "ul": case "ol":
                    listDepth = closing ? Math.Max(0, listDepth - 1) : listDepth + 1;
                    sb.Append("\n");
                    break;
                case "li":
                    if (!closing) sb.Append('\n').Append(new string(' ', Math.Max(0, listDepth - 1) * 2)).Append("- ");
                    break;
                case "pre":
                    inPre = !closing;
                    sb.Append(closing ? "\n```\n" : "\n\n```\n");
                    break;
                case "code":
                    // Inside <pre> the fence already marks it; a nested ` would
                    // just litter the block.
                    if (!inPre) sb.Append('`');
                    break;
                case "strong": case "b":
                    sb.Append("**");
                    break;
                case "em": case "i":
                    sb.Append('*');
                    break;
                case "a":
                    if (closing)
                    {
                        var text = Tidy(linkText.ToString()).Trim();
                        var href = linkHref;
                        linkHref = null;
                        linkText.Clear();
                        // Drop empty-text links (icons, anchors) rather than
                        // emitting "[](url)" noise.
                        if (text.Length == 0) break;
                        if (string.IsNullOrEmpty(href)) { sb.Append(text); break; }
                        sb.Append('[').Append(text).Append("](").Append(href).Append(')');
                    }
                    else
                    {
                        linkHref = Resolve(Attr(raw, "href"), baseUri);
                        linkText.Clear();
                    }
                    break;
                case "img":
                    var alt = Attr(raw, "alt");
                    // Keep the alt text only. The image itself lives on someone
                    // else's server; a board reference is meant to be readable
                    // offline, so a remote <img> would be a broken promise.
                    if (!string.IsNullOrWhiteSpace(alt)) Emit($"[image: {alt}]");
                    break;
                case "td": case "th":
                    sb.Append(closing ? " | " : "");
                    break;
                case "tr":
                    if (closing) sb.Append('\n');
                    break;
            }
        }

        // An unterminated <a> would otherwise swallow the rest of the page.
        if (linkHref != null) sb.Append(Tidy(linkText.ToString()));
    }

    private static string TagName(string raw)
    {
        var end = 0;
        while (end < raw.Length && (char.IsLetterOrDigit(raw[end]))) end++;
        return raw[..end].ToLowerInvariant();
    }

    private static string Attr(string raw, string name)
    {
        var m = Regex.Match(raw, name + @"\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))",
            RegexOptions.IgnoreCase);
        if (!m.Success) return "";
        var v = m.Groups[2].Success ? m.Groups[2].Value
              : m.Groups[3].Success ? m.Groups[3].Value
              : m.Groups[4].Value;
        return DecodeEntities(v).Trim();
    }

    /// Absolute-ise a link, and drop anything that isn't http(s). A `javascript:`
    /// href written into a board would be one click from something unpleasant if
    /// the file is ever rendered somewhere that makes links live.
    private static string Resolve(string href, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(href)) return "";
        if (href.StartsWith("#")) return "";
        try
        {
            var uri = baseUri != null && Uri.TryCreate(baseUri, href, out var abs)
                ? abs
                : Uri.TryCreate(href, UriKind.Absolute, out var a) ? a : null;
            if (uri == null) return "";
            return uri.Scheme is "http" or "https" ? uri.ToString() : "";
        }
        catch { return ""; }
    }

    // ---- text --------------------------------------------------------------

    private static readonly Dictionary<string, string> NamedEntities = new(StringComparer.Ordinal)
    {
        ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'",
        ["nbsp"] = " ", ["mdash"] = "-", ["ndash"] = "-", ["hellip"] = "...",
        ["copy"] = "(c)", ["reg"] = "(R)", ["trade"] = "(TM)", ["rsquo"] = "'",
        ["lsquo"] = "'", ["rdquo"] = "\"", ["ldquo"] = "\"", ["times"] = "x",
        ["middot"] = "-", ["bull"] = "-", ["deg"] = " degrees", ["euro"] = "EUR",
    };

    internal static string DecodeEntities(string s)
    {
        if (s.IndexOf('&') < 0) return s;
        return Regex.Replace(s, @"&(#x?[0-9a-fA-F]+|[a-zA-Z]+);", m =>
        {
            var body = m.Groups[1].Value;
            if (body[0] == '#')
            {
                var hex = body.Length > 1 && (body[1] == 'x' || body[1] == 'X');
                var digits = hex ? body[2..] : body[1..];
                if (int.TryParse(digits,
                        hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var cp)
                    && cp > 0 && cp <= 0x10FFFF)
                {
                    try { return char.ConvertFromUtf32(cp); } catch { return m.Value; }
                }
                return m.Value;
            }
            return NamedEntities.TryGetValue(body, out var v) ? v : m.Value;
        });
    }

    /// Collapse the whitespace the walk inevitably over-produces: runs of
    /// spaces, trailing spaces, and more than one blank line in a row.
    private static string Tidy(string s)
    {
        s = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');
        s = Regex.Replace(s, "[  ]{2,}", " ");
        s = Regex.Replace(s, " +\n", "\n");
        s = Regex.Replace(s, "\n{3,}", "\n\n");
        return s.Trim();
    }
}
