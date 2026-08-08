using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Perch;

/// The alias + reset time a model surfaces in the picker when it's maxed out.
/// ResetsAtMs is Unix-ms (the page formats a local "resets 14:30"), or null when
/// the bucket carried no reset time.
internal readonly record struct ModelUsageLimit(string Alias, bool AtLimit, long? ResetsAtMs);

/// Polls Claude's OAuth usage endpoint and exposes per-model rate-limit state
/// for the model picker. Everything here is best-effort and defensive:
///
///  - The endpoint currently returns 429 on every call (verified 2026-07), so
///    the steady state is "no data" — every consumer must handle that, and the
///    picker just shows all models enabled with no annotations.
///  - At most one request per 10 minutes; after two-plus consecutive 429s the
///    gap opens to 30 minutes. The last SUCCESSFUL payload is retained
///    indefinitely.
///  - Never throws to callers, never blocks the UI thread, and never logs or
///    caches the OAuth token (it's re-read from disk at request time).
internal sealed class UsageService : IDisposable
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    private static readonly TimeSpan NormalGap = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BackoffGap = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private UsageSnapshot? _snapshot;              // last SUCCESSFUL parse; null until one lands
    private DateTime _nextAllowedUtc = DateTime.MinValue;
    private int _consecutive429;
    private volatile bool _inFlight;

    /// Raised when a fetch produced a fresh snapshot so the host can re-push
    /// state. Fires on a threadpool thread — the subscriber must marshal. Never
    /// raised for a 429 / network error / throttled no-op.
    public event Action? Updated;

    /// The at-limit models for the picker, resolved from the last good payload.
    /// Empty when there's no data yet (the normal case) — nothing gets disabled.
    public IReadOnlyList<ModelUsageLimit> CurrentLimits()
    {
        var snap = _snapshot;
        if (snap == null) return Array.Empty<ModelUsageLimit>();
        var list = new List<ModelUsageLimit>();
        // haiku is deliberately absent: the spec maps it to no bucket, so it's
        // never disabled. default is the account default and likewise unscoped.
        foreach (var alias in new[] { "fable", "opus", "sonnet" })
        {
            if (snap.ForModel(alias) is ModelLimit m && m.AtLimit)
                list.Add(new ModelUsageLimit(alias, true, m.ResetsAt?.ToUnixTimeMilliseconds()));
        }
        return list;
    }

    /// Fetch if the throttle allows, else no-op. Fire-and-forget from the host;
    /// resolves quietly and never throws. Safe to call as often as you like —
    /// the internal throttle is what enforces the 10/30-minute cadence.
    public async Task RefreshIfDueAsync()
    {
        if (_inFlight) return;
        if (DateTime.UtcNow < _nextAllowedUtc) return;
        _inFlight = true;
        try
        {
            var changed = await FetchAsync().ConfigureAwait(false);
            if (changed) { try { Updated?.Invoke(); } catch { } }
        }
        catch (Exception ex) { Log.Info("UsageService", $"refresh failed: {ex.Message}"); }
        finally { _inFlight = false; }
    }

    private async Task<bool> FetchAsync()
    {
        var token = ReadAccessToken();
        if (string.IsNullOrEmpty(token))
        {
            // No creds → nothing to do; try again after the normal gap.
            _nextAllowedUtc = DateTime.UtcNow + NormalGap;
            return false;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        // The full Claude Code header set. Content-Type is a content header, so
        // TryAddWithoutValidation on a body-less GET may drop it — harmless; the
        // endpoint 429s regardless, and the rest carry the oauth handshake.
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        req.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Headers.TryAddWithoutValidation("User-Agent", "claude-cli/2.1.208 (external, cli)");
        req.Headers.TryAddWithoutValidation("x-app", "cli");

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Log.Info("UsageService", $"request error: {ex.Message}");
            _consecutive429 = 0;                    // a network error isn't a rate limit
            _nextAllowedUtc = DateTime.UtcNow + NormalGap;
            return false;
        }

        using (resp)
        {
            if ((int)resp.StatusCode == 429)
            {
                _consecutive429++;
                var gap = _consecutive429 >= 2 ? BackoffGap : NormalGap;
                _nextAllowedUtc = DateTime.UtcNow + gap;
                Log.Info("UsageService", $"429 (streak {_consecutive429}); next in {gap.TotalMinutes:0}m");
                return false;
            }

            _consecutive429 = 0;
            _nextAllowedUtc = DateTime.UtcNow + NormalGap;
            if (!resp.IsSuccessStatusCode)
            {
                Log.Info("UsageService", $"http {(int)resp.StatusCode}");
                return false;
            }

            string body;
            try { body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
            catch { return false; }
            // Retain even an empty parse — it's still the latest good payload,
            // and the spec says keep the last success indefinitely.
            _snapshot = UsageParser.Parse(body);
            return true;
        }
    }

    /// Read claudeAiOauth.accessToken from %USERPROFILE%\.claude\.credentials.json.
    /// Returns null on any problem. The token is used immediately and never
    /// stored on the instance or logged.
    private static string? ReadAccessToken()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(home, ".claude", ".credentials.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                && oauth.ValueKind == JsonValueKind.Object
                && oauth.TryGetProperty("accessToken", out var tok)
                && tok.ValueKind == JsonValueKind.String)
            {
                var s = tok.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
        }
        catch { }
        return null;
    }

    public void Dispose() { try { _http.Dispose(); } catch { } }
}
