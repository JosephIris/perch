using System;
using System.IO;
using Xunit;

namespace Perch.Tests;

/// The team folder. Two properties carry the weight: a team survives a
/// round-trip through disk with every field intact, and Perch never saves over
/// a team file it could not read.
public class TeamStoreTests
{
    private static string TempRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "perch-teamtest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Open_ReturnsNull_WhenTheProjectHasNoTeam()
    {
        var repo = TempRepo();
        Assert.Null(TeamStore.Open(repo));
        Assert.Null(TeamStore.Open(""));
    }

    [Fact]
    public void Create_MakesTheFolder_WritesGitIgnore_AndIsIdempotent()
    {
        var repo = TempRepo();
        var store = TeamStore.Create(repo);
        Assert.True(Directory.Exists(store.Dir));
        Assert.True(File.Exists(store.JsonPath));
        Assert.True(File.Exists(Path.Combine(repo, ".perch", ".gitignore")));
        Assert.Equal(Path.Combine(repo, ".perch", "team"), store.Dir);

        var again = TeamStore.Create(repo);
        Assert.Equal(store.Dir, again.Dir);
        Assert.Empty(again.Doc.Bots);
    }

    [Fact]
    public void PositionsAndBots_RoundTripThroughDisk()
    {
        var repo = TempRepo();
        var store = TeamStore.Create(repo);
        var pos = store.AddPosition("Frontend dev", "Owns the web chrome.", "", "sonnet");
        var bot = store.AddBot("Ada", pos.Slug, "ada", worktree: true, model: "");
        bot.SessionId = Guid.NewGuid();
        store.Save();

        var back = TeamStore.Open(repo)!;
        Assert.True(back.Readable);
        var p = Assert.Single(back.Doc.Positions);
        Assert.Equal("frontend-dev", p.Slug);
        Assert.Equal("Frontend dev", p.Name);
        Assert.Equal("Owns the web chrome.", p.Purpose);
        Assert.Equal(repo, p.ReferenceRepo);          // empty reference → the project itself
        Assert.Equal("sonnet", p.Model);
        var b = Assert.Single(back.Doc.Bots);
        Assert.Equal("ada", b.Slug);
        Assert.Equal("Ada", b.Nickname);
        Assert.Equal("ada", b.CcName);
        Assert.Equal(bot.SessionId, b.SessionId);
        Assert.True(b.Worktree);
        Assert.Same(b, back.Doc.BotBySession(bot.SessionId!.Value));
        Assert.Same(b, back.Doc.BotByCcName("ADA"));
    }

    [Fact]
    public void Slugs_AreDeduplicatedWithinTheirKind()
    {
        var repo = TempRepo();
        var store = TeamStore.Create(repo);
        var a = store.AddPosition("Frontend dev", "", "", "");
        var b = store.AddPosition("Frontend dev", "", "", "");
        Assert.Equal("frontend-dev", a.Slug);
        Assert.Equal("frontend-dev-2", b.Slug);

        var x = store.AddBot("Ada", a.Slug, "ada", true, "");
        var y = store.AddBot("Ada", b.Slug, "ada-2", true, "");
        Assert.Equal("ada", x.Slug);
        Assert.Equal("ada-2", y.Slug);

        // A name with no alphanumerics falls back rather than producing "".
        var z = store.AddBot("!!!", a.Slug, "bot", true, "");
        Assert.Equal("bot", z.Slug);
    }

    [Fact]
    public void RemovePosition_IsRefused_WhileABotHoldsIt()
    {
        var repo = TempRepo();
        var store = TeamStore.Create(repo);
        var pos = store.AddPosition("Analyst", "", "", "");
        store.AddBot("Cy", pos.Slug, "cy", false, "");
        Assert.False(store.RemovePosition(pos.Slug));
        Assert.True(store.RemoveBot("cy"));
        Assert.True(store.RemovePosition(pos.Slug));
        Assert.Empty(store.Doc.Positions);
    }

    [Fact]
    public void Brief_IsAFileBesideThePosition_NotJson()
    {
        var repo = TempRepo();
        var store = TeamStore.Create(repo);
        var pos = store.AddPosition("Backend dev", "", "", "");
        Assert.Equal("", store.ReadBrief(pos.Slug));
        store.WriteBrief(pos.Slug, "## Role\nYou own the API.\n");
        Assert.Equal("## Role\nYou own the API.\n", store.ReadBrief(pos.Slug));
        Assert.True(File.Exists(Path.Combine(store.Dir, "positions", "backend-dev", "brief.md")));
        store.Save();
        Assert.DoesNotContain("You own the API", File.ReadAllText(store.JsonPath));
    }

    [Fact]
    public void RenderSystemFiles_WritesIdentityThenBrief_ForEveryBot()
    {
        var repo = TempRepo();
        var store = TeamStore.Create(repo);
        var pos = store.AddPosition("Frontend dev", "Owns the web chrome.", "", "");
        store.WriteBrief(pos.Slug, "## Role\nYou own src/web.");
        store.AddBot("Ada", pos.Slug, "ada", true, "");
        store.AddBot("Bo", pos.Slug, "bo", true, "");
        store.RenderSystemFiles("perch");

        var ada = File.ReadAllText(store.SystemPathFor("ada"));
        Assert.StartsWith("# You are Ada, the Frontend dev on the perch team", ada);
        Assert.Contains("session name is `ada`", ada);
        Assert.Contains("You own src/web.", ada);
        var bo = File.ReadAllText(store.SystemPathFor("bo"));
        Assert.StartsWith("# You are Bo, the Frontend dev on the perch team", bo);
    }

    [Fact]
    public void RenderRoster_ListsEveryAddress()
    {
        var repo = TempRepo();
        var store = TeamStore.Create(repo);
        var pos = store.AddPosition("Frontend dev", "Owns the web chrome.", "", "");
        store.AddBot("Ada", pos.Slug, "ada", true, "");
        store.AddBot("Bo", pos.Slug, "bo-2", true, "");
        store.RenderRoster("perch");
        var roster = File.ReadAllText(store.RosterPath);
        Assert.Contains("`ada`", roster);
        Assert.Contains("`bo-2`", roster);
        Assert.Contains("Frontend dev", roster);
    }

    [Fact]
    public void Save_IsRefused_WhenTheTeamFileIsUnreadable()
    {
        var repo = TempRepo();
        var store = TeamStore.Create(repo);
        File.WriteAllText(store.JsonPath, "{ this is not json");

        var broken = TeamStore.Open(repo)!;
        Assert.False(broken.Readable);
        Assert.NotEqual("", broken.Problem);
        broken.AddBot("Ada", "x", "ada", true, "");
        broken.Save();
        Assert.Equal("{ this is not json", File.ReadAllText(store.JsonPath));
    }

    [Fact]
    public void Save_IsRefused_WhenWrittenByANewerPerch()
    {
        var repo = TempRepo();
        var store = TeamStore.Create(repo);
        File.WriteAllText(store.JsonPath, "{\"v\":2,\"positions\":[],\"bots\":[]}");
        var newer = TeamStore.Open(repo)!;
        Assert.False(newer.Readable);
        Assert.Contains("newer version", newer.Problem);
    }

    [Fact]
    public void RemoveBot_DropsItsRenderedFolder()
    {
        var repo = TempRepo();
        var store = TeamStore.Create(repo);
        var pos = store.AddPosition("QA", "", "", "");
        store.AddBot("Dee", pos.Slug, "dee", false, "");
        store.RenderSystemFiles("perch");
        Assert.True(File.Exists(store.SystemPathFor("dee")));
        Assert.True(store.RemoveBot("dee"));
        Assert.False(Directory.Exists(Path.Combine(store.BotsDir, "dee")));
        Assert.False(store.RemoveBot("dee"));
    }
}
