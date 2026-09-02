using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Perch.Tests;

/// The team controller, driven with lambdas instead of a window. What these
/// pin is the contract a bot depends on: its files exist before its shell
/// starts, its address is unique, the owner's post reaches its terminal as
/// one prefixed line (or waits for a Claude to come up), and the room's
/// ledger tells the story in order.
public class TeamControllerTests
{
    private sealed class Harness
    {
        public readonly string Repo;
        public readonly Project Project;
        public readonly List<Session> Sessions = new();
        public readonly List<object> Posted = new();
        public readonly List<(Guid Session, string Line)> Typed = new();
        public readonly List<Action> Delayed = new();
        public readonly List<Guid> Closed = new();
        public bool TypeOk = true;
        public readonly TeamController Ctrl;

        public Harness()
        {
            Repo = Path.Combine(Path.GetTempPath(), "perch-teamctrl-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Repo);
            Project = new Project { Name = "perch", Path = Repo };
            Ctrl = new TeamController(new TeamHost
            {
                ProjectById = id => Project.Id == id ? Project : null,
                Projects = () => new[] { Project },
                SessionById = id => Sessions.FirstOrDefault(s => s.Id == id),
                Sessions = () => Sessions,
                ResolveCwd = (s, p) => s.Cwd,
                ReadTranscript = (pane, sid, cwd) => null,
                TypeToClaude = (s, line) => { if (!TypeOk) return false; Typed.Add((s.Id, line)); return true; },
                Wake = s => s.Dormant = false,
                CreateTab = (proj, name, wt, model, cc) =>
                {
                    var s = new Session { Title = name, Cwd = proj.Path, ProjectId = proj.Id };
                    s.Root.PeerName = cc;
                    s.Root.PinnedPeerName = cc;
                    s.Root.ClaudeSessionId = Guid.NewGuid().ToString();
                    Sessions.Add(s);
                    return Task.FromResult<Session?>(s);
                },
                CloseSession = (id, rm) => { Closed.Add(id); Sessions.RemoveAll(s => s.Id == id); },
                Post = o => Posted.Add(o),
                PushState = () => { },
                Delay = (a, t) => Delayed.Add(a),
            });
        }

        public TeamStore Store => Ctrl.StoreFor(Project.Id)!;

        public async Task<TeamBot> CreateBot(string nick, string position = "Frontend dev", string? slug = null, bool worktree = false)
        {
            await Ctrl.OnBotCreateAsync(new TeamBotCreateMsg
            {
                ProjectId = Project.Id, Nickname = nick, Worktree = worktree,
                PositionSlug = slug,
                Position = slug == null ? new TeamPositionSpec
                {
                    Name = position, Purpose = "Owns the web chrome.", Brief = "## Role\nYou own src/web.",
                } : null,
            });
            return Store.Doc.Bots.First(b => b.Nickname == nick);
        }

        public JsonElement To(TeamPostMsg _, string json) => JsonDocument.Parse(json).RootElement.Clone();

        public void Post(string text, string? toJson, string clientId = "c1")
        {
            Ctrl.OnPost(new TeamPostMsg
            {
                ProjectId = Project.Id, Text = text, ClientId = clientId,
                To = toJson == null ? null : JsonDocument.Parse(toJson).RootElement.Clone(),
            });
        }

        public List<RoomEntry> Ledger => Store.Ledger.ReadAll();

        /// Every team.data payload posted so far, flattened to (kind, text).
        public List<(string Kind, string Text)> PostedEntries()
        {
            var list = new List<(string, string)>();
            foreach (var o in Posted)
            {
                var el = JsonSerializer.SerializeToElement(o);
                if (el.TryGetProperty("type", out var t) && t.GetString() == "team.data")
                    foreach (var e in el.GetProperty("entries").EnumerateArray())
                        list.Add((e.GetProperty("kind").GetString()!, e.GetProperty("text").GetString()!));
            }
            return list;
        }
    }

    [Fact]
    public async Task Create_WritesTheBotsFiles_PinsItsAddress_AndLogsJoined()
    {
        var h = new Harness();
        var bot = await h.CreateBot("Ada");
        var store = h.Store;

        Assert.Equal("ada", bot.Slug);
        Assert.Equal("ada", bot.CcName);
        Assert.NotNull(bot.SessionId);
        var sess = h.Sessions.Single();
        Assert.Equal("ada", sess.Root.PinnedPeerName);

        // The files the shim and the hook read, written BEFORE the shell starts.
        Assert.True(File.Exists(store.JsonPath));
        Assert.StartsWith("# You are Ada, the Frontend dev on the perch team", File.ReadAllText(store.SystemPathFor("ada")));
        Assert.Contains("You own src/web.", File.ReadAllText(store.SystemPathFor("ada")));
        Assert.Contains("`ada`", File.ReadAllText(store.RosterPath));
        Assert.Equal(store.SystemPathFor("ada"), File.ReadAllText(TeamMarkers.BriefPathFor(sess.Root.Id)).Trim());
        Assert.Equal(store.RosterPath, File.ReadAllText(TeamMarkers.RosterPathFor(sess.Root.Id)).Trim());

        var joined = Assert.Single(h.Ledger);
        Assert.Equal("system", joined.Kind);
        Assert.Equal("joined", joined.Event);
        Assert.Equal("Ada joined as Frontend dev", joined.Text);

        var view = JsonSerializer.SerializeToElement(h.Ctrl.ProjectTeamView(h.Project.Id)!);
        var b = Assert.Single(view.GetProperty("bots").EnumerateArray());
        Assert.Equal("Ada", b.GetProperty("nickname").GetString());
        Assert.Equal("Frontend dev", b.GetProperty("positionName").GetString());
        Assert.Equal(sess.Id.ToString("D"), b.GetProperty("sessionId").GetString());
        Assert.True(Assert.Single(view.GetProperty("positions").EnumerateArray()).GetProperty("hasBrief").GetBoolean());

        TeamMarkers.Clear(sess.Root.Id);
    }

    [Fact]
    public async Task Create_MintsAnAddressNoLivePaneAlreadyAnswersTo()
    {
        var h = new Harness();
        var other = new Session { Title = "ada" };
        other.Root.PeerName = "ada";
        h.Sessions.Add(other);

        var bot = await h.CreateBot("Ada");
        Assert.Equal("ada-2", bot.CcName);
        Assert.Contains("`ada-2`", File.ReadAllText(h.Store.RosterPath));

        // Same nickname twice on one team is refused, not renamed.
        await h.Ctrl.OnBotCreateAsync(new TeamBotCreateMsg
        {
            ProjectId = h.Project.Id, Nickname = "ada", PositionSlug = bot.PositionSlug,
        });
        Assert.Single(h.Store.Doc.Bots);
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
    }

    [Theory]
    [InlineData("Ada", true)]
    [InlineData("ada_2", true)]
    [InlineData("front-end", true)]
    [InlineData("", false)]
    [InlineData("two words", false)]
    [InlineData("@ada", false)]
    [InlineData("everyone", false)]
    [InlineData("you", false)]
    public void Nicknames_MustBeMentionSafe(string nick, bool ok)
        => Assert.Equal(ok, TeamController.ValidNickname(nick));

    [Fact]
    public async Task Post_ToANickname_TypesOnePrefixedLine()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        h.Post("please fix the sidebar", "[\"Ada\"]");

        var (sid, line) = Assert.Single(h.Typed);
        Assert.Equal(h.Sessions.Single().Id, sid);
        Assert.Equal("[Perch team] Joseph → @Ada: please fix the sidebar", line);

        var ledger = h.Ledger;
        var post = ledger.Single(e => e.Kind == "user");
        Assert.Equal("you", post.From);
        Assert.Equal("ada", Assert.Single(post.To!));
        Assert.Equal("c1", post.ClientId);
        // Delivery is a fact ON the post, not a row after it.
        Assert.True(post.Delivered);
        Assert.DoesNotContain(ledger, e => e.Event == "delivered");
        // The page saw the post, with nicknames not slugs.
        var seen = h.PostedEntries();
        Assert.Contains(("user", "please fix the sidebar"), seen);
        TeamMarkers.Clear(h.Sessions.Single().Root.Id);
    }

    [Fact]
    public async Task Post_ToEveryone_FansOutToEachBot()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        await h.CreateBot("Bo", slug: "frontend-dev");
        h.Post("introduce yourselves", "\"everyone\"");

        Assert.Equal(2, h.Typed.Count);
        Assert.All(h.Typed, t => Assert.StartsWith("[Perch team] Joseph → @everyone: introduce yourselves", t.Line));
        Assert.Equal(h.Sessions.Select(s => s.Id).OrderBy(x => x), h.Typed.Select(t => t.Session).OrderBy(x => x));
        var post = h.Ledger.Single(e => e.Kind == "user");
        Assert.Equal(TeamRender.Everyone, Assert.Single(post.To!));
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
    }

    [Fact]
    public async Task Post_WhenNoClaudeIsUp_ParksUntilTheAgentStarts()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var sess = h.Sessions.Single();
        h.TypeOk = false;
        h.Post("hello?", "[\"Ada\"]");
        Assert.Empty(h.Typed);
        Assert.DoesNotContain(h.Ledger, e => e.Event == "delivered");

        // The session-start hook fires; the flush runs after the settle delay.
        h.TypeOk = true;
        h.Ctrl.OnAgentUp(sess);
        var flush = Assert.Single(h.Delayed);
        flush();
        var (_, line) = Assert.Single(h.Typed);
        Assert.Equal("[Perch team] Joseph → @Ada: hello?", line);
        Assert.Contains(h.Ledger, e => e.Event == "delivered" && e.Text == "Delivered to Ada");

        // Nothing left parked: a second agent-up schedules nothing.
        h.Ctrl.OnAgentUp(sess);
        Assert.Single(h.Delayed);
        TeamMarkers.Clear(sess.Root.Id);
    }

    [Fact]
    public async Task Post_ToASleepingBot_WakesItAndParks()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var sess = h.Sessions.Single();
        sess.Dormant = true;
        // Wake flips Dormant, but no Claude answers yet (TypeOk false = booting).
        h.TypeOk = false;
        h.Post("wake up", "[\"Ada\"]");
        Assert.False(sess.Dormant);
        Assert.Empty(h.Typed);
        h.TypeOk = true;
        h.Ctrl.OnAgentUp(sess);
        h.Delayed.Single()();
        Assert.Single(h.Typed);
        TeamMarkers.Clear(sess.Root.Id);
    }

    [Fact]
    public async Task Post_Unaddressed_WithOneBot_GoesToThem()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        h.Post("how is it going", null);
        // Routing is async even on the shortcut path; it completes synchronously
        // here because nothing awaits, but give it a beat to be safe.
        for (var i = 0; i < 50 && h.Typed.Count == 0; i++) await Task.Delay(20);
        var (_, line) = Assert.Single(h.Typed);
        Assert.Equal("[Perch team] Joseph → @Ada: how is it going", line);
        Assert.Contains(h.Ledger, e => e.Event == "routed" && e.Text.StartsWith("Sent to Ada"));
        var post = h.Ledger.Single(e => e.Kind == "user");
        Assert.Null(post.To);
        TeamMarkers.Clear(h.Sessions.Single().Root.Id);
    }

    [Fact]
    public async Task Post_Unaddressed_WithSeveralBots_AndNoClaude_AsksInstead()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        await h.CreateBot("Bo", slug: "frontend-dev");
        try
        {
            ClaudeHeadless.ResolveOverride = () => null;   // no claude → the router can't run
            h.Post("who owns the sidebar?", null);
            for (var i = 0; i < 100 && !h.Ledger.Any(e => e.Event == "error"); i++) await Task.Delay(50);
        }
        finally { ClaudeHeadless.ResolveOverride = null; }

        Assert.Empty(h.Typed);
        var ask = h.Ledger.Single(e => e.Event == "error");
        Assert.StartsWith("Not sure who that's for", ask.Text);
        Assert.Contains("@Ada", ask.Text);
        Assert.Contains("@everyone", ask.Text);
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
    }

    [Fact]
    public async Task Remove_ClearsTheMarkers_LogsLeft_AndCanCloseTheTab()
    {
        var h = new Harness();
        var bot = await h.CreateBot("Ada");
        var sess = h.Sessions.Single();
        Assert.True(File.Exists(TeamMarkers.BriefPathFor(sess.Root.Id)));

        h.Ctrl.OnBotRemove(new TeamBotRemoveMsg { ProjectId = h.Project.Id, BotId = bot.Slug, CloseTab = true });
        Assert.Empty(h.Store.Doc.Bots);
        Assert.False(File.Exists(TeamMarkers.BriefPathFor(sess.Root.Id)));
        Assert.Null(sess.Root.PinnedPeerName);
        Assert.Equal(sess.Id, Assert.Single(h.Closed));
        Assert.Contains(h.Ledger, e => e.Event == "left" && e.Text == "Ada left the team");
        Assert.DoesNotContain("Ada (session name", File.ReadAllText(h.Store.RosterPath));
    }

    [Fact]
    public async Task ClosingABotsTab_KeepsItOnTheRoster_AsNotRunning()
    {
        var h = new Harness();
        var bot = await h.CreateBot("Ada");
        var sess = h.Sessions.Single();
        h.Ctrl.OnSessionClosed(sess);
        Assert.Null(bot.SessionId);
        Assert.Contains("[not running]", File.ReadAllText(h.Store.RosterPath));
        Assert.Contains(h.Ledger, e => e.Event == "left" && e.Text.Contains("tab was closed"));
        TeamMarkers.Clear(sess.Root.Id);
    }

    [Fact]
    public async Task SleepAndWake_AreRoomEvents_AndRosterPresence()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var sess = h.Sessions.Single();
        sess.Dormant = true;
        h.Ctrl.OnSessionSlept(sess);
        Assert.Contains("[asleep]", File.ReadAllText(h.Store.RosterPath));
        sess.Dormant = false;
        h.Ctrl.OnSessionWoke(sess);
        Assert.Contains("[idle]", File.ReadAllText(h.Store.RosterPath));
        var events = h.Ledger.Where(e => e.Kind == "system").Select(e => e.Event).ToList();
        Assert.Equal(new[] { "joined", "asleep", "woke" }, events);
        TeamMarkers.Clear(sess.Root.Id);
    }

    [Fact]
    public async Task PeerMessage_FromABot_LandsInTheLedgerWithFullText()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        await h.CreateBot("Bo", slug: "frontend-dev");
        var ada = h.Sessions[0];
        h.Ctrl.OnPeerMsg(ada, ada.Root.Id, new PeerMsgMessage("sent", "bo", "Schema done…", true,
            "Schema migration finished\nThe new column is tenant_id.", "Schema migration finished"));
        var peer = h.Ledger.Single(e => e.Kind == "peer");
        Assert.Equal("ada", peer.From);
        Assert.Equal("bo", Assert.Single(peer.To!));
        Assert.Equal("Schema migration finished\nThe new column is tenant_id.", peer.Text);
        Assert.Equal("Schema migration finished", peer.Summary);
        Assert.True(peer.Ok);
        // "sending" is not a room event.
        h.Ctrl.OnPeerMsg(ada, ada.Root.Id, new PeerMsgMessage("sending", "bo", "x"));
        Assert.Single(h.Ledger, e => e.Kind == "peer");
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
    }

    [Fact]
    public async Task TeamPost_FromABot_IsANoteForTheRoom()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var ada = h.Sessions.Single();
        h.Ctrl.OnTeamPost(ada, ada.Root.Id, new TeamPostMessage("Sidebar spacing is on main now."));
        var note = h.Ledger.Single(e => e.Kind == "note");
        Assert.Equal("ada", note.From);
        Assert.Equal(TeamRender.Everyone, Assert.Single(note.To!));
        Assert.Contains(("note", "Sidebar spacing is on main now."), h.PostedEntries());
        TeamMarkers.Clear(ada.Root.Id);
    }

    [Fact]
    public void DeliveryLine_FlattensMultiLinePosts()
    {
        Assert.Equal("[Perch team] Joseph → @Ada: one ⏎ two", TeamController.DeliveryLine("one\r\ntwo\n", "Ada"));
        Assert.Equal("[Perch team] Joseph → @everyone: hi", TeamController.DeliveryLine("hi", null));
    }

    [Fact]
    public void ParseRoute_ReadsStructuredOrText_AndDropsUnknownSlugs()
    {
        var doc = new TeamDoc();
        doc.Bots.Add(new TeamBot { Slug = "ada", Nickname = "Ada", CcName = "ada" });
        doc.Bots.Add(new TeamBot { Slug = "bo", Nickname = "Bo", CcName = "bo-2" });

        var structured = new HeadlessResult(true, "prose", null, 0, 0, "", "{\"to\":[\"ada\",\"zed\"],\"confidence\":0.9,\"reason\":\"sidebar\"}");
        var (to, conf, reason) = TeamController.ParseRoute(structured, doc);
        Assert.Equal("ada", Assert.Single(to).Slug);
        Assert.Equal(0.9, conf, 3);
        Assert.Equal("sidebar", reason);

        var text = new HeadlessResult(true, "{\"to\":[\"Bo\",\"bo-2\"],\"confidence\":0.5,\"reason\":\"r\"}", null, 0, 0, "");
        var (to2, conf2, _) = TeamController.ParseRoute(text, doc);
        Assert.Equal("bo", Assert.Single(to2).Slug);   // nickname and address both resolve, deduped
        Assert.Equal(0.5, conf2, 3);

        var (to3, conf3, why) = TeamController.ParseRoute(new HeadlessResult(true, "not json", null, 0, 0, ""), doc);
        Assert.Empty(to3);
        Assert.Equal(0, conf3);
        Assert.Equal("unreadable answer", why);

        var (to4, _, why4) = TeamController.ParseRoute(new HeadlessResult(false, "", "boom", 0, 0, ""), doc);
        Assert.Empty(to4);
        Assert.Equal("boom", why4);
    }

    [Fact]
    public async Task Request_RepliesWithEntriesSinceTheWatermark()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        h.Post("one", "[\"Ada\"]", "c1");
        h.Post("two", "[\"Ada\"]", "c2");
        var before = h.Posted.Count;
        h.Ctrl.OnRequest(new TeamRequestMsg { ProjectId = h.Project.Id, SinceSeq = 2 });
        var reply = JsonSerializer.SerializeToElement(h.Posted[before]);
        Assert.Equal("team.data", reply.GetProperty("type").GetString());
        var entries = reply.GetProperty("entries").EnumerateArray().ToList();
        Assert.All(entries, e => Assert.True(e.GetProperty("seq").GetInt64() > 2));
        Assert.Contains(entries, e => e.GetProperty("kind").GetString() == "user" && e.GetProperty("clientId").GetString() == "c2");
        Assert.Equal(h.Store.Ledger.LastSeq, reply.GetProperty("lastSeq").GetInt64());
        TeamMarkers.Clear(h.Sessions.Single().Root.Id);
    }
}
