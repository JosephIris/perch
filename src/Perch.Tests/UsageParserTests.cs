using System;
using Xunit;

namespace Perch.Tests;

// The usage-endpoint success schema is UNVERIFIED (it 429s today), so the parser
// is written to survive whatever shape it eventually returns. These tests feed it
// several plausible shapes plus outright garbage and assert it never throws and
// resolves models sensibly. If the real schema turns out different, the parser's
// "walk for anything bucket-shaped" strategy should still cope — and a new shape
// gets a case added here.
public class UsageParserTests
{
    // ---- Flat buckets keyed by model (seven_day_opus / seven_day_sonnet) ----

    [Fact]
    public void FlatBuckets_MapOpusAndSonnetByKey()
    {
        var json = """
        {
          "five_hour":         { "utilization": 0.4 },
          "seven_day":         { "utilization": 0.9 },
          "seven_day_opus":    { "utilization": 1.0, "resets_at": 1893456000 },
          "seven_day_sonnet":  { "utilization": 0.5 }
        }
        """;
        var snap = UsageParser.Parse(json);

        var opus = snap.ForModel("opus");
        Assert.NotNull(opus);
        Assert.True(opus!.Value.AtLimit);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000), opus.Value.ResetsAt);

        var sonnet = snap.ForModel("sonnet");
        Assert.NotNull(sonnet);
        Assert.False(sonnet!.Value.AtLimit);           // 50% → not at limit
    }

    [Fact]
    public void AggregateBuckets_NeverDisableAModel()
    {
        // five_hour / seven_day are account-wide (no model token) — even maxed,
        // they must NOT disable opus/sonnet/fable.
        var json = """
        { "five_hour": { "utilization": 1.0 }, "seven_day": { "utilization": 1.0 } }
        """;
        var snap = UsageParser.Parse(json);
        Assert.Null(snap.ForModel("opus"));
        Assert.Null(snap.ForModel("sonnet"));
        Assert.Null(snap.ForModel("fable"));
    }

    // ---- Nested list of { model, used_percentage } --------------------------

    [Fact]
    public void NestedList_MapsByModelLabel_AndPercentNormalizes()
    {
        var json = """
        {
          "buckets": [
            { "model": "opus",   "used_percentage": 100 },
            { "model": "sonnet", "used_percentage": 42 },
            { "display_name": "Fable", "used_percentage": 150 }
          ]
        }
        """;
        var snap = UsageParser.Parse(json);

        Assert.True(snap.ForModel("opus")!.Value.AtLimit);       // 100% → limit
        Assert.False(snap.ForModel("sonnet")!.Value.AtLimit);    // 42% → fine
        // fable matched by display_name; 150% clamps as ≥100.
        Assert.True(snap.ForModel("fable")!.Value.AtLimit);
    }

    // ---- Fraction vs percent for utilization --------------------------------

    [Theory]
    [InlineData("1.0", true)]    // fraction exactly 1 → 100%
    [InlineData("0.99", false)]  // fraction just under
    [InlineData("100", true)]    // percent form
    [InlineData("99", false)]    // percent just under
    [InlineData("100.0", true)]
    public void UtilizationValue_NormalizesFractionOrPercent(string value, bool expectAtLimit)
    {
        var json = $"{{ \"seven_day_opus\": {{ \"utilization\": {value} }} }}";
        var snap = UsageParser.Parse(json);
        Assert.Equal(expectAtLimit, snap.ForModel("opus")!.Value.AtLimit);
    }

    // ---- resets_at: ISO vs epoch (seconds / ms) -----------------------------

    [Fact]
    public void ResetsAt_Iso8601Parses()
    {
        var json = """
        { "seven_day_opus": { "utilization": 1.0, "resets_at": "2030-01-01T14:30:00Z" } }
        """;
        var reset = UsageParser.Parse(json).ForModel("opus")!.Value.ResetsAt;
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 14, 30, 0, TimeSpan.Zero), reset);
    }

    [Fact]
    public void ResetsAt_EpochSecondsAndMillis_BothResolve()
    {
        const long secs = 1_893_456_000;             // ~2030, 10 digits → seconds
        var sJson = $"{{ \"seven_day_opus\": {{ \"utilization\": 1.0, \"resets_at\": {secs} }} }}";
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(secs),
            UsageParser.Parse(sJson).ForModel("opus")!.Value.ResetsAt);

        var ms = secs * 1000L;                        // 13 digits → milliseconds
        var mJson = $"{{ \"seven_day_opus\": {{ \"utilization\": 1.0, \"resets_at\": {ms} }} }}";
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(ms),
            UsageParser.Parse(mJson).ForModel("opus")!.Value.ResetsAt);
    }

    [Fact]
    public void AtLimit_WithNoResetsAt_IsStillAtLimit()
    {
        var json = """{ "seven_day_opus": { "utilization": 1.0 } }""";
        var opus = UsageParser.Parse(json).ForModel("opus");
        Assert.True(opus!.Value.AtLimit);
        Assert.Null(opus.Value.ResetsAt);
    }

    [Fact]
    public void ResetsAt_Garbage_IsIgnoredNotThrown()
    {
        var json = """{ "seven_day_opus": { "utilization": 1.0, "resets_at": "not-a-date" } }""";
        var opus = UsageParser.Parse(json).ForModel("opus");
        Assert.True(opus!.Value.AtLimit);
        Assert.Null(opus.Value.ResetsAt);
    }

    // ---- Haiku / default are never model-scoped -----------------------------

    [Fact]
    public void HaikuAndDefault_AlwaysResolveNull()
    {
        var json = """{ "seven_day_opus": { "utilization": 1.0 } }""";
        var snap = UsageParser.Parse(json);
        Assert.Null(snap.ForModel("haiku"));
        Assert.Null(snap.ForModel(""));
        Assert.Null(snap.ForModel("default"));
    }

    // ---- Garbage / null / empty degrade to an empty snapshot ----------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"error\": { \"type\": \"rate_limit_error\" } }")]
    [InlineData("42")]
    [InlineData("null")]
    public void GarbageOrEmpty_YieldsEmptySnapshot_NeverThrows(string? json)
    {
        var snap = UsageParser.Parse(json);
        Assert.Empty(snap.Buckets);
        Assert.Null(snap.ForModel("opus"));
        Assert.Null(snap.ForModel("fable"));
    }

    [Fact]
    public void BucketWithoutUtilization_IsNotABucket()
    {
        // A node that merely mentions a model but has no utilization / percentage
        // isn't a rate-limit bucket and must not resolve to one.
        var json = """{ "seven_day_opus": { "resets_at": 1893456000 } }""";
        Assert.Null(UsageParser.Parse(json).ForModel("opus"));
    }
}
