using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace Perch;

/// Serves the built web bundle (wwwroot) to the WKWebView over loopback —
/// the mac stand-in for WebView2's SetVirtualHostNameToFolderMapping.
///
/// Loopback HTTP rather than a WKURLSchemeHandler because 127.0.0.1 is a
/// "potentially trustworthy origin": the page keeps secure-context APIs
/// (crypto.subtle, clipboard where WebKit allows it) and root-absolute asset
/// URLs (/fonts/…, /app.css) resolve exactly as they do against the Windows
/// virtual host. Bound to 127.0.0.1 on an ephemeral port; serves static GETs
/// only, no directory listings.
internal sealed class StaticServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _root;
    public int Port { get; }

    public StaticServer(string webRoot)
    {
        _root = Path.GetFullPath(webRoot);
        // Race-free enough: grab a free port, then bind HttpListener to it.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (!_listener.IsListening) { break; }
            catch (Exception ex) { Log.Error("StaticServer.accept", ex); continue; }
            _ = Task.Run(() => Serve(ctx));
        }
    }

    private void Serve(HttpListenerContext ctx)
    {
        try
        {
            var rsp = ctx.Response;
            var rel = Uri.UnescapeDataString(ctx.Request.Url?.AbsolutePath ?? "/").TrimStart('/');
            if (rel.Length == 0) rel = "index.html";

            // Resolve and confine to the web root — no traversal.
            var full = Path.GetFullPath(Path.Combine(_root, rel));
            if (!full.StartsWith(_root, StringComparison.Ordinal) || !File.Exists(full))
            {
                rsp.StatusCode = 404;
                rsp.Close();
                return;
            }

            rsp.ContentType = ContentType(Path.GetExtension(full));
            rsp.Headers["Cache-Control"] = "no-cache";
            var bytes = File.ReadAllBytes(full);
            rsp.ContentLength64 = bytes.Length;
            rsp.OutputStream.Write(bytes);
            rsp.Close();
        }
        catch (Exception ex) { Log.Error("StaticServer.serve", ex); try { ctx.Response.Abort(); } catch { } }
    }

    private static string ContentType(string ext) => ext.ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
