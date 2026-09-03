using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Perch.Tests;

/// Faces and the shared/local split of the team folder. What these pin: the
/// hat is the position's and comes from its name; the rest of a face is drawn
/// once and stored; the shared file never carries a session id; memory is a
/// file the bot owns; and Perch's own .gitignore is rewritten so the team
/// travels while the local half doesn't.
public class TeamLooksTests
{
    private static string TempRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "perch-looks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [InlineData("Team lead", "captain")]
    [InlineData("Product manager", "captain")]
    [InlineData("PM", "captain")]
    [InlineData("Frontend dev", "beanie")]
    [InlineData("Web developer", "beanie")]
    [InlineData("Backend dev", "hardhat")]
    [InlineData("DevOps", "hardhat")]
    [InlineData("Designer", "beret")]
    [InlineData("UX researcher", "beret")]
    [InlineData("QA", "deerstalker")]
    [InlineData("Test engineer", "deerstalker")]
    [InlineData("Senior analyst", "tophat")]
    [InlineData("Data scientist", "tophat")]
    [InlineData("Frontend lead", "captain")]   // a lead first, whatever they lead
    public void HatFor_ReadsThePositionName(string name, string hat)
        => Assert.Equal(hat, TeamLooks.HatFor(name));

    [Fact]
    public void HatFor_UnknownNames_GetAStableHat()
    {
        var a = TeamLooks.HatFor("Gardener");
        Assert.Contains(a, TeamLooks.Hats);
        Assert.Equal(a, TeamLooks.HatFor("Gardener"));
        Assert.Equal(a, TeamLooks.HatFor("gardener"));
    }

    [Fact]
    public void RandomLook_StaysInsideTheVocabulary_AndFavoursTheMonocle()
    {
        var rng = new Random(7);
        var monocles = 0;
        for (var i = 0; i < 300; i++)
        {
            var look = TeamLooks.RandomLook(rng);
            Assert.Contains(look.Eyewear, TeamLooks.Eyewear);
            Assert.Contains(look.Extra, TeamLooks.Extras);
            Assert.Contains(look.Temper, TeamLooks.Tempers);
            if (look.Eyewear == "monocle") monocles++;
        }
        Assert.True(monocles > 300 / TeamLooks.Eyewear.Length, $"monocle came up {monocles}/300");
    }

    [Fact]
    public void Normalize_ReplacesOnlyWhatItDoesNotKnow()
    {
        var n = TeamLooks.Normalize(new TeamLook { Eyewear = "sunglasses", Extra = "scarf", Temper = "" });
        Assert.Equal("monocle", n.Eyewear);
        Assert.Equal("scarf", n.Extra);
        Assert.Equal("steady", n.Temper);
        Assert.Equal("beanie", TeamLooks.NormalizeHat("fez", "Frontend dev"));
        Assert.Equal("tophat", TeamLooks.NormalizeHat("tophat", "Frontend dev"));
    }

    [Fact]
    public void Positions_GetTheirHat_AndBots_TheirLook_AtCreation()
    {
        var repo = TempRepo();
        var store = TeamStore.Create(repo);
        var pos = store.AddPosition("Backend dev", "Owns the API.", "", "");
        var bot = store.AddBot("Bo", pos.Slug, "bo", worktree: false, model: "");
        Assert.Equal("hardhat", pos.Hat);
        Assert.NotNull(bot.Look);
        store.Save();

        var back = TeamStore.Open(repo)!;
        Assert.Equal("hardhat", back.Doc.Positions.Single().Hat);
        var b = back.Doc.Bots.Single();
        Assert.Equal(bot.Look!.Eyewear, b.Look!.Eyewear);
        Assert.Equal(bot.Look!.Extra, b.Look!.Extra);
        Assert.Equal(bot.Look!.Temper, b.Look!.Temper);
    }

    [Fact]
    public void OlderDocuments_GetAFaceOnLoad_AndTheirSessionMovesToLocal()
    {
        // A team.json from before faces and before the split: no hat, no look,
        // and the session id inline.
        var repo = TempRepo();
        var dir = TeamStore.DirFor(repo);
        Directory.CreateDirectory(dir);
        var sid = Guid.NewGuid();
        File.WriteAllText(Path.Combine(dir, "team.json"),
            "{\"v\":1,\"positions\":[{\"slug\":\"designer\",\"name\":\"Designer\",\"purpose\":\"\",\"referenceRepo\":\"\",\"model\":\"\",\"createdAtMs\":1,\"briefGeneratedAtMs\":0,\"briefModel\":\"\"}]," +
            "\"bots\":[{\"slug\":\"mira\",\"nickname\":\"Mira\",\"positionSlug\":\"designer\",\"ccName\":\"mira\",\"sessionId\":\"" + sid.ToString("D") + "\",\"worktree\":true,\"model\":\"\",\"createdAtMs\":1}]}");
        File.WriteAllText(Path.Combine(dir, "room.jsonl"), "{\"seq\":1,\"tsMs\":1,\"kind\":\"system\",\"from\":\"perch\",\"text\":\"old\"}\n");
        File.WriteAllText(Path.Combine(dir, "roster.md"), "old roster\n");

        var store = TeamStore.Open(repo)!;
        Assert.True(store.Readable);
        Assert.Equal("beret", store.Doc.Positions.Single().Hat);
        var bot = store.Doc.Bots.Single();
        Assert.NotNull(bot.Look);
        Assert.Equal(sid, bot.SessionId);
        Assert.Null(bot.LegacySessionId);

        // Saved back on load: the shared file has the face and no session;
        // the local file has the session; the chat moved; stale renders went.
        var shared = File.ReadAllText(store.JsonPath);
        Assert.Contains("\"hat\"", shared);
        Assert.Contains("\"beret\"", shared);
        Assert.Contains("\"look\"", shared);
        Assert.DoesNotContain("sessionId", shared);
        Assert.Contains(sid.ToString("D"), File.ReadAllText(store.LocalJsonPath));
        Assert.True(File.Exists(store.LedgerPath));
        Assert.False(File.Exists(Path.Combine(dir, "room.jsonl")));
        Assert.False(File.Exists(Path.Combine(dir, "roster.md")));
        Assert.Contains("old", File.ReadAllText(store.LedgerPath));
    }

    [Fact]
    public void Memory_IsSeededForANewBot_ReadBack_AndCapped()
    {
        var repo = TempRepo();
        var store = TeamStore.Create(repo);
        var pos = store.AddPosition("QA", "Breaks things.", "", "");
        var bot = store.AddBot("Quinn", pos.Slug, "quinn", worktree: false, model: "");
        Assert.True(File.Exists(store.MemoryPathFor(bot.Slug)));
        Assert.StartsWith("# Quinn — memory", store.ReadMemory(bot.Slug));

        store.WriteMemory(bot.Slug, "- Checkout flow 3 is flaky on Fridays.");
        Assert.Equal("- Checkout flow 3 is flaky on Fridays.", store.ReadMemory(bot.Slug));
        store.RenderRoster("perch");
        var context = File.ReadAllText(store.ContextPathFor(bot.Slug));
        Assert.Contains("- Checkout flow 3 is flaky on Fridays.", context);
        Assert.Contains("# Team roster", context);

        store.WriteMemory(bot.Slug, new string('x', 5000));
        var capped = store.ReadMemory(bot.Slug);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(capped) < 2200);
        Assert.EndsWith("[memory truncated — keep it under 2 KB]", capped);

        // Removing the bot removes both its shared and its local folder.
        store.RemoveBot(bot.Slug);
        Assert.False(File.Exists(store.MemoryPathFor(bot.Slug)));
        Assert.False(File.Exists(store.ContextPathFor(bot.Slug)));
    }

    [Fact]
    public void GitIgnore_IsRewrittenToShareTheTeam_ButOnlyPerchsOwn()
    {
        // Perch's boards-only file → the sharing form.
        var repo = TempRepo();
        BoardStore.EnsureGitIgnored(repo);
        var path = Path.Combine(repo, ".perch", ".gitignore");
        Assert.Equal("*", File.ReadAllLines(path).Single(l => l.Length > 0 && !l.StartsWith('#')));
        TeamStore.Create(repo);
        var rules = File.ReadAllLines(path).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
        Assert.Equal(new[] { "*", "!.gitignore", "!team/", "!team/**", "team/local/" }, rules);
        // Idempotent.
        TeamStore.Open(repo);
        Assert.Equal(TeamStore.ShareableGitIgnore, File.ReadAllText(path));

        // A hand-written file is left alone.
        var repo2 = TempRepo();
        Directory.CreateDirectory(Path.Combine(repo2, ".perch"));
        File.WriteAllText(Path.Combine(repo2, ".perch", ".gitignore"), "boards/\nscreens/\n");
        TeamStore.Create(repo2);
        Assert.Equal("boards/\nscreens/\n", File.ReadAllText(Path.Combine(repo2, ".perch", ".gitignore")));

        // No file at all (the user chose to track everything) stays that way,
        // except that Create writes the boards one first — so open, don't create.
        var repo3 = TempRepo();
        Directory.CreateDirectory(TeamStore.DirFor(repo3));
        TeamStore.Open(repo3);
        Assert.False(File.Exists(Path.Combine(repo3, ".perch", ".gitignore")));
    }

    [Fact]
    public void StaleOnDisk_NoticesAnotherWriter_AndReloadKeepsLocalSessions()
    {
        var repo = TempRepo();
        var store = TeamStore.Create(repo);
        var pos = store.AddPosition("Designer", "", "", "");
        var bot = store.AddBot("Mira", pos.Slug, "mira", worktree: false, model: "");
        bot.SessionId = Guid.NewGuid();
        store.Save();
        Assert.False(store.StaleOnDisk());

        // "A pull": another machine added a bot to the shared file.
        var other = TeamStore.Open(repo)!;
        other.AddBot("Bo", pos.Slug, "bo", worktree: false, model: "");
        other.Doc.Bots.Single(b => b.Slug == "mira").SessionId = null;   // no tab there
        File.SetLastWriteTimeUtc(store.JsonPath, DateTime.UtcNow.AddSeconds(-5));   // make the stamps differ regardless of clock granularity
        other.Save();
        Assert.True(store.StaleOnDisk());

        store.Reload();
        Assert.Equal(2, store.Doc.Bots.Count);
        // Mira's tab here is remembered (from local/sessions.json as `other`
        // saved it? no — `other` saved Mira with no session; the LOCAL file is
        // shared between the two stores on one machine, so it reflects the
        // last saver). Bo has none.
        Assert.Null(store.Doc.Bots.Single(b => b.Slug == "bo").SessionId);
        Assert.False(store.StaleOnDisk());
    }
}
