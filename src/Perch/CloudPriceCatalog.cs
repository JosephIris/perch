using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Perch;

/// How a VM is provisioned, which is what decides WHICH price it pays. Read off
/// the instance's scheduling.provisioningModel — the same box costs wildly
/// different amounts depending on this, so guessing "on-demand" for everything
/// is how you end up telling someone their DWS GPU costs 2x what it does.
internal enum PriceVariant { Standard, Spot, FlexStart }

/// One priceable row, distilled from the billing catalog. The raw catalog is
/// ~24MB of JSON across 31k SKUs; we keep only what prices a VM (~1.4MB), because
/// this gets cached to disk and re-read on every launch.
internal sealed record SkuRow(
    [property: JsonPropertyName("d")] string Desc,
    [property: JsonPropertyName("r")] string[] Regions,
    [property: JsonPropertyName("g")] string Group,      // CPU | RAM | GPU
    [property: JsonPropertyName("p")] double Price);     // USD per usage unit (vCPU-h, GiB-h, GPU-h)

internal sealed record SkuCacheFile(
    [property: JsonPropertyName("fetchedAtUnixMs")] long FetchedAtUnixMs,
    [property: JsonPropertyName("rows")] List<SkuRow> Rows);

[JsonSerializable(typeof(SkuCacheFile))]
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
internal partial class SkuJsonContext : JsonSerializerContext { }

/// The machine-type facts a price needs. gcloud's `instances list` gives us the
/// machine TYPE but not its shape, so these come from `machine-types list`.
internal sealed record MachineSpec(int Cpus, double MemGiB, IReadOnlyList<(string Type, int Count)> Accelerators);

/// Prices a VM from Google's public billing catalog instead of a table we hand-
/// maintain.
///
/// Why bother: a static table goes stale, can't know about a machine type we
/// never typed in, and — the real killer — only knows ONE price per shape. GCP
/// charges very differently for the same box depending on how it was provisioned:
/// an A100-80GB is $3.93/hr on-demand but $1.85/hr under DWS Flex-Start. A table
/// that says "a2-ultragpu-4g = $20.28" is simply wrong for a DWS box, by ~72%.
///
/// How it works: a machine's price is the SUM of component SKUs, not one lookup —
///     a2-ultragpu-4g = 48 × "A2 Instance Core" + 680 × "A2 Instance Ram"
///                          + 4 × "Nvidia Tesla A100 80GB GPU"
/// each resolved for the machine's region and provisioning model. That's what
/// makes it scale: the only mapping left is family → "A2 Instance Core" (~a dozen
/// families, and mostly a plain uppercase of the type's first segment), so new
/// SIZES inside a family price themselves and rate changes land on their own.
///
/// It is still LIST price. It does not know your committed-use or negotiated
/// contract discounts — for those, cloud-prices.json still overrides everything.
/// Every failure path falls back to CloudPricing's static table: a missing price
/// must never take the panel down.
internal sealed class CloudPriceCatalog
{
    /// Compute Engine's service id in the public billing catalog. Stable.
    private const string ComputeService = "6F81-5844-456A";

    /// List prices move on the order of months. A day is plenty, and it keeps a
    /// cold launch from paying for 31k SKUs.
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private static readonly Regex SizeToken = new(@"\b\d+gb\b", RegexOptions.Compiled);

    private List<SkuRow>? _rows;
    private bool _tried;

    /// Test seam: price from a fixture instead of the network.
    internal void SeedRows(IEnumerable<SkuRow> rows) { _rows = rows.ToList(); _tried = true; }

    public static string CachePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Perch", "sku-cache.json");

    /// True once we have rows to price from (cached or freshly fetched). Never
    /// throws — a catalog we can't reach just means the static table stays in
    /// charge.
    public async Task<bool> EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_rows is { Count: > 0 }) return true;
        if (_tried) return false;          // one attempt per session; don't retry-storm
        _tried = true;

        try
        {
            var cached = ReadCache();
            if (cached != null) { _rows = cached; return true; }

            var fresh = await FetchAsync(ct).ConfigureAwait(false);
            if (fresh == null || fresh.Count == 0) return false;
            _rows = fresh;
            WriteCache(fresh);
            Log.Info($"CloudPriceCatalog: fetched {fresh.Count} priceable SKUs");
            return true;
        }
        catch (Exception ex) { Log.Error("CloudPriceCatalog.EnsureLoaded", ex); return false; }
    }

    /// USD/hour for this machine, or null when any component can't be resolved —
    /// in which case the caller falls back to the static table. Partial answers
    /// are worse than none: a GPU box priced without its GPU reads as cheap.
    public double? PerHour(string machineType, string region, PriceVariant variant, MachineSpec spec)
    {
        if (_rows == null || _rows.Count == 0) return null;
        var family = FamilyOf(machineType);
        if (family.Length == 0) return null;
        var custom = machineType.Contains("custom", StringComparison.OrdinalIgnoreCase);

        var core = Rate("CPU", d => IsInstance(d, "core") && FamilyMatches(d, family, custom), region, variant);
        var ram  = Rate("RAM", d => IsInstance(d, "ram")  && FamilyMatches(d, family, custom), region, variant);
        if (core == null || ram == null) return null;

        var total = spec.Cpus * core.Value + spec.MemGiB * ram.Value;

        foreach (var (type, count) in spec.Accelerators)
        {
            var gpu = Rate("GPU", d => GpuMatches(d, type), region, variant);
            if (gpu == null) return null;      // a GPU we can't price → don't guess
            total += count * gpu.Value;
        }
        return total;
    }

    /// Resolve one component. Falls back to the Standard rate when the variant has
    /// no SKU of its own — GCP publishes a DWS/Spot price for the GPU but not
    /// always for the cores and RAM beside it, and dropping the whole machine over
    /// that would be worse than pricing its CPU at on-demand.
    private double? Rate(string group, Func<string, bool> match, string region, PriceVariant variant)
        => Find(group, match, region, variant) ?? (variant == PriceVariant.Standard ? null : Find(group, match, region, PriceVariant.Standard));

    private double? Find(string group, Func<string, bool> match, string region, PriceVariant variant)
    {
        foreach (var r in _rows!)
        {
            if (!string.Equals(r.Group, group, StringComparison.OrdinalIgnoreCase)) continue;
            if (!r.Regions.Contains(region, StringComparer.OrdinalIgnoreCase)) continue;
            var d = r.Desc.ToLowerInvariant();
            if (!VariantMatches(d, variant)) continue;
            if (!match(d)) continue;
            return r.Price;
        }
        return null;
    }

    // ---- description matching -------------------------------------------------
    // The catalog has no machine-type field; everything is encoded in prose like
    // "Spot Preemptible A2 Instance Core running in Americas". These predicates
    // are the whole trick, so they're deliberately narrow — a wrong match prices
    // the wrong thing, which is worse than no match at all (that just falls back).

    internal static string FamilyOf(string? machineType)
    {
        if (string.IsNullOrWhiteSpace(machineType)) return "";
        var dash = machineType!.IndexOf('-');
        return (dash > 0 ? machineType[..dash] : machineType).ToLowerInvariant();
    }

    private static bool IsInstance(string desc, string kind) => desc.Contains($"instance {kind}");

    private static bool FamilyMatches(string desc, string family, bool custom)
    {
        if (!Regex.IsMatch(desc, $@"\b{Regex.Escape(family)}\b")) return false;
        // "E2 Custom Instance Core" prices custom shapes only; a predefined type
        // must not match it, and vice versa.
        return desc.Contains("custom") == custom;
    }

    /// nvidia-a100-80gb → "Nvidia Tesla A100 80GB GPU running in Americas".
    /// The size token is load-bearing: without it "a100" also matches the 80GB
    /// SKU (a ~30% price difference on the same-looking row).
    internal static bool GpuMatches(string desc, string acceleratorType)
    {
        var toks = acceleratorType.ToLowerInvariant()
            .Replace("nvidia-", "").Replace("tesla-", "")
            .Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (toks.Length == 0) return false;
        foreach (var t in toks)
            if (!Regex.IsMatch(desc, $@"\b{Regex.Escape(t)}\b")) return false;
        return toks.Any(t => SizeToken.IsMatch(t)) == SizeToken.IsMatch(desc);
    }

    private static bool VariantMatches(string desc, PriceVariant v)
    {
        var spot = desc.Contains("spot preemptible") || desc.Contains("preemptible");
        var dws  = desc.Contains("dws");
        return v switch
        {
            PriceVariant.Spot      => spot,
            PriceVariant.FlexStart => dws,
            _                      => !spot && !dws,
        };
    }

    internal static PriceVariant VariantOf(string? provisioningModel) =>
        (provisioningModel ?? "").ToUpperInvariant() switch
        {
            "SPOT"       => PriceVariant.Spot,
            "FLEX_START" => PriceVariant.FlexStart,
            _            => PriceVariant.Standard,
        };

    /// Rows we can never price a running VM from. Dropping them is most of why the
    /// cache is 1.4MB instead of 24MB.
    private static bool Keep(string group, string desc)
    {
        if (group is not ("CPU" or "RAM" or "GPU")) return false;
        var d = desc.ToLowerInvariant();
        if (d.Contains("commitment")) return false;      // you don't buy these per-hour
        if (d.StartsWith("reserved")) return false;
        if (d.Contains("sole tenancy")) return false;
        if (d.Contains("premium for")) return false;
        return true;
    }

    // ---- fetch + cache --------------------------------------------------------

    internal static List<SkuRow> Distill(JsonElement skus)
    {
        var rows = new List<SkuRow>();
        foreach (var s in skus.EnumerateArray())
        {
            var desc = Str(s, "description");
            if (desc == null) continue;
            if (!s.TryGetProperty("category", out var cat)) continue;
            var group = Str(cat, "resourceGroup") ?? "";
            if (!Keep(group, desc)) continue;

            if (!s.TryGetProperty("pricingInfo", out var pi) || pi.ValueKind != JsonValueKind.Array) continue;
            var first = pi.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object) continue;
            if (!first.TryGetProperty("pricingExpression", out var pe)) continue;
            if (!pe.TryGetProperty("tieredRates", out var tr) || tr.ValueKind != JsonValueKind.Array) continue;

            // Last tier is the rate that actually applies past any free tier.
            var tier = tr.EnumerateArray().LastOrDefault();
            if (tier.ValueKind != JsonValueKind.Object) continue;
            if (!tier.TryGetProperty("unitPrice", out var up)) continue;
            var units = Str(up, "units");
            var nanos = up.TryGetProperty("nanos", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : 0;
            var price = (double.TryParse(units, out var u) ? u : 0) + nanos / 1e9;
            if (price <= 0) continue;

            var regions = s.TryGetProperty("serviceRegions", out var sr) && sr.ValueKind == JsonValueKind.Array
                ? sr.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                : Array.Empty<string>();
            if (regions.Length == 0) continue;

            rows.Add(new SkuRow(desc, regions, group, price));
        }
        return rows;
    }

    private async Task<List<SkuRow>?> FetchAsync(CancellationToken ct)
    {
        var token = await AccessTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token)) { Log.Info("CloudPriceCatalog: no gcloud access token; static prices stay in charge"); return null; }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var rows = new List<SkuRow>();
        string? page = null;
        for (var guard = 0; guard < 40; guard++)      // ~7 pages in practice; bounded so a bad token can't spin
        {
            var url = $"https://cloudbilling.googleapis.com/v1/services/{ComputeService}/skus?currencyCode=USD&pageSize=5000";
            if (!string.IsNullOrEmpty(page)) url += $"&pageToken={Uri.EscapeDataString(page)}";

            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Info($"CloudPriceCatalog: catalog HTTP {(int)resp.StatusCode}; static prices stay in charge");
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("skus", out var skus) && skus.ValueKind == JsonValueKind.Array)
                rows.AddRange(Distill(skus));

            page = Str(doc.RootElement, "nextPageToken");
            if (string.IsNullOrEmpty(page)) break;
        }
        return rows;
    }

    /// The same credential the poller already uses for gcloud — no API key, no
    /// extra IAM, nothing for the user to set up.
    private static async Task<string?> AccessTokenAsync(CancellationToken ct)
    {
        var (code, stdout, _) = await CloudPoller.RunAsync("auth print-access-token", 15_000, ct).ConfigureAwait(false);
        return code == 0 ? stdout.Trim() : null;
    }

    private static List<SkuRow>? ReadCache()
    {
        try
        {
            var p = CachePath();
            if (!File.Exists(p)) return null;
            var file = JsonSerializer.Deserialize(File.ReadAllText(p), SkuJsonContext.Default.SkuCacheFile);
            if (file == null || file.Rows.Count == 0) return null;
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(file.FetchedAtUnixMs);
            if (age > Ttl || age < TimeSpan.Zero) return null;
            return file.Rows;
        }
        catch (Exception ex) { Log.Info($"CloudPriceCatalog: cache unreadable ({ex.Message}); refetching"); return null; }
    }

    private static void WriteCache(List<SkuRow> rows)
    {
        try
        {
            var p = CachePath();
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var file = new SkuCacheFile(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rows);
            File.WriteAllText(p, JsonSerializer.Serialize(file, SkuJsonContext.Default.SkuCacheFile));
        }
        catch (Exception ex) { Log.Info($"CloudPriceCatalog: cache write skipped ({ex.Message})"); }
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
