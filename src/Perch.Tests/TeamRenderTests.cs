using System.Collections.Generic;
using System.Text;
using Xunit;

namespace Perch.Tests;

/// The prose Perch hands to Claude on a team's behalf. These pin the wire
/// contract inside that prose — the SendMessage address form, the
/// `[Perch team]` prefix, the `perch team post` verb, the six brief headings —
/// because a bot that is told the wrong verb is a bot that cannot reach anyone.
public class TeamRenderTests
{
    private static TeamDoc Team(int bots)
    {
        var doc = new TeamDoc();
        doc.Positions.Add(new TeamPosition
        {
            Slug = "frontend-dev", Name = "Frontend dev",
            Purpose = "Owns everything under src/web: the sidebar, the panes, the dialogs, and the CSS tokens.",
        });
        doc.Positions.Add(new TeamPosition
        {
            Slug = "backend-dev", Name = "Backend dev",
            Purpose = "Owns the WPF host, the IPC pipes, the hook handler and the CLI.",
        });
        var names = new[] { "Ada", "Bo", "Cy", "Dee", "Eve" };
        for (var i = 0; i < bots; i++)
        {
            doc.Bots.Add(new TeamBot
            {
                Slug = names[i].ToLowerInvariant(), Nickname = names[i],
                CcName = names[i].ToLowerInvariant(),
                PositionSlug = i % 2 == 0 ? "frontend-dev" : "backend-dev",
            });
        }
        return doc;
    }

    [Fact]
    public void Roster_NamesEveryAddress_AndTheThreeWaysToTalk()
    {
        var roster = TeamRender.Roster(Team(3), "perch",
            new Dictionary<string, string> { ["ada"] = "working", ["cy"] = "asleep" },
            modelLimits: null,
            // A teammate is addressed by ADDRESS, not by name: several sessions
            // can answer to "bo" (an earlier run's, another machine's) and the
            // send then fails outright, which is how a hand-off went missing.
            addresses: new Dictionary<string, string> { ["bo"] = @"uds:\\.\pipe\LOCAL\cc-msg-7d21" });
        Assert.Contains("Ada (session name `ada`) — Frontend dev", roster);
        Assert.Contains(@"Bo (session name `bo`, address `uds:\\.\pipe\LOCAL\cc-msg-7d21`) — Backend dev", roster);
        Assert.Contains("`to` = the ADDRESS beside their name above, never the nickname", roster);
        Assert.Contains("Cy (session name `cy`)", roster);
        Assert.Contains("[working]", roster);
        Assert.Contains("[asleep]", roster);
        Assert.Contains("SendMessage", roster);
        Assert.Contains("perch team post", roster);
        // A post to everyone is for whoever it concerns; the others say so
        // in the one form the room knows to drop.
        Assert.Contains("`→ @everyone`", roster);
        Assert.Contains("`(no reply)`", roster);
        Assert.Contains(TeamRender.PostPrefix, roster);
        Assert.Contains("One owner per piece", roster);
        // Milestone B: explicit handoffs, one-note intros, reactions, asks,
        // screenshots, the memory rule — the verbs the room understands.
        foreach (var k in new[] { "`HANDOFF:`", "`REPORT:`", "`QUESTION:`", "`ANSWER:`", "`FYI:`" }) Assert.Contains(k, roster);
        Assert.Contains("post ONE note to the room of at most two lines", roster);
        Assert.Contains("Never introduce yourself by messaging teammates", roster);
        Assert.Contains("at most six lines", roster);
        Assert.Contains("perch team react #<n> <emoji>", roster);
        Assert.Contains("perch team react @<nick> <emoji>", roster);
        foreach (var e in new[] { "✅", "👀", "✏️", "👋" }) Assert.Contains(e, roster);
        Assert.Contains("perch team ask \"<question>\" [--choices \"A|B\"]", roster);
        Assert.Contains("perch team post --image <path>", roster);
        Assert.Contains("`#<n>` after the prefix is that post's number", roster);
        Assert.Contains("details below a `---` line", roster);
    }

    [Fact]
    public void Roster_StaysSmall_ForAFiveBotTeam()
    {
        var roster = TeamRender.Roster(Team(5), "perch");
        Assert.True(Encoding.UTF8.GetByteCount(roster) < 4096, $"roster is {Encoding.UTF8.GetByteCount(roster)} bytes");
    }

    [Fact]
    public void Roster_WithNoBots_SaysSo()
    {
        var roster = TeamRender.Roster(new TeamDoc(), "perch");
        Assert.Contains("(No bots yet.)", roster);
    }

    [Fact]
    public void SystemPrompt_LeadsWithIdentity_ThenTheBriefVerbatim()
    {
        var doc = Team(1);
        var text = TeamRender.SystemPrompt(doc.Bots[0], doc.Positions[0], "## Role\nYou own src/web.\n", "perch");
        Assert.StartsWith("# You are Ada, the Frontend dev on the perch team", text);
        Assert.Contains("session name is `ada`", text);
        Assert.Contains("Your purpose, in the owner's words: Owns everything under src/web", text);
        Assert.Contains("## Your standing brief", text);
        Assert.Contains("## Role\nYou own src/web.", text);
    }

    [Fact]
    public void SystemPrompt_WithoutABrief_TellsTheBotToWorkFromThePurpose()
    {
        var doc = Team(1);
        var text = TeamRender.SystemPrompt(doc.Bots[0], doc.Positions[0], "", "perch");
        Assert.Contains("No brief has been written", text);
    }

    [Fact]
    public void BriefPrompt_HasAllSixSections_InOrder()
    {
        var doc = Team(0);
        var prompt = TeamRender.BriefPrompt(doc.Positions[0], "perch");
        var last = -1;
        foreach (var h in TeamRender.BriefHeadings)
        {
            var at = prompt.IndexOf(h, System.StringComparison.Ordinal);
            Assert.True(at > last, $"missing or out of order: {h}");
            last = at;
        }
        Assert.Equal(6, TeamRender.BriefHeadings.Length);
        Assert.Contains("Position: Frontend dev", prompt);
        Assert.Contains("Purpose, in the owner's words: Owns everything under src/web", prompt);
        Assert.Contains("read-only", prompt);
        Assert.Contains("700 words", prompt);
    }

    [Fact]
    public void Context_IsTheRosterThenTheBotsOwnMemory_WithTheRule()
    {
        var doc = Team(2);
        var roster = TeamRender.Roster(doc, "perch");
        var bot = doc.Bots[0];
        var path = @"C:\repo\.perch\team\bots\ada\memory.md";

        var empty = TeamRender.Context(roster, bot, "", path);
        Assert.StartsWith("# Team roster — perch", empty);
        Assert.Contains("# Your memory", empty);
        Assert.Contains("`" + path + "`", empty);
        Assert.Contains("Keep a short summary on top and the details below a line that is exactly `---`", empty);
        Assert.EndsWith("(Empty so far.)\n", empty);

        var full = TeamRender.Context(roster, bot, "- The sidebar is mine.\n- Bo owns the API.", path);
        Assert.EndsWith("- The sidebar is mine.\n- Bo owns the API.\n", full);
        Assert.DoesNotContain("(Empty so far.)", full);

        var seed = TeamRender.MemorySeed(bot);
        Assert.StartsWith("# Ada — memory", seed);

        var system = TeamRender.SystemPrompt(bot, doc.Positions[0], "## Role\nYou own src/web.", "perch", path);
        Assert.Contains("You have a memory file, `" + path + "`", system);
        Assert.Contains("arrive with every prompt", system);
    }

    [Fact]
    public void TaskBlock_SpeaksToTheLeadAndToAMember_WithAndWithoutATask()
    {
        var doc = Team(2);
        doc.LeadSlug = "ada";
        var tasks = new TaskDoc();

        var leadEmpty = TeamRender.TaskBlock(tasks, doc, doc.Bots[0]);
        Assert.Contains("(No task open. You are the lead", leadEmpty);
        Assert.Contains("perch team task new", leadEmpty);
        var memberEmpty = TeamRender.TaskBlock(tasks, doc, doc.Bots[1]);
        Assert.Contains("Ada leads and opens tasks", memberEmpty);
        Assert.DoesNotContain("perch team task done", memberEmpty);

        tasks.Open.Add(new TaskBoard
        {
            Id = "t1", Title = "Ship the sidebar", Status = "review", SetBy = "ada",
            Items = { new TaskItem { Bot = "bo", Title = "API for the room", Status = "done", Note = "merged" } },
        });
        tasks.Open.Add(new TaskBoard { Id = "t2", Title = "Dark footer", Status = "open", SetBy = "you" });
        var member = TeamRender.TaskBlock(tasks, doc, doc.Bots[1]);
        Assert.Contains("- Task t1: **Ship the sidebar** — review (waiting for Joseph to confirm)", member);
        Assert.Contains("  - Bo (you): [done] API for the room — merged", member);
        Assert.Contains("- Task t2: **Dark footer** — open", member);
        Assert.Contains("  - (no pieces yet)", member);
        Assert.Contains("Ada (`ada`) leads", member);
        Assert.Contains("perch team task mine <id>", member);
        var lead = TeamRender.TaskBlock(tasks, doc, doc.Bots[0]);
        Assert.Contains("perch team task assign <id> <session name>", lead);
        Assert.Contains("perch team task done <id>", lead);

        var system = TeamRender.SystemPrompt(doc.Bots[0], doc.Positions[0], "## Role\nYou own src/web.", "perch", null, isLead: true);
        Assert.Contains("and the team lead on the perch team", system);
        Assert.Contains("## You lead the team", system);
        Assert.Contains("perch team task done <id>", system);
        Assert.Contains("Never wait for him to agree first", system);
        Assert.Contains("several may be open at once", system);
    }

    [Fact]
    public void OneLine_FlattensAndCaps()
    {
        Assert.Equal("", TeamRender.OneLine("  \n ", 10));
        Assert.Equal("a b c", TeamRender.OneLine("a\r\nb\n\nc", 10));
        Assert.Equal("abcde…", TeamRender.OneLine("abcdefghij", 5));
    }
}
