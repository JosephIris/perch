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
            new Dictionary<string, string> { ["ada"] = "working", ["cy"] = "asleep" });
        Assert.Contains("Ada (session name `ada`) — Frontend dev", roster);
        Assert.Contains("Bo (session name `bo`) — Backend dev", roster);
        Assert.Contains("Cy (session name `cy`)", roster);
        Assert.Contains("[working]", roster);
        Assert.Contains("[asleep]", roster);
        Assert.Contains("SendMessage", roster);
        Assert.Contains("perch team post", roster);
        Assert.Contains(TeamRender.PostPrefix, roster);
        Assert.Contains("One owner per task", roster);
    }

    [Fact]
    public void Roster_StaysSmall_ForAFiveBotTeam()
    {
        var roster = TeamRender.Roster(Team(5), "perch");
        Assert.True(Encoding.UTF8.GetByteCount(roster) < 3072, $"roster is {roster.Length} chars");
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
    public void RouterPrompt_ListsSlugs_AndTheMessage()
    {
        var prompt = TeamRender.RouterPrompt(Team(2), "the sidebar row is misaligned");
        Assert.Contains("- ada: Ada, Frontend dev", prompt);
        Assert.Contains("- bo: Bo, Backend dev", prompt);
        Assert.Contains("the sidebar row is misaligned", prompt);
        Assert.Contains("\"required\":[\"to\",\"confidence\",\"reason\"]", TeamRender.RouterSchema);
    }

    [Fact]
    public void OneLine_FlattensAndCaps()
    {
        Assert.Equal("", TeamRender.OneLine("  \n ", 10));
        Assert.Equal("a b c", TeamRender.OneLine("a\r\nb\n\nc", 10));
        Assert.Equal("abcde…", TeamRender.OneLine("abcdefghij", 5));
    }
}
