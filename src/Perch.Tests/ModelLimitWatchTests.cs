using System;
using System.Linq;
using Perch;
using Xunit;

namespace Perch.Tests;

/// Reading "you've reached your Fable limit" off a pane's own transcript.
///
/// The transcript line below is the real one, trimmed: it is what Claude Code
/// wrote into the shabtay bot's session on 2026-09-04 when fable ran out, the
/// morning the owner noticed his bots hadn't moved to opus. Nothing else in
/// perch saw it — the account-wide usage endpoint 429s on every call, so the
/// switch that was supposed to fire had no input at all.
public class ModelLimitWatchTests
{
    /// A refusal row as Claude Code writes it. The model field reads
    /// "&lt;synthetic&gt;" — the real model is named only in the prose, which is
    /// why the alias is parsed from there.
    private const string Refusal =
        "{\"type\":\"assistant\",\"timestamp\":\"2026-09-04T06:23:20.215Z\"," +
        "\"message\":{\"model\":\"<synthetic>\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\"," +
        "\"text\":\"You've reached your Fable limit. Run /usage-credits to continue or switch models with /model.\"}]}," +
        "\"error\":\"rate_limit\",\"isApiErrorMessage\":true,\"apiErrorStatus\":429}";

    private const string OrdinaryRow =
        "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-fable-5-1\",\"content\":" +
        "[{\"type\":\"text\",\"text\":\"Working on it — the rate limit code is in UsageService.\"}]}}";

    [Fact]
    public void RefusalNamesTheModelThatWasRefused()
    {
        var w = new ModelLimitWatch();
        Assert.True(w.Ingest(Refusal, fallbackAlias: null));
        Assert.Equal(new[] { "fable" }, w.Current().Select(l => l.Alias));
        Assert.True(w.Current().Single().AtLimit);
        // No reset time is in the payload, and inventing one would put a wrong
        // "until 14:05" in the room.
        Assert.Null(w.Current().Single().ResetsAtMs);
    }

    [Fact]
    public void ProseAboutRateLimitsIsNotARefusal()
    {
        var w = new ModelLimitWatch();
        // An agent DISCUSSING rate limits (this very test file, say) must not
        // mark a model as maxed out — the marker is the row's own error field.
        Assert.False(w.Ingest(OrdinaryRow, fallbackAlias: "fable"));
        Assert.Empty(w.Current());
    }

    [Fact]
    public void AWordingChangeFallsBackToThePanesOwnModel()
    {
        var w = new ModelLimitWatch();
        var reworded = Refusal.Replace("You've reached your Fable limit.", "Usage limit reached.");
        Assert.True(w.Ingest(reworded, fallbackAlias: "opus"));
        Assert.Equal(new[] { "opus" }, w.Current().Select(l => l.Alias));
    }

    [Fact]
    public void AnUnknownModelNameIsIgnoredRatherThanGuessed()
    {
        var w = new ModelLimitWatch();
        var other = Refusal.Replace("your Fable limit", "your Fathom limit");
        Assert.False(w.Ingest(other, fallbackAlias: null));
        Assert.Empty(w.Current());
    }

    [Fact]
    public void TheHoldLapsesSoTheBotGoesBack()
    {
        var t = new DateTimeOffset(2026, 9, 4, 6, 23, 0, TimeSpan.Zero);
        var w = new ModelLimitWatch { Now = () => t };
        w.Ingest(Refusal, null);
        Assert.Single(w.Current());

        // A minute before the hold is up: still held.
        t = t + ModelLimitWatch.Hold - TimeSpan.FromMinutes(1);
        Assert.Single(w.Current());

        // Past it: the model is offered again. If the account is still out of
        // headroom the next refusal re-arms this within one turn.
        t = t + TimeSpan.FromMinutes(2);
        Assert.Empty(w.Current());
    }

    [Fact]
    public void AccountLimitsWinTheMergeBecauseOnlyTheyKnowTheResetTime()
    {
        var account = new[] { new ModelUsageLimit("fable", true, 1_770_000_000_000) };
        var local = new[] { new ModelUsageLimit("fable", true, null), new ModelUsageLimit("opus", true, null) };
        var merged = ModelLimitWatch.Merge(account, local);
        Assert.Equal(2, merged.Count);
        Assert.Equal(1_770_000_000_000, merged.Single(l => l.Alias == "fable").ResetsAtMs);
        Assert.Null(merged.Single(l => l.Alias == "opus").ResetsAtMs);
    }

    [Fact]
    public void NoSignalMeansNoLimits()
    {
        Assert.Empty(new ModelLimitWatch().Current());
        Assert.Empty(ModelLimitWatch.Merge(null, null));
    }
}
