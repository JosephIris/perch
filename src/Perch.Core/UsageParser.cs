using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Perch;

/// One rate-limit bucket pulled out of the usage payload. Fraction is the
/// utilization normalized to a share of 1 (so 1.0 == 100%). Key concatenates
/// the bucket's parent property name and any model-ish label it carries, so a
/// substring test can find the model token wherever the schema hid it.
internal readonly record struct UsageBucket(string Key, double Fraction, DateTimeOffset? ResetsAt)
{
    /// ≥100% utilization → the model is out of budget.
    public bool AtLimit => Fraction >= 1.0;
}

/// A model's resolved limit: whether it's maxed and when it resets.
internal readonly record struct ModelLimit(bool AtLimit, DateTimeOffset? ResetsAt);

/// The buckets found in one usage payload, with per-model resolution.
internal sealed class UsageSnapshot
{
    public IReadOnlyList<UsageBucket> Buckets { get; }
    public UsageSnapshot(IReadOnlyList<UsageBucket> buckets) => Buckets = buckets;

    public static readonly UsageSnapshot Empty = new(Array.Empty<UsageBucket>());

    /// Resolve a CLI alias to its model-scoped bucket, or null when there's no
    /// matching bucket (→ the model is never disabled). Mapping (feature spec):
    ///   fable  → any bucket labeled "fable"
    ///   opus   → seven_day_opus  (any bucket whose key/label carries "opus")
    ///   sonnet → seven_day_sonnet
    ///   haiku / default / unknown → none
    /// The aggregate five_hour / seven_day buckets carry no model token, so a
    /// substring match never catches them — models are never disabled off the
    /// account-wide buckets.
    public ModelLimit? ForModel(string? alias)
    {
        var token = (alias ?? "").Trim().ToLowerInvariant() switch
        {
            "fable"  => "fable",
            "opus"   => "opus",
            "sonnet" => "sonnet",
            _        => null,
        };
        if (token == null) return null;
        foreach (var b in Buckets)
            if (b.Key.Contains(token, StringComparison.OrdinalIgnoreCase))
                return new ModelLimit(b.AtLimit, b.ResetsAt);
        return null;
    }
}

/// Parses the (unverified) success schema of the Claude OAuth usage endpoint
/// into per-model rate-limit buckets, defensively. The endpoint currently
/// always 429s, so this runs against a schema we've never seen live — it walks
/// the JSON for ANY node that looks like a bucket rather than keying on a fixed
/// path, and every helper degrades to "no data" on anything unexpected instead
/// of throwing.
///
/// A bucket = an object carrying `utilization` OR `used_percentage` (a number),
/// optionally `resets_at` (epoch seconds/ms or ISO 8601).
internal static class UsageParser
{
    public static UsageSnapshot Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return UsageSnapshot.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var buckets = new List<UsageBucket>();
            Walk(doc.RootElement, parentKey: "", buckets);
            return new UsageSnapshot(buckets);
        }
        catch { return UsageSnapshot.Empty; }
    }

    private static void Walk(JsonElement el, string parentKey, List<UsageBucket> acc)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryBucket(el, parentKey, out var b)) acc.Add(b);
                foreach (var prop in el.EnumerateObject())
                    Walk(prop.Value, prop.Name, acc);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    // List items inherit the array's key (e.g. "buckets"); a
                    // labeled item (model/name/…) overrides it in TryBucket.
                    Walk(item, parentKey, acc);
                break;
        }
    }

    private static bool TryBucket(JsonElement obj, string parentKey, out UsageBucket bucket)
    {
        bucket = default;
        var frac = ReadFraction(obj, "utilization") ?? ReadFraction(obj, "used_percentage");
        if (frac == null) return false;
        var label = ReadString(obj, "model") ?? ReadString(obj, "name")
                    ?? ReadString(obj, "display_name") ?? ReadString(obj, "label");
        // Identity = parent property name (flat shape { seven_day_opus: {…} })
        // plus any label (nested-list shape { model:"opus", … }). Concatenate
        // both so ForModel's substring test finds the token wherever it lives.
        var key = string.Join(
            " ",
            new[] { parentKey, label }.Where(s => !string.IsNullOrEmpty(s)));
        bucket = new UsageBucket(key, frac.Value, ReadResetsAt(obj));
        return true;
    }

    /// Read a utilization number and normalize to a 0..1+ fraction: values ≤ 1
    /// are already a fraction of 1; larger values are a percentage (÷100).
    private static double? ReadFraction(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        double raw;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (!v.TryGetDouble(out raw)) return null;
        }
        else if (v.ValueKind == JsonValueKind.String
                 && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out raw))
        {
            // parsed
        }
        else return null;
        if (double.IsNaN(raw) || double.IsInfinity(raw) || raw < 0) return null;
        return raw <= 1.0 ? raw : raw / 100.0;
    }

    private static string? ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString()
           : null;

    /// resets_at may be epoch seconds, epoch ms, or ISO 8601. Null/garbage → null.
    private static DateTimeOffset? ReadResetsAt(JsonElement obj)
    {
        if (!obj.TryGetProperty("resets_at", out var v)) return null;
        try
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var num))
                return FromEpoch(num);
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
                    return FromEpoch(epoch);
                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                    return dto;
            }
        }
        catch { }
        return null;
    }

    /// Epoch → DateTimeOffset. Values past ~1e12 are treated as milliseconds,
    /// smaller ones as seconds (so both a 10- and 13-digit stamp resolve sanely).
    private static DateTimeOffset? FromEpoch(double num)
    {
        if (num <= 0) return null;
        return num > 1e12
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)num)
            : DateTimeOffset.FromUnixTimeSeconds((long)num);
    }
}
