using Xunit;

namespace Perch.Tests;

/// The HTML extractor behind board URL references.
///
/// This is the one piece of the board feature working on adversarial input —
/// the open internet — with a hand-rolled scanner rather than a parser. So the
/// bar is not "produces beautiful markdown". It is:
///
///   1. Never throws, whatever it is fed.
///   2. Never emits something dangerous into a file an agent will read.
///   3. Knows when it FAILED, so the caller can say so instead of writing a
///      confident-looking empty page.
public class HtmlToMarkdownTests
{
    // ---- the ordinary case -------------------------------------------------

    [Fact]
    public void ConvertsTheTagsThatCarryMeaning()
    {
        const string html = """
        <html><body>
          <h1>Session cookies</h1>
          <p>Keep the cookie <strong>HttpOnly</strong>.</p>
          <h2>Why</h2>
          <ul><li>Script can't read it</li><li>Survives XSS</li></ul>
          <pre><code>Set-Cookie: sid=x; HttpOnly</code></pre>
        </body></html>
        """;
        var md = HtmlToMarkdown.Convert(html);

        Assert.Contains("# Session cookies", md);
        Assert.Contains("## Why", md);
        Assert.Contains("**HttpOnly**", md);
        Assert.Contains("- Script can't read it", md);
        Assert.Contains("- Survives XSS", md);
        Assert.Contains("```", md);
        Assert.Contains("Set-Cookie: sid=x; HttpOnly", md);
    }

    [Fact]
    public void DropsTheFurnitureThatIsNeverContent()
    {
        const string html = """
        <html><head><title>T</title><style>.a{color:red}</style></head>
        <body>
          <nav><a href="/x">Home</a><a href="/y">Docs</a></nav>
          <script>window.tracker=1;alert('hi')</script>
          <p>The actual sentence.</p>
          <footer>© 2026 Someone</footer>
        </body></html>
        """;
        var md = HtmlToMarkdown.Convert(html);

        Assert.Contains("The actual sentence.", md);
        // Script and style bodies must not survive as text — that is the whole
        // reason they are stripped as blocks rather than tag-by-tag.
        Assert.DoesNotContain("window.tracker", md);
        Assert.DoesNotContain("alert(", md);
        Assert.DoesNotContain("color:red", md);
        Assert.DoesNotContain("Someone", md);
        Assert.DoesNotContain("Docs", md);
    }

    [Fact]
    public void PrefersMainWhenThePageOffersOne()
    {
        const string html = """
        <html><body>
          <div id="sidebar"><p>Unrelated navigation prose that is quite long indeed and goes on.</p></div>
          <main><p>The article body, which is what we came for and is nice and long too.</p></main>
        </body></html>
        """;
        var md = HtmlToMarkdown.Convert(html);
        Assert.Contains("The article body", md);
        Assert.DoesNotContain("Unrelated navigation", md);
    }

    [Fact]
    public void IgnoresAnEmptyMainRatherThanThrowingThePageAway()
    {
        // Some pages ship an empty <main> and put the content elsewhere.
        // Narrowing into it would silently produce nothing.
        const string html = """
        <html><body>
          <main></main>
          <div><p>All of the real content lives out here, and there is a decent amount of it.</p></div>
        </body></html>
        """;
        Assert.Contains("All of the real content", HtmlToMarkdown.Convert(html));
    }

    // ---- links -------------------------------------------------------------

    [Fact]
    public void ResolvesRelativeLinksAgainstThePage()
    {
        const string html = "<p>See <a href=\"/spec/6.2\">section 6.2</a>.</p>";
        var md = HtmlToMarkdown.Convert(html, new System.Uri("https://example.com/docs/index.html"));
        Assert.Contains("[section 6.2](https://example.com/spec/6.2)", md);
    }

    [Fact]
    public void DropsLinksThatArentHttp()
    {
        // A javascript: or file: href written into a board is one click from
        // something unpleasant wherever that markdown is later rendered.
        const string html =
            "<p><a href=\"javascript:alert(1)\">click</a> and " +
            "<a href=\"file:///C:/Windows/System32/x.exe\">this</a> and " +
            "<a href=\"data:text/html,<h1>x\">that</a></p>";
        var md = HtmlToMarkdown.Convert(html);
        Assert.DoesNotContain("javascript:", md);
        Assert.DoesNotContain("file://", md);
        Assert.DoesNotContain("data:", md);
        // The text survives; only the dangerous target is dropped.
        Assert.Contains("click", md);
    }

    [Fact]
    public void DropsEmptyLinksInsteadOfEmittingNoise()
    {
        var md = HtmlToMarkdown.Convert("<p>a<a href=\"https://x.dev/i\"></a>b</p>");
        Assert.DoesNotContain("[]", md);
    }

    [Fact]
    public void KeepsImageAltTextButNeverTheRemoteImage()
    {
        // A board reference has to be readable offline, so a remote <img> would
        // be a broken promise. The alt text is the part that carries meaning.
        var md = HtmlToMarkdown.Convert("<p><img src=\"https://cdn.example.com/a.png\" alt=\"the login form\"></p>");
        Assert.Contains("the login form", md);
        Assert.DoesNotContain("cdn.example.com", md);
        Assert.DoesNotContain("![", md);
    }

    // ---- entities ----------------------------------------------------------

    [Fact]
    public void DecodesTheEntitiesRealPagesUse()
    {
        var md = HtmlToMarkdown.Convert(
            "<p>a &amp; b &lt;tag&gt; &quot;q&quot; &nbsp; &mdash; &hellip; &#65; &#x42;</p>");
        Assert.Contains("a & b <tag> \"q\"", md);
        Assert.Contains("A", md);
        Assert.Contains("B", md);
        Assert.DoesNotContain("&amp;", md);
        Assert.DoesNotContain("&#", md);
    }

    [Fact]
    public void LeavesAnUnknownEntityAloneRatherThanManglingIt()
    {
        Assert.Contains("&zzz;", HtmlToMarkdown.Convert("<p>&zzz;</p>"));
    }

    [Fact]
    public void OutOfRangeNumericEntityDoesNotThrow()
    {
        var md = HtmlToMarkdown.Convert("<p>&#1114112; &#x7FFFFFFF; &#0;</p>");
        Assert.NotNull(md);
    }

    // ---- title -------------------------------------------------------------

    [Fact]
    public void ExtractsTheTitleFromTheOriginalHtml()
    {
        // Read before <head> is stripped — a regression here silently loses
        // every reference's name.
        Assert.Equal("OAuth 2.0 for Browser-Based Apps",
            HtmlToMarkdown.ExtractTitle("<html><head><title>OAuth 2.0 for Browser-Based Apps</title></head><body>x</body></html>"));
        Assert.Equal("A & B", HtmlToMarkdown.ExtractTitle("<title>A &amp; B</title>"));
        Assert.Equal("", HtmlToMarkdown.ExtractTitle("<html><body>no title</body></html>"));
        Assert.Equal("", HtmlToMarkdown.ExtractTitle(""));
    }

    // ---- the honesty rule --------------------------------------------------

    [Fact]
    public void LooksThin_CatchesAJavaScriptRenderedShell()
    {
        // The failure mode that matters: a 200 OK whose body is a div and a
        // bundle. Extraction "succeeds" and produces nothing, and without this
        // the board would carry a confident, empty .md.
        const string spa = """
        <html><head><title>App</title></head>
        <body><div id="root"></div><script src="/bundle.js"></script></body></html>
        """;
        Assert.True(HtmlToMarkdown.LooksThin(HtmlToMarkdown.Convert(spa)));
        Assert.True(HtmlToMarkdown.LooksThin(""));
        Assert.True(HtmlToMarkdown.LooksThin("   \n  "));
    }

    [Fact]
    public void LooksThin_IsFalseForARealPage()
    {
        var body = "<p>" + string.Join(" ", System.Linq.Enumerable.Repeat("Real prose about session cookies.", 20)) + "</p>";
        Assert.False(HtmlToMarkdown.LooksThin(HtmlToMarkdown.Convert(body)));
    }

    // ---- never throws ------------------------------------------------------

    [Fact]
    public void MalformedInputDegradesInsteadOfThrowing()
    {
        foreach (var bad in new[]
        {
            "", "   ", "<", "<p", "<p>unclosed", "</p></div>", "<<<>>>",
            "<a href=", "<a href=\"unterminated>text", "<!-- unterminated comment",
            "<script>never closed", "<pre><code>a", "<h1>", "<ul><li>a",
            "<p>" + new string('x', 50_000) + "</p>",
        })
        {
            var md = HtmlToMarkdown.Convert(bad);
            Assert.NotNull(md);
        }
    }

    [Fact]
    public void DeeplyNestedMarkupDoesNotBlowTheStack()
    {
        // The scanner is iterative precisely so this is boring.
        var html = string.Concat(System.Linq.Enumerable.Repeat("<div>", 5000))
                 + "deep"
                 + string.Concat(System.Linq.Enumerable.Repeat("</div>", 5000));
        Assert.Contains("deep", HtmlToMarkdown.Convert(html));
    }

    [Fact]
    public void AnUnterminatedAnchorDoesNotSwallowTheRestOfThePage()
    {
        var md = HtmlToMarkdown.Convert("<p>before</p><a href=\"https://x.dev\">link text and then the page just ends");
        Assert.Contains("before", md);
        Assert.Contains("link text", md);
    }

    // ---- tidiness ----------------------------------------------------------

    [Fact]
    public void CollapsesTheWhitespaceTheWalkOverProduces()
    {
        var md = HtmlToMarkdown.Convert("<div><div><div><p>one</p></div></div></div><p>two</p>");
        Assert.DoesNotContain("\n\n\n", md);
        Assert.False(md.StartsWith("\n"));
        Assert.False(md.EndsWith("\n"));
    }
}
