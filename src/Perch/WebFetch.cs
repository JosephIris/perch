using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Perch;

/// Fetch a web page for a board reference.
///
/// This is the first place Perch fetches a URL the USER supplied, rather than
/// one endpoint we control, so it carries guards the app has not needed before:
///
///   - **Scheme.** Only http/https, via the same WebUrlPolicy the browser pane
///     uses. Not negotiable — a board reference is a thing an agent will open.
///   - **Size.** The response is read through a capped stream. Without a cap,
///     "paste a link" is an unbounded allocation in the UI process, and this
///     process has already died of OOM once under a large burst.
///   - **Redirects.** Capped, and the FINAL url is re-checked: a http(s) link
///     that redirects to something else must not slip past the scheme gate.
///   - **Content type.** HTML/text only. A PDF or a zip would be stored as a
///     .md full of binary, which helps nobody.
///
/// Loopback and private addresses are deliberately NOT blocked: pasting a link
/// to a local dev server's docs is a real thing to want, the URL comes from the
/// user's own clipboard, and Perch is a local tool rather than a service taking
/// URLs from strangers.
internal static class WebFetch
{
    /// 8 MiB. Comfortably more than any documentation page and far less than
    /// anything that would hurt.
    private const int MaxBytes = 8 << 20;

    /// Long-lived, like UsageService's. A per-call HttpClient burns sockets.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        // Some docs sites serve a stub or a 403 to an unidentified client.
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Perch/1.0");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,text/plain;q=0.9,*/*;q=0.1");
        return http;
    }

    internal sealed record Result(bool Ok, string Html, string FinalUrl, string Error);

    /// GET `url` as text. Never throws — every failure comes back as
    /// Ok=false with a message fit to show the user, because "the fetch didn't
    /// work" is ordinary and should read as information, not as a crash.
    public static async Task<Result> GetAsync(string url, CancellationToken ct = default)
    {
        if (WebUrlPolicy.Classify(url) != WebUrlKind.Web)
            return new Result(false, "", url, "Only http and https links can be fetched.");

        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);

            // Re-check after redirects: the gate above only saw the URL we were
            // given, and a redirect can land somewhere else entirely.
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
            if (WebUrlPolicy.Classify(finalUrl) != WebUrlKind.Web)
                return new Result(false, "", finalUrl, "That link redirected somewhere Perch won't follow.");

            if (!resp.IsSuccessStatusCode)
                return new Result(false, "", finalUrl, $"The site answered {(int)resp.StatusCode} {resp.ReasonPhrase}.");

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.Length > 0
                && !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            {
                return new Result(false, "", finalUrl, $"That link is {mediaType}, not a web page.");
            }

            // Declared length is a hint, not a promise — read through a cap
            // regardless, so a lying or absent Content-Length can't matter.
            var declared = resp.Content.Headers.ContentLength;
            if (declared is > MaxBytes)
                return new Result(false, "", finalUrl, "That page is too big to keep on a board.");

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buf = new byte[81920];
            using var ms = new System.IO.MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + read > MaxBytes)
                    return new Result(false, "", finalUrl, "That page is too big to keep on a board.");
                ms.Write(buf, 0, read);
            }

            var charset = resp.Content.Headers.ContentType?.CharSet;
            return new Result(true, Decode(ms.ToArray(), charset), finalUrl, "");
        }
        catch (TaskCanceledException)
        {
            return new Result(false, "", url, "The site took too long to answer.");
        }
        catch (Exception ex)
        {
            Log.Info("WebFetch", $"{url}: {ex.Message}");
            return new Result(false, "", url, "Couldn't reach that link.");
        }
    }

    /// Decode bytes using the declared charset, falling back to UTF-8. An
    /// unknown or absent charset is common and must not fail the fetch.
    private static string Decode(byte[] bytes, string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes); }
            catch { /* fall through */ }
        }
        return Encoding.UTF8.GetString(bytes);
    }
}
