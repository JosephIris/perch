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
        /// Every Enter the controller pressed on its own (the submit retry).
        public readonly List<Guid> Entered = new();
        public readonly List<Action> Delayed = new();
        public readonly List<Guid> Closed = new();
        public bool TypeOk = true;
        /// What a bot's transcript reads as, per pane. Null = no transcript.
        public Func<Guid, InspectorData?> Transcript = _ => null;
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
                ReadTranscript = (pane, sid, cwd) => Transcript(pane),
                TypeToClaude = (s, line) => { if (!TypeOk) return false; Typed.Add((s.Id, line)); return true; },
                PressEnter = s => { Entered.Add(s.Id); return true; },
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

        /// A hook status for the session's pane, as MainWindow relays it.
        public void Status(Session s, string state, string? detail = null)
            => Ctrl.OnAgentStatus(s, new StatusMessage(state, detail));

        /// Run the newest timer callback (and drop it from the list).
        public void RunLastDelayed()
        {
            var a = Delayed[^1];
            Delayed.RemoveAt(Delayed.Count - 1);
            a();
        }

        public void Request() => Ctrl.OnRequest(new TeamRequestMsg { ProjectId = Project.Id, SinceSeq = 0 });

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
        // The per-prompt marker points at the bot's OWN context (roster +
        // its memory), not the shared roster.
        Assert.Equal(store.ContextPathFor("ada"), File.ReadAllText(TeamMarkers.RosterPathFor(sess.Root.Id)).Trim());
        var context = File.ReadAllText(store.ContextPathFor("ada"));
        Assert.Contains("# Team roster", context);
        Assert.Contains("# Your memory", context);
        Assert.Contains(store.MemoryPathFor("ada"), context);
        // The face: a hat from the position's name, a look drawn once.
        Assert.Equal("beanie", store.Doc.Positions.Single().Hat);
        Assert.NotNull(bot.Look);
        Assert.Contains(bot.Look!.Eyewear, TeamLooks.Eyewear);
        Assert.Contains(bot.Look!.Extra, TeamLooks.Extras);
        Assert.Contains(bot.Look!.Temper, TeamLooks.Tempers);
        var faceView = JsonSerializer.SerializeToElement(h.Ctrl.ProjectTeamView(h.Project.Id)!);
        var lookView = faceView.GetProperty("bots")[0].GetProperty("look");
        Assert.Equal("beanie", lookView.GetProperty("hat").GetString());
        Assert.Equal(bot.Look!.Eyewear, lookView.GetProperty("eyewear").GetString());
        Assert.Equal("beanie", faceView.GetProperty("positions")[0].GetProperty("hat").GetString());

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

        // Nothing left parked: a second agent-up schedules nothing (the one
        // timer the flush added is the typed line's submit check).
        Assert.Equal(2, h.Delayed.Count);
        h.Ctrl.OnAgentUp(sess);
        Assert.Equal(2, h.Delayed.Count);
        TeamMarkers.Clear(sess.Root.Id);
    }

    [Fact]
    public async Task Post_IsConfirmedByThePromptSubmitHook_OrEnterIsPressedAgain()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var sess = h.Sessions.Single();

        // Typed, and one timer armed: the check for the hook's echo.
        h.Post("hi", "[\"Ada\"]");
        Assert.Single(h.Typed);
        Assert.Single(h.Delayed);
        // The prompt-submit hook reports a [Perch team] prompt: the check is
        // then a no-op — no Enter, no new timer, nothing in the ledger.
        h.Status(sess, "working", "[Perch team] Joseph → @Ada: hi");
        h.RunLastDelayed();
        Assert.Empty(h.Entered);
        Assert.Empty(h.Delayed);
        Assert.DoesNotContain(h.Ledger, e => e.Kind == "system" && e.Event is "undelivered" or "waiting");

        // No echo this time. Enter is pressed again, twice, each with a new
        // check; the third check gives up and tells the room.
        h.Post("still there?", "[\"Ada\"]", "c2");
        h.RunLastDelayed();
        Assert.Single(h.Entered);
        h.RunLastDelayed();
        Assert.Equal(2, h.Entered.Count);
        h.RunLastDelayed();
        Assert.Equal(2, h.Entered.Count);
        Assert.Empty(h.Delayed);
        var stuck = h.Ledger.Single(e => e.Event == "undelivered");
        Assert.Equal("ada", stuck.From);
        Assert.StartsWith("Ada didn't take the post", stuck.Text);
        TeamMarkers.Clear(sess.Root.Id);
    }

    [Fact]
    public async Task Post_IntoAWorkingBot_IsQueued_NotStuck()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var sess = h.Sessions.Single();
        sess.Root.AgentState = AgentState.Working;
        h.Post("one more thing", "[\"Ada\"]");
        Assert.Single(h.Typed);
        // Mid-turn the line is queued and the hook only reports it later, so
        // an unconfirmed submit is not a failure: Enter is retried (harmless
        // on an empty box) but the room is told nothing.
        h.RunLastDelayed();
        h.RunLastDelayed();
        h.RunLastDelayed();
        Assert.Equal(2, h.Entered.Count);
        Assert.DoesNotContain(h.Ledger, e => e.Event == "undelivered");
        TeamMarkers.Clear(sess.Root.Id);
    }

    [Fact]
    public async Task Post_ToABotThatIsAskingSomething_WaitsUntilItsAnswered()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var sess = h.Sessions.Single();
        sess.Root.AgentState = AgentState.Permission;

        // Nothing is typed into a permission prompt: the keystrokes would
        // answer it. The post parks, and the room says why.
        h.Post("please fix the sidebar", "[\"Ada\"]");
        Assert.Empty(h.Typed);
        Assert.Empty(h.Delayed);
        var held = h.Ledger.Single(e => e.Kind == "system" && e.Event == "waiting");
        Assert.Equal("ada", held.From);
        Assert.Contains("waiting on you", held.Text);
        Assert.False(h.Ledger.Single(e => e.Kind == "user").Delivered);

        // A status that isn't a prompt frees it: one flush is scheduled (not
        // one per status), and it types once the pane really has moved on.
        h.Status(sess, "working");
        h.Status(sess, "working");
        Assert.Single(h.Delayed);
        sess.Root.AgentState = AgentState.Working;
        h.RunLastDelayed();
        var (_, line) = Assert.Single(h.Typed);
        Assert.Equal("[Perch team] Joseph → @Ada: please fix the sidebar", line);
        Assert.Contains(h.Ledger, e => e.Event == "delivered" && e.Text == "Delivered to Ada");

        // Enter is never pressed on a pane that is asking something either.
        h.Post("and the footer", "[\"Ada\"]", "c2");
        sess.Root.AgentState = AgentState.Waiting;
        h.RunLastDelayed();
        Assert.Empty(h.Entered);
        Assert.Equal(2, h.Ledger.Count(e => e.Event == "waiting"));
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
    public async Task Post_NamingNobody_GoesToEveryone()
    {
        // The owner talks to the room; each bot decides from the text whether
        // the post is for it. No model call, no "not sure who that's for".
        var h = new Harness();
        await h.CreateBot("Ada");
        await h.CreateBot("Bo", slug: "frontend-dev");
        h.Post("who owns the sidebar?", null);
        Assert.Equal(2, h.Typed.Count);
        Assert.All(h.Typed, t => Assert.Equal("[Perch team] Joseph → @everyone: who owns the sidebar?", t.Line));
        var post = h.Ledger.Single(e => e.Kind == "user");
        Assert.Equal(TeamRender.Everyone, Assert.Single(post.To!));
        Assert.True(post.Delivered);
        Assert.DoesNotContain(h.Ledger, e => e.Event is "routed" or "error");
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
    }

    [Fact]
    public async Task Start_GivesABotWithoutATab_AFreshOne_UnderTheSameName()
    {
        // The bot came with a pull (or its tab was closed): no session here.
        var h = new Harness();
        var bot = await h.CreateBot("Ada");
        var first = h.Sessions.Single();
        h.Sessions.Clear();
        h.Ctrl.OnSessionClosed(first);
        Assert.Null(bot.SessionId);

        await h.Ctrl.OnBotStartAsync(new TeamBotStartMsg { ProjectId = h.Project.Id, BotId = bot.Slug });
        var sess = h.Sessions.Single();
        Assert.Equal(sess.Id, bot.SessionId);
        Assert.Equal("ada", sess.Root.PinnedPeerName);   // same address, nothing here took it
        Assert.Equal(h.Store.SystemPathFor("ada"), File.ReadAllText(TeamMarkers.BriefPathFor(sess.Root.Id)).Trim());
        Assert.Contains(h.Ledger, e => e.Event == "joined" && e.Text == "Ada started here as Frontend dev");
        // The local file, not the shared one, records the tab.
        Assert.Contains(sess.Id.ToString("D"), File.ReadAllText(h.Store.LocalJsonPath));
        Assert.DoesNotContain("sessionId", File.ReadAllText(h.Store.JsonPath));

        // Already running → nothing happens but a toast.
        await h.Ctrl.OnBotStartAsync(new TeamBotStartMsg { ProjectId = h.Project.Id, BotId = bot.Slug });
        Assert.Single(h.Sessions);
        TeamMarkers.Clear(sess.Root.Id);
    }

    [Fact]
    public async Task TheFirstLeadTypeBot_Leads_AndTheOwnerCanHandItOver()
    {
        var h = new Harness();
        var ada = await h.CreateBot("Ada");                                  // Frontend dev: not a lead
        Assert.Null(h.Store.Doc.LeadSlug);
        var lee = await h.CreateBot("Lee", position: "Team lead");
        Assert.Equal("lee", h.Store.Doc.LeadSlug);
        Assert.Contains(h.Ledger, e => e.Event == "lead" && e.Text == "Lee leads the team");
        Assert.Contains("You lead the team", File.ReadAllText(h.Store.SystemPathFor("lee")));
        Assert.DoesNotContain("You lead the team", File.ReadAllText(h.Store.SystemPathFor("ada")));
        Assert.Contains("Lee (session name `lee`) — Team lead, the team lead", File.ReadAllText(h.Store.RosterPath));

        h.Ctrl.OnLeadSet(new TeamLeadSetMsg { ProjectId = h.Project.Id, BotId = ada.Slug });
        Assert.Equal("ada", h.Store.Doc.LeadSlug);
        Assert.Contains("You lead the team", File.ReadAllText(h.Store.SystemPathFor("ada")));
        Assert.DoesNotContain("You lead the team", File.ReadAllText(h.Store.SystemPathFor("lee")));
        var view = JsonSerializer.SerializeToElement(h.Ctrl.ProjectTeamView(h.Project.Id)!);
        Assert.Equal("ada", view.GetProperty("lead").GetString());
        Assert.Same(lee, h.Store.Doc.Bot("lee"));
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
    }

    [Fact]
    public async Task TaskBoard_TheLeadRunsIt_EveryBotKeepsItsPiece_AndTheOwnerConfirms()
    {
        var h = new Harness();
        var lee = await h.CreateBot("Lee", position: "Team lead");
        var ada = await h.CreateBot("Ada");
        var leeSess = h.Sessions.Single(s => s.Root.PinnedPeerName == "lee");
        var adaSess = h.Sessions.Single(s => s.Root.PinnedPeerName == "ada");
        void Task(Session s, string op, string? title = null, string? bot = null, string? status = null, string? note = null)
            => h.Ctrl.OnTeamTask(s, s.Root.Id, new TeamTaskMessage(op, bot, title, status, note));

        // A member can't set the task; the room says so.
        Task(adaSess, "main", "Ship the sidebar");
        Assert.Null(h.Store.Tasks.Current);
        Assert.Contains(h.Ledger, e => e.Event == "error" && e.Text.Contains("only the lead (Lee)"));

        // The lead sets it, splits it; Ada keeps her piece current.
        Task(leeSess, "main", "Ship the sidebar");
        var board = h.Store.Tasks.Current!;
        Assert.Equal("open", board.Status);
        Assert.Equal("lee", board.SetBy);
        Task(leeSess, "assign", "Nest the bots under the team row", bot: "ada");
        Task(leeSess, "mine", "Review Ada's change", status: "doing");
        Task(adaSess, "mine", status: "doing", note: "chevron in, CSS next");
        Assert.Equal("doing", board.ItemOf("ada")!.Status);
        Assert.Equal("Nest the bots under the team row", board.ItemOf("ada")!.Title);
        Assert.Equal("chevron in, CSS next", board.ItemOf("ada")!.Note);
        // The board reaches every bot with its prompt, phrased for its role.
        var adaCtx = File.ReadAllText(h.Store.ContextPathFor("ada"));
        Assert.Contains("**Ship the sidebar** — open", adaCtx);
        Assert.Contains("- Ada (you): [doing] Nest the bots under the team row — chevron in, CSS next", adaCtx);
        Assert.Contains("Lee (`lee`) leads", adaCtx);
        Assert.Contains("perch team task done", File.ReadAllText(h.Store.ContextPathFor("lee")));
        Assert.DoesNotContain("perch team task done", adaCtx);
        // Persisted beside team.json, shared.
        Assert.True(File.Exists(h.Store.TasksPath));
        Assert.Contains("Ship the sidebar", File.ReadAllText(h.Store.TasksPath));

        // The lead closes it → review; the owner says not yet → open, and the
        // lead gets the note as an owner post.
        Task(leeSess, "done");
        Assert.Equal("review", board.Status);
        Assert.Contains(h.Ledger, e => e.Event == "task.review" && e.Text.StartsWith("Lee says the task is done"));
        h.Typed.Clear();
        h.Ctrl.OnTaskReject(new TeamTaskRejectMsg { ProjectId = h.Project.Id, Note = "the footer still shifts" });
        Assert.Equal("open", board.Status);
        var (toLee, line) = Assert.Single(h.Typed);
        Assert.Equal(leeSess.Id, toLee);
        Assert.Equal("[Perch team] Joseph → @Lee: Not done yet: the footer still shifts", line);

        // Second time: the owner confirms. Everyone gets the wrap-up post;
        // each bot is reset when its turn ends AFTER that post went in.
        Task(leeSess, "done");
        h.Typed.Clear();
        h.Ctrl.OnTaskConfirm(new TeamTaskConfirmMsg { ProjectId = h.Project.Id });
        Assert.Equal("done", board.Status);
        Assert.Equal(2, h.Typed.Count);
        Assert.All(h.Typed, t => Assert.Contains("Wrap up now: update your memory file", t.Line));
        var view = JsonSerializer.SerializeToElement(h.Ctrl.ProjectTeamView(h.Project.Id)!).GetProperty("task");
        Assert.Equal(2, view.GetProperty("wrapping").GetArrayLength());

        // A Done BEFORE the post echo is the old turn ending: no reset.
        h.Typed.Clear();
        h.Status(adaSess, "done");
        Assert.Empty(h.Typed);
        // The echo, then the turn end: /clear is typed, the room says reset.
        h.Status(adaSess, "working", "[Perch team] Joseph → @everyone: The task");
        h.Status(adaSess, "done");
        var (resetSess, cleared) = Assert.Single(h.Typed);
        Assert.Equal(adaSess.Id, resetSess);
        Assert.Equal("/clear", cleared);
        Assert.Contains(h.Ledger, e => e.Event == "reset" && e.Text == "Ada reset for the next task");
        Assert.NotNull(h.Store.Tasks.Current);   // Lee is still wrapping

        h.Status(leeSess, "working", "[Perch team] Joseph → @everyone: The task");
        h.Status(leeSess, "done");
        Assert.Null(h.Store.Tasks.Current);
        var archived = Assert.Single(h.Store.Tasks.Done);
        Assert.Equal("Ship the sidebar", archived.Title);
        Assert.Contains(h.Ledger, e => e.Event == "task" && e.Text.StartsWith("Everyone is reset"));
        // The context now says there is no task, and the lead is told to set one.
        Assert.Contains("(No task set yet. You are the lead", File.ReadAllText(h.Store.ContextPathFor("lee")));
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
    }

    [Fact]
    public async Task TaskBoard_TheOwnerCanSetTheTask_WithoutALead()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        h.Ctrl.OnTaskSet(new TeamTaskSetMsg { ProjectId = h.Project.Id, Title = "Fix the login flow" });
        var board = h.Store.Tasks.Current!;
        Assert.Equal("you", board.SetBy);
        Assert.Contains(h.Ledger, e => e.Event == "task" && e.Text == "Task set by Joseph: Fix the login flow");
        h.Ctrl.OnTaskSet(new TeamTaskSetMsg { ProjectId = h.Project.Id, Title = "Fix the login and signup flows" });
        Assert.Same(board, h.Store.Tasks.Current);
        Assert.Contains(h.Ledger, e => e.Event == "task" && e.Text == "Joseph renamed the task: Fix the login and signup flows");
        // A bot with no session (not running) has nothing to wrap: confirm
        // archives at once.
        var sess = h.Sessions.Single();
        h.Sessions.Clear();
        h.Ctrl.OnSessionClosed(sess);
        h.Ctrl.OnTaskConfirm(new TeamTaskConfirmMsg { ProjectId = h.Project.Id });
        Assert.Null(h.Store.Tasks.Current);
        Assert.Single(h.Store.Tasks.Done);
    }

    [Fact]
    public async Task Post_WithNoBotsLeft_SaysSo()
    {
        var h = new Harness();
        var bot = await h.CreateBot("Ada");
        h.Ctrl.OnBotRemove(new TeamBotRemoveMsg { ProjectId = h.Project.Id, BotId = bot.Slug, CloseTab = true });
        h.Post("anyone?", null);
        Assert.Empty(h.Typed);
        Assert.DoesNotContain(h.Ledger, e => e.Kind == "user");
        Assert.Contains(h.Ledger, e => e.Event == "error" && e.Text.StartsWith("No bots on the team yet"));
    }

    [Fact]
    public async Task Room_ShowsWhatABotSaysToTheOwner_NotItsAsidesToTeammates()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var pane = h.Sessions.Single().Root;
        static InspectorEvent Ev(string kind, string ts, string text = "", string verb = "", string target = "")
            => new(kind, ts, text, verb, target, "", 1);
        h.Transcript = _ => new InspectorData(new[]
        {
            // A room post: the reply is for the owner.
            Ev("prompt", "2026-09-02T19:00:00Z", "[Perch team] Joseph → @Ada: say hey to Bo"),
            Ev("work", "2026-09-02T19:00:05Z", verb: "SendMessage"),
            Ev("beat", "2026-09-02T19:00:10Z", "Done, Bo has a hello from me."),
            // Bo answers: the exchange is in the room already (Bo's hook put
            // it there); what Ada says about it is narration, not a reply.
            Ev("peer", "2026-09-02T19:01:00Z", "Hey Ada, Bo here.", "from", "bo"),
            Ev("work", "2026-09-02T19:01:05Z", verb: "SendMessage"),
            Ev("beat", "2026-09-02T19:01:10Z", "Replied to Bo with a hello. Nothing pending."),
            // A post to everyone that isn't for Ada: the agreed answer is dropped.
            Ev("prompt", "2026-09-02T19:02:00Z", "[Perch team] Joseph → @everyone: Bo, how's the API?"),
            Ev("beat", "2026-09-02T19:02:05Z", "(no reply)"),
            // Something the owner typed straight into the terminal is answered there.
            Ev("prompt", "2026-09-02T19:03:00Z", "what model are you?"),
            Ev("beat", "2026-09-02T19:03:05Z", "claude-fable-5-1"),
            // And a post for Ada again.
            Ev("prompt", "2026-09-02T19:04:00Z", "[Perch team] Joseph → @Ada: status?"),
            Ev("beat", "2026-09-02T19:04:05Z", "Sidebar is done; footer next."),
        }, null);
        h.Request();

        var beats = h.Ledger.Where(e => e.Kind == "beat").Select(e => e.Text).ToList();
        Assert.Equal(new[] { "Done, Bo has a hello from me.", "Sidebar is done; footer next." }, beats);
        // Tool calls are kept regardless — they fold in the room.
        Assert.Equal(2, h.Ledger.Count(e => e.Kind == "work"));
        // The turn-starter is remembered across polls: a beat arriving later
        // in a teammate-started turn stays out too.
        var more = new List<InspectorEvent>(h.Transcript(pane.Id)!.Events)
        {
            Ev("peer", "2026-09-02T19:05:00Z", "Thanks!", "from", "bo"),
            Ev("beat", "2026-09-02T19:05:05Z", "Acknowledged Bo's thanks."),
        };
        h.Transcript = _ => new InspectorData(more, null);
        h.Request();
        Assert.Equal(2, h.Ledger.Count(e => e.Kind == "beat"));
        TeamMarkers.Clear(pane.Id);
    }

    [Fact]
    public void NoReply_IsRecognisedLoosely_AndAnsweringFollowsTheLastTurnStarter()
    {
        Assert.True(TeamController.IsNoReply("(no reply)"));
        Assert.True(TeamController.IsNoReply("  (No reply.) "));
        Assert.False(TeamController.IsNoReply("No reply needed from Bo; I'm on it."));
        static InspectorEvent Ev(string kind, string text = "") => new(kind, "2026-09-02T19:00:00Z", text, "", "", "", 1);
        var events = new[]
        {
            Ev("prompt", "[Perch team] Joseph → @Ada: hi"), Ev("beat", "hello"),
            Ev("peer", "from bo"), Ev("beat", "ok"),
        };
        Assert.False(TeamController.AnsweringAt(events, 0));
        Assert.True(TeamController.AnsweringAt(events, 2));
        Assert.False(TeamController.AnsweringAt(events, 4));
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
