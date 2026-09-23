using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Perch;

/// One Drive file or folder as the inbox needs it.
internal sealed record DriveFile(string Id, string Name, string MimeType, DateTimeOffset Modified, long Size, string[] Parents)
{
    public bool IsFolder => MimeType == "application/vnd.google-apps.folder";
}

/// The few Drive v3 calls the inbox makes, authenticated as a service account
/// from its JSON key. Hand-rolled (JWT bearer grant + four REST calls) rather
/// than pulling in the Google SDK: it is a few dozen lines, and the SDK would
/// be most of the app's dependency weight for one opt-in feature.
///
/// The service account only sees what has been shared with it, so the full
/// `drive` scope reaches exactly the inbox folder. It needs full scope rather
/// than readonly because the inbox's state file is written back.
internal sealed class DriveClient : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private const string Api = "https://www.googleapis.com/drive/v3/files";
    private const string UploadApi = "https://www.googleapis.com/upload/drive/v3/files";

    private readonly string _email;
    private readonly RSA _rsa;
    private string? _token;
    private DateTimeOffset _tokenExpires;

    private DriveClient(string email, RSA rsa) { _email = email; _rsa = rsa; }

    /// Parse a service-account JSON key. Throws with a readable message when
    /// the text isn't one — the caller shows it as the inbox's status.
    public static DriveClient FromKeyJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var email = root.TryGetProperty("client_email", out var e) ? e.GetString() : null;
        var pem = root.TryGetProperty("private_key", out var k) ? k.GetString() : null;
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(pem))
            throw new InvalidOperationException("the key has no client_email / private_key");
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return new DriveClient(email, rsa);
    }

    public string Email => _email;

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token != null && DateTimeOffset.UtcNow < _tokenExpires) return _token;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = B64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var claims = B64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = _email,
            ["scope"] = "https://www.googleapis.com/auth/drive",
            ["aud"] = "https://oauth2.googleapis.com/token",
            ["iat"] = now,
            ["exp"] = now + 3600,
        }));
        var unsigned = header + "." + claims;
        var sig = B64Url(_rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = unsigned + "." + sig,
        });
        using var resp = await Http.PostAsync("https://oauth2.googleapis.com/token", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new DriveException((int)resp.StatusCode, "sign-in failed: " + Brief(body));
        using var doc = JsonDocument.Parse(body);
        _token = doc.RootElement.GetProperty("access_token").GetString();
        var ttl = doc.RootElement.TryGetProperty("expires_in", out var x) ? x.GetInt32() : 3600;
        _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(ttl - 120);
        return _token!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        var resp = await Http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode) return resp;
        var body = await resp.Content.ReadAsStringAsync(ct);
        var code = (int)resp.StatusCode;
        resp.Dispose();
        throw new DriveException(code, Brief(body));
    }

    /// Every non-trashed file matching a Drive query, following pages.
    public async Task<List<DriveFile>> QueryAsync(string q, CancellationToken ct)
    {
        var all = new List<DriveFile>();
        string? page = null;
        do
        {
            var url = $"{Api}?q={Uri.EscapeDataString(q + " and trashed=false")}" +
                      "&fields=nextPageToken,files(id,name,mimeType,modifiedTime,size,parents)" +
                      "&pageSize=1000&supportsAllDrives=true&includeItemsFromAllDrives=true" +
                      (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
            using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            foreach (var f in doc.RootElement.GetProperty("files").EnumerateArray())
                all.Add(Parse(f));
            page = doc.RootElement.TryGetProperty("nextPageToken", out var p) ? p.GetString() : null;
        } while (page != null);
        return all;
    }

    public Task<List<DriveFile>> ListChildrenAsync(string folderId, CancellationToken ct)
        => QueryAsync($"'{folderId.Replace("'", "")}' in parents", ct);

    public async Task<byte[]> DownloadAsync(string fileId, CancellationToken ct)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{Api}/{Uri.EscapeDataString(fileId)}?alt=media&supportsAllDrives=true"), ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// Replace an EXISTING file's content. Deliberately no create: a service
    /// account has no storage quota of its own, so Drive refuses a file it
    /// would own inside a user's My Drive — but it may edit one the user made.
    public async Task UpdateContentAsync(string fileId, byte[] content, string mime, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch,
            $"{UploadApi}/{Uri.EscapeDataString(fileId)}?uploadType=media&supportsAllDrives=true")
        {
            Content = new ByteArrayContent(content),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        using var _ = await SendAsync(req, ct);
    }

    /// Create a file in a folder. Drive may refuse this for a service account
    /// in a user's My Drive (it has no storage quota of its own); the caller
    /// turns that refusal into instructions.
    public async Task<string> CreateFileAsync(string folderId, string name, byte[] content, string mime, CancellationToken ct)
    {
        var meta = JsonSerializer.Serialize(new { name, parents = new[] { folderId } });
        var body = new MultipartContent("related");
        body.Add(new StringContent(meta, Encoding.UTF8, "application/json"));
        var data = new ByteArrayContent(content);
        data.Headers.ContentType = new MediaTypeHeaderValue(mime);
        body.Add(data);
        var req = new HttpRequestMessage(HttpMethod.Post, $"{UploadApi}?uploadType=multipart&supportsAllDrives=true&fields=id") { Content = body };
        using var resp = await SendAsync(req, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("id").GetString() ?? "";
    }

    private static DriveFile Parse(JsonElement f)
    {
        var parents = f.TryGetProperty("parents", out var ps)
            ? System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(ps.EnumerateArray(), p => p.GetString() ?? ""))
            : Array.Empty<string>();
        return new DriveFile(
            f.GetProperty("id").GetString() ?? "",
            f.GetProperty("name").GetString() ?? "",
            f.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "",
            f.TryGetProperty("modifiedTime", out var t) && DateTimeOffset.TryParse(t.GetString(), out var d) ? d : DateTimeOffset.MinValue,
            f.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var n) ? n : 0,
            parents);
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// Drive's error JSON is long; keep the message line.
    private static string Brief(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m)) return m.GetString() ?? body;
                if (doc.RootElement.TryGetProperty("error_description", out var ed)) return ed.GetString() ?? body;
                return e.ToString();
            }
        }
        catch { }
        return body.Length > 200 ? body[..200] : body;
    }

    public void Dispose() => _rsa.Dispose();
}

internal sealed class DriveException : Exception
{
    public int Status { get; }
    public DriveException(int status, string message) : base(message) { Status = status; }
}
