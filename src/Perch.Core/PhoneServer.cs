using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Perch;

/// The phone link: a small HTTP server on the local network that the Perch
/// iPhone app (ios/) talks to. It lists the sessions, types a line into one
/// (confirmed like everything typed into a Claude: LineDelivery), and hands
/// back a session's latest answer for the phone to show and read aloud.
///
/// Off unless Settings.PhoneEnabled. Every request carries the pairing token
/// (`Authorization: Bearer …`) the phone got from the QR code in Settings →
/// Phone; anything else is a 401, and an address that keeps guessing is shut
/// out for a minute. Plain HTTP: the token crosses the wifi in the clear, so
/// this is for a network you trust (home, office), not a café.
///
/// A raw TcpListener rather than HttpListener: on Windows HttpListener needs a
/// URL reservation (admin) to listen beyond localhost; a socket only needs the
/// firewall's one-time "allow" prompt. The HTTP it speaks is the small subset
/// the app uses: one request per connection, JSON in, JSON out.
///
/// The protocol is written down in docs/PHONE-API.md; keep the two in step.
internal sealed class PhoneServer : IDisposable
{
    public const int ApiVersion = 1;
    public const int DefaultPort = 47800;
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 64 * 1024;
    private const int BadAuthLimit = 10;
    private static readonly TimeSpan BadAuthWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    public sealed class Host
    {
        public required Func<IReadOnlyList<PhoneSession>> Sessions { get; init; }
        /// Type `text` into the session and submit it. Returns what happened:
        /// "queued", "answered" (it was waiting on a question), "empty",
        /// "unsupported" (a shell or codex tab) or "missing".
        public required Func<Guid, string, string> Send { get; init; }
        public required Func<Guid, Task<PhoneReply?>> Reply { get; init; }
        /// The current pairing token; read per request so "New code" needs no restart.
        public required Func<string> Token { get; init; }
        public required Func<string> Name { get; init; }
    }

    private readonly IUiThread _ui;
    private readonly Host _h;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<IPAddress, (int Count, DateTime Since)> _bad = new();
    private TcpListener? _listener;
    private bool _disposed;

    public PhoneServer(IUiThread ui, Host h)
    {
        _ui = ui;
        _h = h;
    }

    /// The port actually bound (differs from the requested one only for 0, in tests).
    public int Port { get; private set; }

    public void Start(int port)
    {
        if (_listener != null) return;
        var l = new TcpListener(IPAddress.IPv6Any, port);
        // Dual-stack, so the phone can use either family.
        l.Server.DualMode = true;
        l.Start();
        _listener = l;
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoopAsync(l, _cts.Token));
        Log.Info("Phone.start", $"listening on port {Port}");
    }

    private async Task AcceptLoopAsync(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.Error("Phone.accept", ex);
                try { await Task.Delay(250, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }
            _ = Task.Run(() => ServeAsync(client, ct));
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var _c = client;
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            var stream = client.GetStream();
            var req = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
            if (req == null) { await WriteAsync(stream, 400, new { error = "bad request" }, ct); return; }
            var (status, body) = await HandleAsync(remote, req).ConfigureAwait(false);
            await WriteAsync(stream, status, body, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex) { Log.Error("Phone.serve", ex); }
    }

    internal sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    private static async Task<Request?> ReadRequestAsync(Stream s, CancellationToken ct)
    {
        // Headers: read until the blank line.
        var buf = new List<byte>(1024);
        var one = new byte[1];
        while (true)
        {
            if (buf.Count > MaxHeaderBytes) return null;
            var n = await s.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) return null;
            buf.Add(one[0]);
            var c = buf.Count;
            if (c >= 4 && buf[c - 4] == '\r' && buf[c - 3] == '\n' && buf[c - 2] == '\r' && buf[c - 1] == '\n') break;
        }
        var lines = Encoding.ASCII.GetString(buf.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var i = line.IndexOf(':');
            if (i > 0) headers[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        var body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var len) && len > 0)
        {
            if (len > MaxBodyBytes) return null;
            body = new byte[len];
            var at = 0;
            while (at < len)
            {
                var n = await s.ReadAsync(body.AsMemory(at, len - at), ct).ConfigureAwait(false);
                if (n == 0) return null;
                at += n;
            }
        }
        var path = first[1];
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        return new Request(first[0].ToUpperInvariant(), path, headers, body);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static async Task WriteAsync(Stream s, int status, object body, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 429 => "Too Many Requests", _ => "Error" };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {json.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await s.WriteAsync(json, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    internal async Task<(int Status, object Body)> HandleAsync(IPAddress remote, Request req)
    {
        if (_bad.TryGetValue(remote, out var bad) && bad.Count >= BadAuthLimit && DateTime.UtcNow - bad.Since < BadAuthWindow)
            return (429, new { error = "too many attempts, wait a minute" });
        if (!Authorized(req))
        {
            _bad.AddOrUpdate(remote, (1, DateTime.UtcNow),
                (_, o) => DateTime.UtcNow - o.Since < BadAuthWindow ? (o.Count + 1, o.Since) : (1, DateTime.UtcNow));
            Log.Info("Phone.auth", $"rejected {remote}");
            return (401, new { error = "not paired" });
        }
        _bad.TryRemove(remote, out _);

        var parts = req.Path.Trim('/').Split('/');
        // GET /v1/hello
        if (req.Method == "GET" && parts is ["v1", "hello"])
            // hosts: the addresses as they are NOW, so a phone paired before
            // Tailscale was installed learns its address without rescanning.
            return (200, new { app = "perch", api = ApiVersion, name = await OnUi(() => _h.Name()), hosts = LocalAddresses() });
        // GET /v1/sessions
        if (req.Method == "GET" && parts is ["v1", "sessions"])
            return (200, new { sessions = await OnUi(() => _h.Sessions()) });
        if (parts is ["v1", "sessions", var idText, var verb] && Guid.TryParse(idText, out var id))
        {
            // POST /v1/sessions/{id}/send  {"text": "...", "voice": true}
            if (req.Method == "POST" && verb == "send")
            {
                SendBody? b;
                try { b = JsonSerializer.Deserialize<SendBody>(req.Body, Json); }
                catch (JsonException) { return (400, new { error = "body must be {\"text\": \"...\"}" }); }
                var text = (b?.Text ?? "").Trim();
                if (b?.Voice == true && text.Length > 0) text += " " + VoiceTag;
                var result = await OnUi(() => _h.Send(id, text));
                return (result == "missing" ? 404 : 200, new { result });
            }
            // GET /v1/sessions/{id}/reply
            if (req.Method == "GET" && verb == "reply")
            {
                var tcs = new TaskCompletionSource<PhoneReply?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ui.Post(async () =>
                {
                    try { tcs.SetResult(await _h.Reply(id)); }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                var r = await tcs.Task.ConfigureAwait(false);
                return (200, new { text = r?.Text, atMs = r?.AtMs });
            }
        }
        return (404, new { error = "no such endpoint" });
    }

    /// Same tag the page's dictation adds (voice.ts VOICE_TAG), so the model
    /// reads for meaning rather than wording.
    public const string VoiceTag = "[voice input]";

    private sealed record SendBody(string? Text, bool? Voice);

    private bool Authorized(Request req)
    {
        var token = _h.Token();
        if (string.IsNullOrEmpty(token)) return false;
        if (!req.Headers.TryGetValue("Authorization", out var auth) || !auth.StartsWith("Bearer ", StringComparison.Ordinal))
            return false;
        var given = Encoding.UTF8.GetBytes(auth["Bearer ".Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(token));
    }

    private async Task<T> OnUi<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    /// A fresh pairing token: 32 random bytes, URL-safe.
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// This machine's addresses the phone can try, best first: the wifi/LAN
    /// ones, then Tailscale's (so a phone away from home still reaches it
    /// when both are on the owner's tailnet). Virtual adapters (WSL, Hyper-V,
    /// Docker) are skipped where they say so.
    public static List<string> LocalAddresses()
    {
        var list = new List<(string Addr, int Rank)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var label = (ni.Name + " " + ni.Description).ToLowerInvariant();
                if (label.Contains("vethernet") || label.Contains("hyper-v") || label.Contains("wsl")
                    || label.Contains("docker") || label.Contains("virtualbox") || label.Contains("vmware")) continue;
                var props = ni.GetIPProperties();
                // An adapter with a default gateway is the one on a real network.
                var hasGateway = props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
                var tunnel = ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel;
                foreach (var ua in props.UnicastAddresses)
                    if (Rank(ua.Address, hasGateway, tunnel) is int rank) list.Add((ua.Address.ToString(), rank));
            }
        }
        catch (Exception ex) { Log.Error("Phone.addresses", ex); }
        return list.OrderBy(x => x.Rank).Select(x => x.Addr).Distinct().ToList();
    }

    /// Where an address goes in the list the phone tries, or null to leave it
    /// out. Pure. Lower is tried first.
    internal static int? Rank(IPAddress a, bool hasGateway, bool tunnel)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) return null;
        var b = a.GetAddressBytes();
        if (b[0] == 169 && b[1] == 254) return null;   // link-local: no DHCP
        // Tailscale hands out 100.64.0.0/10. After the LAN: at home the wifi
        // is the shorter way; away, it is the only way.
        if (IsTailscale(a)) return 10;
        // Any other tunnel (a work VPN) is not somewhere the phone can reach.
        if (tunnel) return null;
        var isPrivate = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        return (hasGateway ? 0 : 2) + (isPrivate ? 0 : 1);
    }

    internal static bool IsTailscale(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return a.AddressFamily == AddressFamily.InterNetwork && b[0] == 100 && (b[1] & 0xC0) == 64;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts.Dispose();
        Log.Info("Phone.stop", "stopped");
    }
}

/// One tab as the phone sees it. Kind: "claude", "thread" (a project chat's
/// thread), "chat" (a project chat), "codex" or "shell". CanSend is false for
/// the last two in API v1.
internal sealed record PhoneSession(
    Guid Id, string Title, string? Project, string Kind, string State,
    bool CanSend, bool Active, bool Asleep);

internal sealed record PhoneReply(string Text, long? AtMs);
