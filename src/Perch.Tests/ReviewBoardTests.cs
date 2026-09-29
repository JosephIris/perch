using System.IO;
using System.Text.Json;
using Xunit;

namespace Perch.Tests;

// A project chat's board: `perch thread board …` keeps one card per item
// under review in the chat's folder, and the page draws them in columns.
[Collection("Data dir")]
public class ReviewBoardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "perch-board-" + Guid.NewGuid().ToString("N"));
    private readonly string? _prevData = Environment.GetEnvironmentVariable("PERCH_DATA_DIR");

    public ReviewBoardTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("PERCH_DATA_DIR", Path.Combine(_root, "data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PERCH_DATA_DIR", _prevData);
        try { Directory.Delete(_root, true); } catch { }
    }

    private static string Fields(object o) => JsonSerializer.Serialize(o);

    [Theory]
    [InlineData("VERIFIED", "ok")]
    [InlineData("not-working", "bad")]
    [InlineData("NOT WORKING", "bad")]
    [InlineData("can't-verify", "manual")]
    [InlineData("manual", "manual")]
    [InlineData("pending", "pending")]
    [InlineData("", "checking")]
    public void Tone_TakesTheWordsAChatUses(string word, string tone) => Assert.Equal(tone, ReviewBoard.Tone(word));

    [Fact]
    public void Tone_RefusesAnUnknownWord() => Assert.Null(ReviewBoard.Tone("maybe"));

    [Fact]
    public void Set_AddsThenUpdatesOnlyTheFieldsGiven_AndTakesTheThreadItCameFrom()
    {
        var lead = new Session { IsLead = true };
        var (r1, c1) = ReviewBoard.Run(lead, 4, "set", "PK-1", Fields(new Dictionary<string, string>
            { ["title"] = "CTV out of NN", ["verdict"] = "checking" }));
        Assert.StartsWith("ok\nAdded PK-1", r1);
        Assert.True(c1);

        var (r2, _) = ReviewBoard.Run(lead, 4, "set", "pk-1", Fields(new Dictionary<string, string>
            { ["verdict"] = "verified", ["status"] = "Verified", ["question"] = "Deployed yet?" }));
        Assert.StartsWith("ok\nUpdated", r2);

        var it = Assert.Single(ReviewBoard.Load(lead).Items);
        Assert.Equal(("PK-1", "CTV out of NN", "ok", "Verified", "Deployed yet?", 4),
            (it.Key, it.Title, it.Verdict, it.Status, it.Question, it.Thread));

        ReviewBoard.Run(lead, 0, "set", "PK-1", Fields(new Dictionary<string, string> { ["question"] = "" }));
        Assert.Equal("", ReviewBoard.Load(lead).Items[0].Question);
    }

    [Fact]
    public void Set_RefusesABadVerdictOrField_AndChangesNothing()
    {
        var lead = new Session { IsLead = true };
        var (r1, c1) = ReviewBoard.Run(lead, 0, "set", "PK-1", Fields(new Dictionary<string, string> { ["verdict"] = "maybe" }));
        Assert.StartsWith("error", r1);
        Assert.False(c1);
        var (r2, _) = ReviewBoard.Run(lead, 0, "set", "PK-1", Fields(new Dictionary<string, string> { ["colour"] = "red" }));
        Assert.StartsWith("error", r2);
        Assert.Empty(ReviewBoard.Load(lead).Items);
    }

    [Fact]
    public void View_CarriesTheDraftsText_ReadFresh_AndIsNullWhenEmpty()
    {
        var lead = new Session { IsLead = true };
        Assert.Null(ReviewBoard.View(lead));

        var draft = Path.Combine(_root, "PK-1.md");
        File.WriteAllText(draft, "**Verified on build 1.584**");
        ReviewBoard.Run(lead, 0, "set", "PK-1", Fields(new Dictionary<string, string> { ["draft"] = draft, ["verdict"] = "not-working" }));
        ReviewBoard.Run(lead, 0, "title", "Build 1.584", Fields(new Dictionary<string, string> { ["summary"] = "9 tickets" }));

        var json = JsonSerializer.Serialize(ReviewBoard.View(lead));
        Assert.Contains("Verified on build 1.584", json);
        Assert.Contains("\"label\":\"Not working\"", json);
        Assert.Contains("\"title\":\"Build 1.584\"", json);

        File.WriteAllText(draft, "**Not working on build 1.584**");
        Assert.Contains("Not working on build 1.584", JsonSerializer.Serialize(ReviewBoard.View(lead)));
    }

    [Fact]
    public void RemoveAndClear()
    {
        var lead = new Session { IsLead = true };
        ReviewBoard.Run(lead, 0, "set", "A", "{}");
        ReviewBoard.Run(lead, 0, "set", "B", "{}");
        Assert.StartsWith("ok", ReviewBoard.Run(lead, 0, "remove", "a", null).Reply);
        Assert.StartsWith("error", ReviewBoard.Run(lead, 0, "remove", "a", null).Reply);
        Assert.Contains("B —", ReviewBoard.Run(lead, 0, "show", "", null).Reply);
        ReviewBoard.Run(lead, 0, "clear", "", null);
        Assert.Null(ReviewBoard.View(lead));
    }

    [Fact]
    public void Prompts_TellTheChatAndItsThreadsHowToUseTheBoard()
    {
        var proj = new Project { Name = "demo", Path = _root };
        Assert.Contains("perch thread board set <key>", ThreadController.CoordinatorPrompt(proj));
        Assert.Contains("approve <key>", ThreadController.CoordinatorPrompt(proj));
        Assert.Contains("perch thread board set <key>", ThreadController.ThreadPrompt(1, "t", "brief"));
    }
}
