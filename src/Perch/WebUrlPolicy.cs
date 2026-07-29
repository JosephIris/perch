using System;

namespace Perch;

/// What Perch will point a browser at — ONE definition for both consumers:
///
///   - UrlPaneController.OnLayout — creates/navigates a native WebView2 pane
///   - MainWindow.OnUrlOpen       — ShellExecutes into the default browser
///
/// The two rules had drifted: url.open allowed a local .html file (so "open the
/// report the agent just wrote" worked), while the URL pane allowed http/https
/// only. Choosing "open in pane" on the same link therefore produced a pane with
/// no WebView2 in it — a blank rectangle, silent apart from one log line.
///
/// Mirrors src/web/src/web-url.ts. Both sides are pinned to the same case table
/// (src/web/test/fixtures/url-policy-cases.json) so they can't drift again.
internal enum WebUrlKind
{
    Rejected = 0,
    /// http / https — any web address.
    Web,
    /// file:// pointing at a .html/.htm document. Allowed because a local page
    /// in a WebView2 is the same exposure as opening it in the default browser,
    /// which we already permit. NOT allowed: an .exe/.ps1/.lnk, a bare
    /// directory, or any other scheme handler — terminal output is
    /// attacker-influenced and must never be one click from a shell launch.
    HtmlFile,
}

internal static class WebUrlPolicy
{
    /// Classify a URL. Rejected for anything that isn't an absolute http(s) URL
    /// or a file:// .html/.htm.
    public static WebUrlKind Classify(string? url)
    {
        if (string.IsNullOrEmpty(url)) return WebUrlKind.Rejected;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return WebUrlKind.Rejected;
        // Require the scheme to be WRITTEN. .NET resolves a bare Windows path
        // ("C:\out\report.html") to an absolute Uri with Scheme == "file", which
        // JS's `new URL` rejects outright — accepting it here would make the two
        // policies disagree on exactly the input that caused the original bug.
        // Callers hand us a URL; a raw path is the page's job to normalize.
        if (!url.StartsWith(uri.Scheme + ":", StringComparison.OrdinalIgnoreCase))
            return WebUrlKind.Rejected;
        if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            return WebUrlKind.Web;
        if (uri.Scheme == Uri.UriSchemeFile)
        {
            var path = uri.LocalPath;
            return path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
                    ? WebUrlKind.HtmlFile
                    : WebUrlKind.Rejected;
        }
        return WebUrlKind.Rejected;
    }

    public static bool IsAllowed(string? url) => Classify(url) != WebUrlKind.Rejected;
}
