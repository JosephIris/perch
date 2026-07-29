using System.IO;
using System.Text.Json;
using Xunit;

namespace Perch.Tests;

/// Host-side half of the browser-pane URL policy, pinned to the SAME case table
/// the page-side test reads (src/web/test/fixtures/url-policy-cases.json).
///
/// The bug this guards: the page created URL panes pointed at
/// `file:///…/report.html` while UrlPaneController allowed http/https only. The
/// pane appeared, reported its rect, and then nothing — no WebView2 behind the
/// placeholder, no error, just an empty dark rectangle that looked exactly like
/// a page that never loaded. One shared fixture means the next divergence fails
/// in CI in whichever language moved.
public class WebUrlPolicyTests
{
    private sealed record Case(string Url, string? Kind);

    /// Walk up from the test binary to the repo root, then into the web fixture.
    /// Deliberately NOT copied into the output dir: the page-side test must read
    /// the very same bytes, and a copy is a fork waiting to happen.
    private static string FixturePath()
    {
        string? dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "src", "web", "test", "fixtures", "url-policy-cases.json");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "url-policy-cases.json not found walking up from " + AppContext.BaseDirectory);
    }

    private static List<Case> LoadCases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        var list = new List<Case>();
        foreach (var el in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var kindEl = el.GetProperty("kind");
            list.Add(new Case(
                el.GetProperty("url").GetString()!,
                kindEl.ValueKind == JsonValueKind.Null ? null : kindEl.GetString()));
        }
        return list;
    }

    private static WebUrlKind Expected(string? kind) => kind switch
    {
        "web" => WebUrlKind.Web,
        "html-file" => WebUrlKind.HtmlFile,
        null => WebUrlKind.Rejected,
        _ => throw new ArgumentException($"unknown kind '{kind}' in the fixture"),
    };

    [Fact]
    public void SharedFixture_ClassifiesIdenticallyToThePage()
    {
        var cases = LoadCases();
        Assert.True(cases.Count >= 25, "fixture shrank — the cases are load-bearing");
        foreach (var c in cases)
            Assert.Equal(Expected(c.Kind), WebUrlPolicy.Classify(c.Url));
    }

    // ---- the specific holes that produced a blank pane --------------------

    [Fact]
    public void LocalHtmlReport_IsAllowed_SoThePaneActuallyPaints()
    {
        // The whole point: an agent writes a report, the user clicks it, it opens
        // in a pane. Rejecting this is what made the pane render blank.
        Assert.Equal(WebUrlKind.HtmlFile,
            WebUrlPolicy.Classify("file:///C:/Users/me/design-loop/mockup.html"));
        Assert.True(WebUrlPolicy.IsAllowed("file:///C:/out/report.htm"));
    }

    [Fact]
    public void FileScheme_IsHtmlOnly_NoShellLaunchableTargets()
    {
        foreach (var url in new[]
        {
            "file:///C:/tools/setup.exe",
            "file:///C:/tools/payload.ps1",
            "file:///C:/tools/evil.lnk",
            "file:///C:/tools/run.bat",
            "file:///C:/Users/me/",
            "file:///C:/Users/me/report.html.exe",
        })
            Assert.False(WebUrlPolicy.IsAllowed(url), url + " must be refused");
    }

    [Fact]
    public void HandlerAndScriptSchemes_AreRefused()
    {
        // Terminal output is attacker-influenced — none of these may ever be one
        // click from a navigation or a ShellExecute.
        foreach (var url in new[]
        {
            "javascript:alert(1)",
            "data:text/html,<h1>x</h1>",
            "vbscript:msgbox",
            "about:blank",
            "ftp://ftp.example.com/pub/",
            "mailto:hello@buildwithperch.com",
            "ms-settings:privacy",
            "shell:startup",
        })
            Assert.False(WebUrlPolicy.IsAllowed(url), url + " must be refused");
    }

    [Fact]
    public void BareWindowsPath_IsRefused_BecauseTheOtherSideCant()
    {
        // .NET resolves "C:\out\report.html" to an absolute file: Uri all by
        // itself; JS's `new URL` throws on it. Accepting it here would recreate
        // the exact asymmetry the shared fixture exists to prevent. The page
        // normalizes paths into file:// URLs before they ever reach us.
        Assert.False(WebUrlPolicy.IsAllowed(@"C:\out\report.html"));
        Assert.False(WebUrlPolicy.IsAllowed("C:/out/report.html"));
        Assert.True(WebUrlPolicy.IsAllowed("file:///C:/out/report.html"));
    }

    [Fact]
    public void EmptyAndGarbage_AreRefusedWithoutThrowing()
    {
        Assert.False(WebUrlPolicy.IsAllowed(null));
        Assert.False(WebUrlPolicy.IsAllowed(""));
        Assert.False(WebUrlPolicy.IsAllowed("   "));
        Assert.False(WebUrlPolicy.IsAllowed("not a url at all"));
        Assert.False(WebUrlPolicy.IsAllowed("//protocol-relative.example.com"));
    }

    [Fact]
    public void QueryAndFragment_DontSmuggleAnExtensionPastTheCheck()
    {
        // The extension test runs on the path, so a ".html" in the query or
        // fragment of an .exe changes nothing.
        Assert.False(WebUrlPolicy.IsAllowed("file:///C:/tools/setup.exe?x=.html"));
        Assert.False(WebUrlPolicy.IsAllowed("file:///C:/tools/setup.exe#.html"));
        // And the reverse still works — a real .html with a query is fine.
        Assert.True(WebUrlPolicy.IsAllowed("file:///C:/out/report.html?v=2"));
    }

    [Fact]
    public void SchemeComparison_IsCaseInsensitive()
    {
        Assert.Equal(WebUrlKind.Web, WebUrlPolicy.Classify("HTTPS://EXAMPLE.COM/"));
        Assert.Equal(WebUrlKind.HtmlFile, WebUrlPolicy.Classify("FILE:///C:/out/R.HTML"));
    }
}
