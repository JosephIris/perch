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
        public readonly List<(Guid Pane, byte[] Bytes)> Raw = new();
        public readonly List<Guid> Cleared = new();
        public readonly List<Guid> Started = new();
        /// Panes the fake host reports as having NO terminal (never spawned).
        public readonly HashSet<Guid> NoPty = new();
        public readonly List<(Guid Pane, string Model)> ModelSet = new();
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
                WriteRaw = (p, b) => Raw.Add((p, b)),
                ClearPrompt = id => Cleared.Add(id),
                HasPty = id => !NoPty.Contains(id),
                EnsureRunning = s => Started.Add(s.Id),
                SetPaneModel = (p, m) => ModelSet.Add((p, m)),
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
        Assert.Matches(@"^\[Perch team\] #\d+ Joseph → @Ada: please fix the sidebar$", line);

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
        Assert.All(h.Typed, t => Assert.Matches(@"^\[Perch team\] #\d+ Joseph → @everyone: introduce yourselves", t.Line));
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
        Assert.Matches(@"^\[Perch team\] #\d+ Joseph → @Ada: hello\?$", line);
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

        // No echo this time. Enter is pressed again on the first two checks;
        // the third only waits — and when that budget is out the line is
        // HELD, not retyped: the room hears the bot is mid-task, and the next
        // turn-end presses Enter for it.
        h.Post("still there?", "[\"Ada\"]", "c2");
        h.RunLastDelayed();
        Assert.Single(h.Entered);
        h.RunLastDelayed();
        Assert.Equal(2, h.Entered.Count);
        Assert.DoesNotContain(h.Ledger, e => e.Event is "undelivered" or "waiting");
        h.RunLastDelayed();                                  // the last, quiet check: out of budget
        Assert.Equal(2, h.Entered.Count);
        Assert.Equal(2, h.Typed.Count);                      // never typed again on its own
        var held = h.Ledger.Single(e => e.Event == "waiting");
        Assert.Contains("mid-task", held.Text);
        Assert.Empty(h.Delayed);

        // The turn ends: Enter, then the hook confirms, and the room hears it
        // landed.
        h.Status(sess, "done");
        h.RunLastDelayed();                                  // ResumeHeld
        Assert.Equal(3, h.Entered.Count);
        h.Status(sess, "working", "[Perch team] Joseph → @Ada: still there?");
        Assert.Contains(h.Ledger, e => e.Event == "delivered" && e.Text == "Delivered to Ada");
        Assert.DoesNotContain(h.Ledger, e => e.Event == "undelivered");
        TeamMarkers.Clear(sess.Root.Id);
    }

    /// A bot whose turn stalled with no output: the host's watchdog thinks it
    /// is idle, so the line is typed — and sits in its composer. Nobody
    /// confirms it; it is held with a "mid-task" row, and the next quiet
    /// moment the watchdog reports (OnPaneIdle) presses Enter for it. Never a
    /// second copy of the line.
    [Fact]
    public async Task Post_IntoAStalledBot_IsHeld_AndEnteredWhenItComesFree()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var sess = h.Sessions.Single();
        h.Post("one more thing", "[\"Ada\"]");
        Assert.Single(h.Typed);
        for (var i = 0; i < TeamController.SubmitChecks.Length; i++) h.RunLastDelayed();
        Assert.Equal(TeamController.SubmitEnterTries, h.Entered.Count);
        Assert.Contains(h.Ledger, e => e.Event == "waiting" && e.Text.Contains("mid-task"));
        Assert.DoesNotContain(h.Ledger, e => e.Event == "undelivered");

        h.Ctrl.OnPaneIdle(sess);                             // the watchdog: output went quiet
        Assert.Equal(TeamController.SubmitEnterTries + 1, h.Entered.Count);
        Assert.Single(h.Typed);                              // never a second copy
        h.Status(sess, "working", "[Perch team] Joseph → @Ada: one more thing");
        Assert.Contains(h.Ledger, e => e.Event == "delivered" && e.Text == "Delivered to Ada");
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
        Assert.Contains("has a question open", held.Text);
        Assert.False(h.Ledger.Single(e => e.Kind == "user").Delivered);

        // A status that isn't a prompt frees it: one flush is scheduled (not
        // one per status). It types once the pane has really moved on AND is
        // not mid-turn — the answer lets the turn continue, and a line typed
        // into a running turn just sits there, so the post waits for its end.
        h.Status(sess, "working");
        h.Status(sess, "working");
        Assert.Single(h.Delayed);
        sess.Root.AgentState = AgentState.Working;
        h.RunLastDelayed();
        Assert.Empty(h.Typed);                               // still mid-turn
        sess.Root.AgentState = AgentState.Done;
        h.Status(sess, "done");
        h.RunLastDelayed();                                  // the flush
        var (_, line) = Assert.Single(h.Typed);
        Assert.Matches(@"^\[Perch team\] #\d+ Joseph → @Ada: please fix the sidebar$", line);
        Assert.Contains(h.Ledger, e => e.Event == "delivered" && e.Text == "Delivered to Ada");

        // Enter is never pressed on a pane that is asking something either:
        // the submit check finds the prompt and holds the line for after the
        // answer, telling the room why.
        sess.Root.AgentState = AgentState.Waiting;
        h.RunLastDelayed();                                  // the submit check
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
        // (One of the pending timers is the cold-start watchdog; the flush is
        // the newest.)
        h.RunLastDelayed();
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
        Assert.All(h.Typed, t => Assert.Matches(@"^\[Perch team\] #\d+ Joseph → @everyone: who owns the sidebar\?$", t.Line));
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
        void Task(Session s, string op, string? title = null, string? bot = null, string? status = null, string? note = null, string? id = null)
            => h.Ctrl.OnTeamTask(s, s.Root.Id, new TeamTaskMessage(op, bot, title, status, note, id));

        // A member can't open a task; the room says so.
        Task(adaSess, "new", "Ship the sidebar");
        Assert.Empty(h.Store.Tasks.Open);
        Assert.Contains(h.Ledger, e => e.Event == "error" && e.Text.Contains("only the lead (Lee)"));

        // The lead opens it (the CLI reads the id from the reply file), splits
        // it; Ada keeps her piece current — by id, or without one while it's
        // her only open piece.
        Task(leeSess, "new", "Ship the sidebar");
        var board = Assert.Single(h.Store.Tasks.Open);
        Assert.Equal("open", board.Status);
        Assert.Equal("lee", board.SetBy);
        Assert.Equal(board.Id, File.ReadAllText(TeamPaths.TaskReplyPathFor(leeSess.Root.Id)).Trim());
        Task(leeSess, "assign", "Nest the bots under the team row", bot: "ada", id: board.Id);
        Task(leeSess, "mine", "Review Ada's change", status: "doing", id: board.Id);
        Task(adaSess, "mine", status: "doing", note: "chevron in, CSS next");
        Assert.Equal("doing", board.ItemOf("ada")!.Status);
        Assert.Equal("Nest the bots under the team row", board.ItemOf("ada")!.Title);
        Assert.Equal("chevron in, CSS next", board.ItemOf("ada")!.Note);
        // The board reaches every bot with its prompt, phrased for its role.
        var adaCtx = File.ReadAllText(h.Store.ContextPathFor("ada"));
        Assert.Contains($"- Task {board.Id}: **Ship the sidebar** — open", adaCtx);
        Assert.Contains("  - Ada (you): [doing] Nest the bots under the team row — chevron in, CSS next", adaCtx);
        Assert.Contains("Lee (`lee`) leads", adaCtx);
        Assert.Contains("perch team task done <id>", File.ReadAllText(h.Store.ContextPathFor("lee")));
        Assert.DoesNotContain("perch team task done <id>", adaCtx);
        // Persisted beside team.json, shared.
        Assert.True(File.Exists(h.Store.TasksPath));
        Assert.Contains("Ship the sidebar", File.ReadAllText(h.Store.TasksPath));

        // A second task, so "mine" without an id is ambiguous for Ada once she
        // has a piece on both; the lead needs the id to close.
        Task(leeSess, "new", "Dark mode for the room");
        var second = h.Store.Tasks.Open.Single(b => b.Title == "Dark mode for the room");
        Task(leeSess, "assign", "Theme tokens", bot: "ada", id: second.Id);
        Task(adaSess, "mine", status: "done");
        Assert.Contains(h.Ledger, e => e.Event == "error" && e.Text.Contains("without saying which task"));
        Task(adaSess, "mine", status: "done", id: board.Id);
        Assert.Equal("done", board.ItemOf("ada")!.Status);
        Task(leeSess, "done");
        Assert.Contains(h.Ledger, e => e.Event == "error" && e.Text.Contains("without saying which task"));

        // The lead closes the first → review; the owner says not yet → open,
        // and the lead gets the note as an owner post, numbered.
        Task(leeSess, "done", id: board.Id);
        Assert.Equal("review", board.Status);
        Assert.Contains(h.Ledger, e => e.Event == "task.review" && e.TaskId == board.Id && e.Text.StartsWith("Lee says \"Ship the sidebar\" is done"));
        h.Typed.Clear();
        h.Ctrl.OnTaskReject(new TeamTaskRejectMsg { ProjectId = h.Project.Id, TaskId = board.Id, Note = "the footer still shifts" });
        Assert.Equal("open", board.Status);
        var (toLee, line) = Assert.Single(h.Typed);
        Assert.Equal(leeSess.Id, toLee);
        Assert.Matches(@"^\[Perch team\] #\d+ Joseph → @Lee: Not done yet \(task " + board.Id + @"\): the footer still shifts$", line);

        // Second time: the owner confirms. Ada also has a piece on the second
        // task, so only Lee wraps up and resets; Ada is told and carries on.
        Task(leeSess, "done", id: board.Id);
        h.Typed.Clear();
        h.Ctrl.OnTaskConfirm(new TeamTaskConfirmMsg { ProjectId = h.Project.Id, TaskId = board.Id });
        Assert.Equal("done", board.Status);
        Assert.Equal(2, h.Typed.Count);
        Assert.Contains(h.Typed, t => t.Session == leeSess.Id && t.Line.Contains("Wrap up now: update your memory file"));
        Assert.Contains(h.Typed, t => t.Session == adaSess.Id && t.Line.Contains("You still have a piece on another open task"));
        // Confirming takes the card off the board THERE AND THEN: only the
        // second one is left, whatever the bots are still doing about it.
        var tasks = JsonSerializer.SerializeToElement(h.Ctrl.ProjectTeamView(h.Project.Id)!).GetProperty("tasks");
        Assert.Equal(second.Id, Assert.Single(tasks.EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal("Ship the sidebar", Assert.Single(h.Store.Tasks.Done).Title);
        Assert.Contains(h.Ledger, e => e.Event == "task" && e.TaskId == board.Id && e.Text.Contains("is off the board"));

        // A Done BEFORE the post echo is the old turn ending: no reset. The
        // echo, then the turn end: /clear is typed, the room says reset.
        h.Typed.Clear();
        h.Status(leeSess, "done");
        Assert.Empty(h.Typed);
        h.Status(leeSess, "working", "[Perch team] Joseph → @everyone: The task");
        h.Status(leeSess, "done");
        var (resetSess, cleared) = Assert.Single(h.Typed);
        Assert.Equal(leeSess.Id, resetSess);
        Assert.Equal("/clear", cleared);
        Assert.Contains(h.Ledger, e => e.Event == "reset" && e.Text == "Lee reset for the next task");
        // The second stays open, with Ada on it.
        Assert.Same(second, Assert.Single(h.Store.Tasks.Open));
        Assert.Contains($"- Task {second.Id}: **Dark mode for the room** — open", File.ReadAllText(h.Store.ContextPathFor("lee")));
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
    }

    [Fact]
    public async Task TaskBoard_TheOwnerCanOpenAndRenameATask_WithoutALead()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        h.Ctrl.OnTaskSet(new TeamTaskSetMsg { ProjectId = h.Project.Id, Title = "Fix the login flow" });
        var board = Assert.Single(h.Store.Tasks.Open);
        Assert.Equal("you", board.SetBy);
        Assert.Contains(h.Ledger, e => e.Event == "task" && e.TaskId == board.Id && e.Text == "Task opened by Joseph: Fix the login flow");
        // Set again = a second card, not a rename; rename is its own verb.
        h.Ctrl.OnTaskSet(new TeamTaskSetMsg { ProjectId = h.Project.Id, Title = "Signup flow" });
        Assert.Equal(2, h.Store.Tasks.Open.Count);
        h.Ctrl.OnTaskRename(new TeamTaskRenameMsg { ProjectId = h.Project.Id, TaskId = board.Id, Title = "Fix the login and signup flows" });
        Assert.Equal("Fix the login and signup flows", board.Title);
        Assert.Contains(h.Ledger, e => e.Event == "task" && e.Text == "Joseph renamed the task: Fix the login and signup flows");
        // A bot with no session (not running) has nothing to wrap: confirm
        // archives that card at once; the other stays.
        var sess = h.Sessions.Single();
        h.Sessions.Clear();
        h.Ctrl.OnSessionClosed(sess);
        h.Ctrl.OnTaskConfirm(new TeamTaskConfirmMsg { ProjectId = h.Project.Id, TaskId = board.Id });
        Assert.Single(h.Store.Tasks.Open);
        Assert.Equal("Fix the login and signup flows", Assert.Single(h.Store.Tasks.Done).Title);
    }

    [Fact]
    public async Task ACard_LeavesTheBoardWhenItIsConfirmed_AndCanBeForcedOff()
    {
        var h = new Harness();
        var lee = await h.CreateBot("Lee", position: "Team lead");
        await h.CreateBot("Ada");
        var leeSess = h.Sessions[0];
        void Task(Session s, string op, string? title = null, string? bot = null, string? id = null, string? status = null)
            => h.Ctrl.OnTeamTask(s, s.Root.Id, new TeamTaskMessage(op, bot, title, status, null, id));

        Task(leeSess, "new", "Card A");
        var a = h.Store.Tasks.Open.Single(b => b.Title == "Card A");
        Task(leeSess, "assign", "A piece", bot: "ada", id: a.Id);
        Task(leeSess, "new", "Card B");
        var b2 = h.Store.Tasks.Open.Single(x => x.Title == "Card B");
        Task(leeSess, "assign", "B piece", bot: "ada", id: b2.Id);

        // Ada holds a piece on B, so she does not wrap A up — and that must not
        // keep A on the board. Confirming closes A at once.
        Task(leeSess, "done", id: a.Id);
        h.Ctrl.OnTaskConfirm(new TeamTaskConfirmMsg { ProjectId = h.Project.Id, TaskId = a.Id });
        Assert.Same(b2, Assert.Single(h.Store.Tasks.Open));
        Assert.Equal("Card A", Assert.Single(h.Store.Tasks.Done).Title);

        // The lead can close a teammate's piece: --status on an assign, with no
        // title, so an abandoned piece can't hold a card open.
        Task(leeSess, "assign", null, bot: "ada", id: b2.Id, status: "done");
        Assert.Equal("done", b2.ItemOf("ada")!.Status);
        Assert.Equal("B piece", b2.ItemOf("ada")!.Title);

        // A card left stuck in "done" by an older build: closing it again
        // clears it instead of arguing.
        var stuck = new TaskBoard { Id = "deadbeef", Title = "Stuck", Status = "done", SetBy = "lee" };
        h.Store.Tasks.Open.Add(stuck);
        Task(leeSess, "done", id: stuck.Id);
        Assert.DoesNotContain(h.Store.Tasks.Open, x => x.Id == "deadbeef");
        Assert.DoesNotContain(h.Ledger, e => e.Event == "error" && e.Text.Contains("already"));

        // And Joseph can take any card off the board himself.
        h.Ctrl.OnTaskClose(new TeamTaskCloseMsg { ProjectId = h.Project.Id, TaskId = b2.Id });
        Assert.Empty(h.Store.Tasks.Open);
        Assert.Contains(h.Ledger, e => e.Event == "task" && e.TaskId == b2.Id && e.Text.Contains("Joseph took"));
        Assert.Equal(lee.Slug, h.Store.Doc.LeadSlug);
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
    }

    [Fact]
    public async Task AHandEditOfTasksJson_IsWhatTheBoardShows()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        h.Ctrl.OnTaskSet(new TeamTaskSetMsg { ProjectId = h.Project.Id, Title = "Fix the login flow" });
        Assert.Single(h.Store.Tasks.Open);

        // Joseph deletes the card in the file. Perch must not write its own
        // copy back over that — the file is the board.
        File.WriteAllText(h.Store.TasksPath, "{\"v\":2,\"open\":[],\"done\":[]}");
        File.SetLastWriteTimeUtc(h.Store.TasksPath, DateTime.UtcNow.AddSeconds(2));
        System.Threading.Thread.Sleep(TeamStore.TasksDiskCheckMs + 50);
        Assert.Empty(h.Store.Tasks.Open);
        h.Store.SaveTasks();
        Assert.DoesNotContain("Fix the login flow", File.ReadAllText(h.Store.TasksPath));
        TeamMarkers.Clear(h.Sessions.Single().Root.Id);
    }

    [Fact]
    public async Task ALegacyTasksFile_WithOneCurrentBoard_BecomesTheFirstOpenCard()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        File.WriteAllText(h.Store.TasksPath,
            "{\"v\":1,\"current\":{\"id\":\"abcd1234\",\"title\":\"Old task\",\"status\":\"open\",\"setBy\":\"you\",\"createdAtMs\":1,\"items\":[]},\"done\":[]}");
        h.Store.Reload();
        var board = Assert.Single(h.Store.Tasks.Open);
        Assert.Equal("abcd1234", board.Id);
        Assert.Equal("Old task", board.Title);
        Assert.Equal(2, h.Store.Tasks.V);
        TeamMarkers.Clear(h.Sessions.Single().Root.Id);
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
    public async Task PeerMsg_AFailedSend_IsPassedOnByPerch_NotDropped()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        await h.CreateBot("Bo", slug: "frontend-dev");
        var ada = h.Sessions[0];
        var bo = h.Sessions[1];

        // Claude Code refused the send because a nickname is not unique — an
        // earlier run's session answers to "bo" too. Perch knows exactly who
        // was meant, so the message is typed into Bo instead of being dropped:
        // a hand-off on the floor is how the live team stalled.
        h.Typed.Clear();
        h.Ctrl.OnPeerMsg(ada, ada.Root.Id, new PeerMsgMessage("sent", "bo", "…", false,
            "FYI: stand down on the ticket definitions", "FYI: stand down",
            "2 agents are named 'bo'. Re-send with the ref of the one you mean"));
        var (to, line) = Assert.Single(h.Typed);
        Assert.Equal(bo.Id, to);
        Assert.Equal("[Perch team] Ada → you: FYI: stand down on the ticket definitions", line);
        var peer = h.Ledger.Single(e => e.Kind == "peer");
        Assert.Equal("stand down on the ticket definitions", peer.Text);
        Assert.True(peer.Ok);
        Assert.Contains(h.Ledger, e => e.Event == "routed" && e.Text.Contains("Perch passed it on"));
        Assert.DoesNotContain(h.Ledger, e => e.Event == "peer.failed");

        // Nobody to pass it to: then it is one quiet line, as before.
        h.Ctrl.OnPeerMsg(ada, ada.Root.Id, new PeerMsgMessage("sent", "someone-else", "…", false,
            "FYI: nobody here", "FYI", "No agent named 'someone-else' is reachable."));
        var failed = h.Ledger.Single(e => e.Event == "peer.failed");
        Assert.Equal("ada", failed.From);
        Assert.Contains("couldn't reach", failed.Text);

        // The bot's own retry lands the same body seconds later: that is the
        // message the room already shows, not a second one.
        h.Ctrl.OnPeerMsg(ada, ada.Root.Id, new PeerMsgMessage("sent", "bo [7d21]", "…", true,
            "FYI: stand down on the ticket definitions", "FYI: stand down"));
        Assert.Single(h.Ledger, e => e.Kind == "peer");
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
    }

    [Fact]
    public async Task Artefact_FromABot_IsAFileAndACard_AndTheRoomCanOpenIt()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var ada = h.Sessions.Single();
        var src = Path.Combine(h.Repo, "draft.md");
        File.WriteAllText(src, "# Bid shading ETL\nOne row per outgoing bid.\n\n- label: lossprice\n");

        h.Ctrl.OnTeamArtefact(ada, ada.Root.Id, new TeamArtefactMessage(null, "for Galina", src, null, null));
        var card = h.Ledger.Single(e => e.Kind == "artefact");
        Assert.Equal("ada", card.From);
        Assert.Equal("Bid shading ETL", card.Text);          // the file's own heading
        Assert.Equal("md", card.Note);
        Assert.Equal("for Galina", card.Summary);
        Assert.True(File.Exists(h.Store.ArtefactPathFor(card.Target!, "md")));
        Assert.Contains(("artefact", "Bid shading ETL"), h.PostedEntries());

        // Opening it by id gives the body back.
        h.Posted.Clear();
        h.Ctrl.OnArtefactOpen(new TeamArtefactOpenMsg { ProjectId = h.Project.Id, Id = card.Target! });
        var data = JsonSerializer.SerializeToElement(h.Posted.Single());
        Assert.Equal("team.artefact.data", data.GetProperty("type").GetString());
        Assert.Contains("One row per outgoing bid", data.GetProperty("content").GetString());
        Assert.False(data.GetProperty("truncated").GetBoolean());

        // An id the room doesn't have is an error, not a crash.
        h.Posted.Clear();
        h.Ctrl.OnArtefactOpen(new TeamArtefactOpenMsg { ProjectId = h.Project.Id, Id = "nope1234" });
        Assert.Equal("The room doesn't have that artefact.",
            JsonSerializer.SerializeToElement(h.Posted.Single()).GetProperty("error").GetString());

        // Text inline, with a title of its own.
        h.Ctrl.OnTeamArtefact(ada, ada.Root.Id, new TeamArtefactMessage("Counter table", null, null, "| a | b |\n|---|---|\n| 1 | 2 |", null));
        Assert.Equal(2, h.Ledger.Count(e => e.Kind == "artefact"));

        // The menu: newest first, and one whose file was deleted is dropped.
        var gone = h.Ledger.Last(e => e.Kind == "artefact");
        File.Delete(h.Store.ArtefactPathFor(gone.Target!, gone.Note!));
        h.Posted.Clear();
        h.Ctrl.OnArtefactList(new TeamArtefactListMsg { ProjectId = h.Project.Id });
        var index = JsonSerializer.SerializeToElement(h.Posted.Single());
        Assert.Equal("team.artefact.index", index.GetProperty("type").GetString());
        var item = Assert.Single(index.GetProperty("items").EnumerateArray());
        Assert.Equal(card.Target, item.GetProperty("id").GetString());
        Assert.Equal("Bid shading ETL", item.GetProperty("title").GetString());

        // Only text formats: an .exe is refused, and nothing is stored.
        var exe = Path.Combine(h.Repo, "setup.exe");
        File.WriteAllText(exe, "MZ");
        h.Ctrl.OnTeamArtefact(ada, ada.Root.Id, new TeamArtefactMessage("Installer", null, exe, null, null));
        Assert.Equal(2, h.Ledger.Count(e => e.Kind == "artefact"));
        Assert.Contains(h.Ledger, e => e.Event == "error" && e.Text.Contains("the room shows text files"));
        TeamMarkers.Clear(ada.Root.Id);
    }

    [Fact]
    public async Task ALongTeamPost_BecomesAnArtefact_NotAWallOfText()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var ada = h.Sessions.Single();

        // Short: still a note.
        h.Ctrl.OnTeamPost(ada, ada.Root.Id, new TeamPostMessage("Sidebar spacing is on main now."));
        Assert.Single(h.Ledger, e => e.Kind == "note");

        // Long: stored, with its first line as the title and the next as the
        // summary, and nothing of it in the feed.
        var body = "DRAFT 1 v2 (ETL, for Galina)\nSummary: bid-level prepared table\n"
                 + string.Join("\n", Enumerable.Range(0, 40).Select(i => $"- line {i} of the scope"));
        h.Ctrl.OnTeamPost(ada, ada.Root.Id, new TeamPostMessage(body));
        var card = h.Ledger.Single(e => e.Kind == "artefact");
        Assert.Equal("DRAFT 1 v2 (ETL, for Galina)", card.Text);
        Assert.Equal("Summary: bid-level prepared table", card.Summary);
        Assert.Single(h.Ledger, e => e.Kind == "note");
        Assert.Equal(body, File.ReadAllText(h.Store.ArtefactPathFor(card.Target!, "md")));

        // A picture keeps its post: the caption belongs with the picture.
        var png = Path.Combine(h.Repo, "shot.png");
        File.WriteAllBytes(png, new byte[] { 1, 2, 3 });
        h.Ctrl.OnTeamPost(ada, ada.Root.Id, new TeamPostMessage(body, png));
        Assert.Equal(2, h.Ledger.Count(e => e.Kind == "note"));
        TeamMarkers.Clear(ada.Root.Id);
    }

    [Fact]
    public async Task AnArtefactOver256Kb_IsCutWithAMarker()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var ada = h.Sessions.Single();
        h.Ctrl.OnTeamArtefact(ada, ada.Root.Id,
            new TeamArtefactMessage("Huge log", null, null, new string('x', TeamController.ArtefactMaxBytes + 5000), "log"));
        var card = h.Ledger.Single(e => e.Kind == "artefact");
        Assert.Contains("cut at 256 KB", card.Summary);
        var text = File.ReadAllText(h.Store.ArtefactPathFor(card.Target!, "log"));
        Assert.True(text.Length < TeamController.ArtefactMaxBytes + 200);
        Assert.EndsWith("over 256 KB)", text);

        // The cap is bytes, so a body of emoji and accents is cut to fit and
        // never splits a character in half.
        var wide = string.Concat(Enumerable.Repeat("é🐦", 100));
        var cut = TeamController.CutToBytes(wide, 61);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(cut) <= 61);
        Assert.Equal(cut, new string(cut.ToCharArray()));            // still valid text
        Assert.False(char.IsHighSurrogate(cut[^1]));
        TeamMarkers.Clear(ada.Root.Id);
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
        // The tab exists now but its pane has no terminal yet (the page has
        // not laid it out) — the second post must queue, not be typed.
        h.NoPty.Add(h.Sessions.Single().Root.Id);
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

    [Fact]
    public async Task StartupQuestion_BecomesACard_AndTheAnswerPressesTheKeys()
    {
        var h = new Harness();
        var bot = await h.CreateBot("Ada");
        var sess = h.Sessions.Single();
        h.Ctrl.OnPromptStuck(sess, sess.Root.Id);

        var ask = h.Ledger.Single(e => e.Event == "trust");
        Assert.Equal("ada", Assert.Single(ask.To!));
        Assert.Contains("trust its new folder", ask.Text);
        Assert.Contains("[waiting for the owner to answer its start-up question]", File.ReadAllText(h.Store.RosterPath));

        // "Trust folder": Down (select "Yes, I trust this folder"), then Enter.
        h.Ctrl.OnBotAnswer(new TeamBotAnswerMsg { ProjectId = h.Project.Id, BotId = bot.Slug, Answer = "trust" });
        var (pane, bytes) = Assert.Single(h.Raw);
        Assert.Equal(sess.Root.Id, pane);
        Assert.Equal(new byte[] { 0x1b, (byte)'[', (byte)'B', (byte)'\r' }, bytes);
        var done = h.Ledger.Single(e => e.Event == "trusted");
        Assert.Equal("You trusted the folder for Ada", done.Text);
        Assert.DoesNotContain("start-up question", File.ReadAllText(h.Store.RosterPath));
        TeamMarkers.Clear(sess.Root.Id);
    }

    [Fact]
    public async Task StartupQuestion_Declined_TakesTheDialogsDefault()
    {
        var h = new Harness();
        var bot = await h.CreateBot("Bo");
        var sess = h.Sessions.Single();
        h.Ctrl.OnPromptStuck(sess, sess.Root.Id);
        h.Ctrl.OnBotAnswer(new TeamBotAnswerMsg { ProjectId = h.Project.Id, BotId = bot.Slug, Answer = "exit" });
        var (_, bytes) = Assert.Single(h.Raw);
        Assert.Equal(new byte[] { (byte)'\r' }, bytes);
        Assert.Contains(h.Ledger, e => e.Event == "exited" && e.Text == "You told Bo not to start");
        TeamMarkers.Clear(sess.Root.Id);
    }

    [Fact]
    public async Task StartupQuestion_ForANonBotPane_IsIgnored()
    {
        var h = new Harness();
        var stray = new Session { Title = "just a tab" };
        h.Sessions.Add(stray);
        h.Ctrl.OnPromptStuck(stray, stray.Root.Id);
        Assert.Null(h.Ctrl.StoreFor(h.Project.Id));   // no team was even created
        Assert.Empty(h.Raw);
    }

    // ---- Milestone B: cards, images, reactions, addresses -----------------

    [Fact]
    public async Task APost_CarriesItsRoomNumber_IntoTheBotsTerminal()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        h.Post("please fix the sidebar", "[\"Ada\"]");
        var post = h.Ledger.Single(e => e.Kind == "user");
        var (_, line) = Assert.Single(h.Typed);
        Assert.Equal($"[Perch team] #{post.Seq} Joseph → @Ada: please fix the sidebar", line);
        TeamMarkers.Clear(h.Sessions.Single().Root.Id);
    }

    [Fact]
    public async Task APostToAColdBot_WaitsForItsClaude_ThenLands()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var sess = h.Sessions.Single();
        h.NoPty.Add(sess.Root.Id);  // the tab exists but has no terminal yet

        h.Post("wake up and read the brief", "[\"Ada\"]");
        // Nothing is typed into a pane that is only just starting: the shell
        // answers long before Claude does, and a line typed into that gap is
        // lost (this is what dropped two posts in the live room).
        Assert.Empty(h.Typed);
        Assert.Contains(sess.Id, h.Started);
        Assert.False(h.Ledger.Single(e => e.Kind == "user").Delivered);

        // A status from the booting pane must NOT release it either: the
        // flush it schedules finds the pane still starting and types nothing.
        h.Status(sess, "working");
        h.RunLastDelayed();
        Assert.Empty(h.Typed);

        // The session-start hook is the go-ahead: now it goes in.
        h.NoPty.Clear();
        h.Ctrl.OnAgentUp(sess);
        h.RunLastDelayed();
        var (to, line) = Assert.Single(h.Typed);
        Assert.Equal(sess.Id, to);
        Assert.Contains("wake up and read the brief", line);
        TeamMarkers.Clear(sess.Root.Id);
    }

    [Fact]
    public async Task APostThatNeverLands_IsHeldForTheBotsFreeMoments_AndCanBeSentAgainFromTheRoom()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var sess = h.Sessions.Single();
        h.Post("did you see the ticket?", "[\"Ada\"]");
        Assert.Single(h.Typed);

        // No echo through the budget: the line is never typed again on its
        // own (that sent a post twice) — it is held, and the room hears why.
        for (var i = 0; i < TeamController.SubmitChecks.Length; i++) h.RunLastDelayed();
        Assert.Single(h.Typed);
        var waiting = h.Ledger.Single(e => e.Event == "waiting");
        Assert.Contains("mid-task", waiting.Text);
        var postSeq = h.Ledger.Single(e => e.Kind == "user").Seq;
        Assert.Equal(postSeq.ToString(), waiting.Note);

        // Three free moments, each an Enter and a round of checks, none of
        // them confirmed: now it says so, naming the post.
        for (var hold = 1; hold <= TeamController.HoldLimit; hold++)
        {
            h.Status(sess, "done");
            h.RunLastDelayed();                                  // ResumeHeld: Enter
            for (var i = 0; i < TeamController.SubmitChecks.Length; i++) h.RunLastDelayed();
        }
        Assert.Single(h.Typed);
        Assert.Single(h.Ledger, e => e.Event == "waiting");      // told once, not on every hold
        var stuck = h.Ledger.Single(e => e.Event == "undelivered");
        Assert.Equal(postSeq.ToString(), stuck.Note);
        Assert.Contains("send it again from here", stuck.Text);

        // "Send again" types the very same line, with no second post.
        h.Typed.Clear();
        h.Ctrl.OnDeliverRetry(new TeamDeliverRetryMsg { ProjectId = h.Project.Id, Seq = postSeq, BotId = "ada" });
        var (_, again) = Assert.Single(h.Typed);
        Assert.Contains("did you see the ticket?", again);
        Assert.Single(h.Ledger, e => e.Kind == "user");
        Assert.Contains(h.Ledger, e => e.Event == "delivered" && e.Text == "Sent to Ada again");
        TeamMarkers.Clear(sess.Root.Id);
    }

    /// A bot mid-turn: Claude Code puts a typed line in its composer and
    /// leaves it there until the turn ends, so the post is not typed at all
    /// — it is parked, the room says the bot is mid-task, and the turn's end
    /// types it (the hook then confirms it as usual).
    [Fact]
    public async Task APostToABotMidTurn_WaitsForTheTurnToEnd()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var sess = h.Sessions.Single();
        foreach (var leaf in PaneTree.AllLeaves(sess.Root)) leaf.AgentState = AgentState.Working;

        h.Post("one more thing", "[\"Ada\"]");
        Assert.Empty(h.Typed);
        var waiting = h.Ledger.Single(e => e.Event == "waiting");
        Assert.Contains("mid-task", waiting.Text);

        // Its tool calls keep reporting "working": still nothing typed.
        h.Status(sess, "working", "running npm");
        h.RunLastDelayed();                                  // the flush, which declines
        Assert.Empty(h.Typed);

        // The turn ends: typed, and a "delivered" row lands.
        foreach (var leaf in PaneTree.AllLeaves(sess.Root)) leaf.AgentState = AgentState.Done;
        h.Status(sess, "done");
        h.RunLastDelayed();                                  // the flush
        h.RunLastDelayed();                                  // ResumeHeld (nothing held)
        var (to, line) = Assert.Single(h.Typed);
        Assert.Equal(sess.Id, to);
        Assert.Contains("one more thing", line);
        Assert.Contains(h.Ledger, e => e.Event == "delivered" && e.Text == "Delivered to Ada");
        TeamMarkers.Clear(sess.Root.Id);
    }

    [Fact]
    public async Task PermissionRequest_IsACard_AndTheAnswerLandsWhereTheHookPolls()
    {
        var h = new Harness();
        var bot = await h.CreateBot("Ada");
        var sess = h.Sessions.Single();
        h.Ctrl.OnPermAsk(sess, sess.Root.Id, new PermAskMessage("p1", "Bash", "rm -rf build",
            "{\"command\":\"rm -rf build\"}", new[] { "Bash(rm *)" }));
        var card = h.Ledger.Single(e => e.Event == "permission");
        Assert.Equal("p1", card.Note);
        Assert.Equal("ada", Assert.Single(card.To!));
        Assert.Equal("Ada wants to run Bash: rm -rf build", card.Text);
        Assert.Equal("{\"command\":\"rm -rf build\"}", card.Summary);
        Assert.Contains("[waiting for your permission]", File.ReadAllText(h.Store.RosterPath));

        var path = TeamPaths.PermAnswerPathFor("p1");
        try
        {
            h.Ctrl.OnPermAnswer(new TeamPermAnswerMsg { ProjectId = h.Project.Id, Id = "p1", Decision = "allow" });
            // Answered here means no terminal dialog will ever be dismissed:
            // the pane's "on a prompt" state must be dropped by the answer.
            Assert.Contains(sess.Root.Id, h.Cleared);
            // …and a prompt notice arriving right after is recognised as that
            // same, already-settled prompt.
            Assert.True(h.Ctrl.PromptAnsweredRecently(sess.Root.Id));
            Assert.False(h.Ctrl.PromptAnsweredRecently(Guid.NewGuid()));
            Assert.Equal("allow", File.ReadAllText(path).Trim());
            var done = h.Ledger.Single(e => e.Event == "permission.answered");
            Assert.Equal("You allowed Ada", done.Text);
            Assert.DoesNotContain("waiting for your permission", File.ReadAllText(h.Store.RosterPath));

            h.Ctrl.OnPermAsk(sess, sess.Root.Id, new PermAskMessage("p2", "Edit", @"C:\repo\a.ts"));
            h.Ctrl.OnPermAnswer(new TeamPermAnswerMsg { ProjectId = h.Project.Id, Id = "p2", Decision = "deny" });
            Assert.Equal("deny", File.ReadAllText(TeamPaths.PermAnswerPathFor("p2")).Trim());
            Assert.Contains(h.Ledger, e => e.Event == "permission.answered" && e.Text == "You denied Ada");

            h.Ctrl.OnPermDenied(sess, sess.Root.Id, new PermDeniedMessage("Bash", "curl evil", "classifier"));
            Assert.Contains(h.Ledger, e => e.Event == "denied" && e.From == "ada" && e.Text.StartsWith("Ada: auto mode blocked Bash: curl evil"));

            // The hook's answer didn't settle it: Claude's own prompt is on the
            // bot's screen, so the answer is pressed there too. (Without this,
            // an Allow given ten seconds after the card appeared did nothing
            // and the bot waited for ever.)
            h.Raw.Clear();
            h.Ctrl.OnPermAsk(sess, sess.Root.Id, new PermAskMessage("p5", "Bash", "git tag -l x"));
            h.Ctrl.OnPermAnswer(new TeamPermAnswerMsg { ProjectId = h.Project.Id, Id = "p5", Decision = "allow" });
            var onScreen = h.Delayed[^1];
            onScreen();
            var (pane, keys) = Assert.Single(h.Raw);
            Assert.Equal(sess.Root.Id, pane);
            Assert.Equal(new byte[] { (byte)'\r' }, keys);   // Enter takes the highlighted "yes"
            Assert.Contains(h.Ledger, e => e.Text.Contains("still showing the question"));
            File.Delete(TeamPaths.PermAnswerPathFor("p5"));

            // …and when the bot moves on by itself, nothing is pressed.
            h.Raw.Clear();
            h.Ctrl.OnPermAsk(sess, sess.Root.Id, new PermAskMessage("p6", "Bash", "git tag -l y"));
            h.Ctrl.OnPermAnswer(new TeamPermAnswerMsg { ProjectId = h.Project.Id, Id = "p6", Decision = "allow" });
            var stale = h.Delayed[^1];
            h.Status(sess, "working");     // the tool call resumed: the hook's answer took
            stale();
            Assert.Empty(h.Raw);
            File.Delete(TeamPaths.PermAnswerPathFor("p6"));

            // An answer that never comes: the hook gives up, Claude asks in the
            // bot's own terminal, and the room says so instead of leaving a
            // card whose Allow would now do nothing.
            h.Ctrl.OnPermAsk(sess, sess.Root.Id, new PermAskMessage("p3", "Bash", "git push"));
            var timer = h.Delayed.Last();
            h.Delayed.Clear();
            timer();
            var expired = h.Ledger.Single(e => e.Event == "permission.expired");
            Assert.Equal("p3", expired.Note);
            Assert.Equal("ada", expired.From);
            Assert.Contains("waited ten minutes", expired.Text);
            // Answered in time, the timer says nothing.
            h.Ctrl.OnPermAsk(sess, sess.Root.Id, new PermAskMessage("p4", "Bash", "git status"));
            var timer4 = h.Delayed.Last();
            h.Ctrl.OnPermAnswer(new TeamPermAnswerMsg { ProjectId = h.Project.Id, Id = "p4", Decision = "allow" });
            timer4();
            Assert.Single(h.Ledger, e => e.Event == "permission.expired");
            File.Delete(TeamPaths.PermAnswerPathFor("p4"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(TeamPaths.PermAnswerPathFor("p2"));
            TeamMarkers.Clear(sess.Root.Id);
        }
        Assert.Equal("ada", bot.Slug);
    }

    [Fact]
    public async Task AskCard_TheAnswerGoesBackToTheBot_AsAPost()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var sess = h.Sessions.Single();
        h.Ctrl.OnTeamAsk(sess, sess.Root.Id, new TeamAskMessage("q1", "Ship the dark footer?", new[] { "Ship it", "Hold" }));
        var card = h.Ledger.Single(e => e.Event == "ask");
        Assert.Equal("q1", card.Note);
        Assert.Equal(new[] { "Ship it", "Hold" }, card.Choices!.ToArray());
        Assert.Contains("[waiting for your answer]", File.ReadAllText(h.Store.RosterPath));

        h.Typed.Clear();
        h.Ctrl.OnAskAnswer(new TeamAskAnswerMsg { ProjectId = h.Project.Id, Id = "q1", Answer = "Ship it" });
        Assert.Contains(h.Ledger, e => e.Event == "ask.answered" && e.Text == "You answered Ada: Ship it");
        var post = h.Ledger.Single(e => e.Kind == "user");
        Assert.Equal("Ship it", post.Text);
        Assert.Equal("ada", Assert.Single(post.To!));
        var (_, line) = Assert.Single(h.Typed);
        Assert.Equal($"[Perch team] #{post.Seq} Joseph → @Ada: Ship it", line);
        Assert.DoesNotContain("waiting for your answer", File.ReadAllText(h.Store.RosterPath));
        TeamMarkers.Clear(sess.Root.Id);
    }

    [Fact]
    public async Task ANote_CanCarryAPicture_AndTheRoomCanFetchIt()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var sess = h.Sessions.Single();
        var png = Path.Combine(h.Repo, "shot.png");
        File.WriteAllBytes(png, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 3 });
        h.Ctrl.OnTeamPost(sess, sess.Root.Id, new TeamPostMessage("The footer in dark mode", png));
        var note = h.Ledger.Single(e => e.Kind == "note");
        Assert.Equal(png, note.Image);
        // A missing or non-image path is dropped; the words still land.
        h.Ctrl.OnTeamPost(sess, sess.Root.Id, new TeamPostMessage("no pic", Path.Combine(h.Repo, "nope.png")));
        Assert.Null(h.Ledger.Last(e => e.Kind == "note").Image);

        var before = h.Posted.Count;
        h.Ctrl.OnImage(new TeamImageMsg { ProjectId = h.Project.Id, Path = png });
        var reply = JsonSerializer.SerializeToElement(h.Posted[before]);
        Assert.Equal("team.image.data", reply.GetProperty("type").GetString());
        Assert.Equal("image/png", reply.GetProperty("mediaType").GetString());
        Assert.Equal(Convert.ToBase64String(File.ReadAllBytes(png)), reply.GetProperty("data").GetString());
        h.Ctrl.OnImage(new TeamImageMsg { ProjectId = h.Project.Id, Path = Path.Combine(h.Repo, "secrets.txt") });
        Assert.Equal("Not an image file.", JsonSerializer.SerializeToElement(h.Posted[^1]).GetProperty("error").GetString());
        TeamMarkers.Clear(sess.Root.Id);
    }

    [Fact]
    public async Task PeerTargets_ResolveByName_Address_OrDisambiguatedName()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        await h.CreateBot("Bo", slug: "frontend-dev");
        var ada = h.Sessions[0];
        var bo = h.Sessions[1];
        bo.Root.MessagingSocket = @"\\.\pipe\LOCAL\cc-msg-8d1c2eab";
        var store = h.Store;

        Assert.Equal("bo", h.Ctrl.ResolvePeerTarget(store, "bo")!.Slug);
        Assert.Equal("bo", h.Ctrl.ResolvePeerTarget(store, "Bo")!.Slug);
        Assert.Equal("bo", h.Ctrl.ResolvePeerTarget(store, "bo [7d217e]")!.Slug);
        Assert.Equal("bo", h.Ctrl.ResolvePeerTarget(store, @"uds:\\.\pipe\LOCAL\cc-msg-8d1c2eab")!.Slug);
        Assert.Equal("bo", h.Ctrl.ResolvePeerTarget(store, "uds://./pipe/LOCAL/cc-msg-8d1c2eab")!.Slug);
        Assert.Null(h.Ctrl.ResolvePeerTarget(store, @"uds:\\.\pipe\LOCAL\cc-msg-ffffffff"));

        // In the room: the handoff prefix becomes the label, and an address
        // that is nobody's here reads as "(another session)".
        h.Ctrl.OnPeerMsg(ada, ada.Root.Id, new PeerMsgMessage("sent", @"uds:\\.\pipe\LOCAL\cc-msg-8d1c2eab", "…", true,
            "HANDOFF: wire the footer toggle, src/web/footer.ts, by 15:00", "footer toggle"));
        var peer = h.Ledger.Last(e => e.Kind == "peer");
        Assert.Equal("bo", Assert.Single(peer.To!));
        Assert.Equal("handoff", peer.Note);
        Assert.Equal("wire the footer toggle, src/web/footer.ts, by 15:00", peer.Text);
        h.Ctrl.OnPeerMsg(ada, ada.Root.Id, new PeerMsgMessage("sent", "someone-else", "…", true, "hello there"));
        peer = h.Ledger.Last(e => e.Kind == "peer");
        Assert.Equal("(another session)", Assert.Single(peer.To!));
        Assert.Null(peer.Note);
        Assert.Equal("hello there", peer.Text);
        Assert.Equal(("report", "done"), TeamController.SplitHandoff("Report: done"));
        Assert.Equal((null, "REPORTING: x"), TeamController.SplitHandoff("REPORTING: x"));
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
    }

    [Fact]
    public async Task WorkHandedToATeammate_IsCopiedToTheLead_UntilACardCoversIt()
    {
        var h = new Harness();
        var lee = await h.CreateBot("Lee", position: "Team lead");
        await h.CreateBot("Ada");
        var leeSess = h.Sessions.Single(s => s.Root.PinnedPeerName == "lee");
        h.Typed.Clear();
        h.Post("add loading states to the KPI page", "[\"Ada\"]");
        Assert.Equal(2, h.Typed.Count);
        var cc = h.Typed.Single(t => t.Session == leeSess.Id).Line;
        Assert.Matches(@"^\[Perch team\] #\d+ Joseph → @Ada \(cc Lee for the board\): add loading states to the KPI page$", cc);
        Assert.Contains(h.Ledger, e => e.Event == "cc" && e.Text == "Copied to Lee for the board");

        // Once a card names Ada, her posts stop being copied.
        h.Ctrl.OnTeamTask(leeSess, leeSess.Root.Id, new TeamTaskMessage("new", null, "KPI loading states", null, null));
        var board = Assert.Single(h.Store.Tasks.Open);
        h.Ctrl.OnTeamTask(leeSess, leeSess.Root.Id, new TeamTaskMessage("assign", "ada", "skeletons", null, null, board.Id));
        h.Typed.Clear();
        h.Post("also the yearly tab", "[\"Ada\"]");
        Assert.Single(h.Typed);
        // Posts to the lead, or to everyone, are never copied.
        h.Typed.Clear();
        h.Post("status?", "[\"Lee\"]");
        Assert.Single(h.Typed);
        Assert.Equal("lee", lee.Slug);
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
    }

    [Fact]
    public async Task Reactions_ResolveTheirTarget_Dedupe_AndReachTheBot()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var ada = h.Sessions.Single();
        h.Post("ship the dark footer", "[\"Ada\"]");
        var post = h.Ledger.Single(e => e.Kind == "user");
        h.Ctrl.OnTeamPost(ada, ada.Root.Id, new TeamPostMessage("Footer is live on staging."));
        var note = h.Ledger.Single(e => e.Kind == "note");

        // A bot reacts to the owner's post by number, and to a teammate's
        // latest message by nickname (here its own note, for the resolution).
        h.Ctrl.OnTeamReact(ada, ada.Root.Id, new TeamReactMessage($"#{post.Seq}", "👀"));
        var r1 = h.Ledger.Last(e => e.Kind == "reaction");
        Assert.Equal("ada", r1.From);
        Assert.Equal(post.Seq.ToString(), r1.Note);
        Assert.Equal("👀", r1.Text);
        h.Ctrl.OnTeamReact(ada, ada.Root.Id, new TeamReactMessage("@Ada", "✏️"));
        Assert.Equal(note.Seq.ToString(), h.Ledger.Last(e => e.Kind == "reaction").Note);
        // The same reaction twice is one reaction; an unknown target is refused.
        h.Ctrl.OnTeamReact(ada, ada.Root.Id, new TeamReactMessage($"#{post.Seq}", "👀"));
        Assert.Equal(2, h.Ledger.Count(e => e.Kind == "reaction"));
        h.Ctrl.OnTeamReact(ada, ada.Root.Id, new TeamReactMessage("#9999", "👀"));
        Assert.Contains(h.Ledger, e => e.Event == "error" && e.Text.Contains("reacted to something the room doesn't have"));

        // The owner reacts to Ada's note: a reaction row, and one line to Ada.
        h.Typed.Clear();
        h.Ctrl.OnReact(new TeamReactMsg { ProjectId = h.Project.Id, Seq = note.Seq, Emoji = "✅" });
        var mine = h.Ledger.Last(e => e.Kind == "reaction");
        Assert.Equal("you", mine.From);
        var (_, line) = Assert.Single(h.Typed);
        Assert.Equal($"[Perch team] Joseph reacted ✅ to #{note.Seq} \"Footer is live on staging.\"", line);
        // Reacting to your own post tells nobody.
        h.Typed.Clear();
        h.Ctrl.OnReact(new TeamReactMsg { ProjectId = h.Project.Id, Seq = post.Seq, Emoji = "✅" });
        Assert.Empty(h.Typed);
        Assert.Equal(4, h.Ledger.Count(e => e.Kind == "reaction"));
        TeamMarkers.Clear(ada.Root.Id);
    }

    [Fact]
    public async Task ModelAtItsLimit_MovesTheBot_AndBackWhenItLifts_OncePerMinute()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        await h.CreateBot("Bo", slug: "frontend-dev");
        var ada = h.Sessions[0];
        var bo = h.Sessions[1];
        // Ada's pane says fable; Bo's pane says nothing, but its transcript does.
        ada.Root.Model = "fable";
        h.Transcript = pane => pane == bo.Root.Id
            ? new InspectorData(Array.Empty<InspectorEvent>(), new InspectorVitals("claude-fable-5-1", 1, 1, 0, 0, 0, 0, 0))
            : null;
        var clock = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        h.Ctrl.Now = () => clock;
        var resetAt = new DateTimeOffset(2026, 9, 3, 14, 5, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        // fable at its limit: both move to opus, once, and the room says so.
        var limited = new[] { new ModelUsageLimit("fable", true, resetAt) };
        h.Ctrl.OnModelLimits(limited);
        Assert.Equal(2, h.ModelSet.Count);
        Assert.Contains((ada.Root.Id, "opus"), h.ModelSet);
        Assert.Contains((bo.Root.Id, "opus"), h.ModelSet);
        var rows = h.Ledger.Where(e => e.Event == "model").ToList();
        Assert.Equal(2, rows.Count);
        var until = DateTimeOffset.FromUnixTimeMilliseconds(resetAt).ToLocalTime().ToString("HH:mm");
        Assert.Contains(rows, r => r.Text == $"fable is at its limit until {until} — Ada switched to opus");
        Assert.Contains($"Model limits right now: fable until {until}", File.ReadAllText(h.Store.RosterPath));
        Assert.Contains("Perch switches your model for you", File.ReadAllText(h.Store.RosterPath));

        // The same limit again a moment later: nothing more happens.
        h.Ctrl.OnModelLimits(limited);
        Assert.Equal(2, h.ModelSet.Count);
        // fable AND opus at limit, half a minute later: the bots are already
        // off fable and stay put (moving twice would be churn).
        clock = clock.AddSeconds(30);
        h.Ctrl.OnModelLimits(new[] { new ModelUsageLimit("fable", true, resetAt), new ModelUsageLimit("opus", true, null) });
        Assert.Equal(2, h.ModelSet.Count);

        // The limit lifts before a minute has passed since the switch:
        // nothing yet; after it, both go back and the room says so.
        h.Ctrl.OnModelLimits(Array.Empty<ModelUsageLimit>());
        Assert.Equal(2, h.ModelSet.Count);
        clock = clock.AddSeconds(31);
        h.Ctrl.OnModelLimits(Array.Empty<ModelUsageLimit>());
        Assert.Equal(4, h.ModelSet.Count);
        Assert.Contains((ada.Root.Id, "fable"), h.ModelSet.Skip(2));
        Assert.Contains(h.Ledger, e => e.Event == "model" && e.Text == "fable is back — Bo switched back");
        Assert.DoesNotContain("Model limits right now", File.ReadAllText(h.Store.RosterPath));

        // A bot whose model Perch can't tell is left alone.
        clock = clock.AddSeconds(61);
        ada.Root.Model = "";
        h.Transcript = _ => null;
        h.Ctrl.OnModelLimits(limited);
        Assert.Equal(4, h.ModelSet.Count);
        Assert.Equal("fable", TeamController.AliasFromModelId("claude-fable-5-1"));
        Assert.Equal("haiku", TeamController.AliasFromModelId("claude-haiku-4-5-20251001"));
        Assert.Null(TeamController.AliasFromModelId("gpt-9"));
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
    }

    /// A tab restored after a restart but never opened has no terminal: a
    /// post to that bot must start it (the host arms the resume and spawns),
    /// and the roster must say so rather than "idle".
    [Fact]
    public async Task Post_ToABotWhoseTabNeverStarted_StartsIt()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var sess = h.Sessions.Single();
        h.NoPty.Add(sess.Root.Id);
        h.Ctrl.OnRequest(new TeamRequestMsg { ProjectId = h.Project.Id });
        Assert.Contains("[not started]", File.ReadAllText(h.Store.RosterPath));

        h.Post("are you there?", "[\"Ada\"]");
        Assert.Equal(sess.Id, Assert.Single(h.Started));

        // Once the terminal is up, presence is back to normal.
        h.NoPty.Clear();
        h.Ctrl.OnRequest(new TeamRequestMsg { ProjectId = h.Project.Id });
        Assert.DoesNotContain("[not started]", File.ReadAllText(h.Store.RosterPath));
        TeamMarkers.Clear(sess.Root.Id);
    }

    /// A pasted picture rides on the post and is named in the typed line so
    /// the bot can Read it; a post that is only a picture is still a post.
    [Fact]
    public async Task Post_WithAPastedPicture_NamesTheFileForTheBot()
    {
        var h = new Harness();
        await h.CreateBot("Ada");
        var png = Path.Combine(h.Repo, "shot.png");
        File.WriteAllBytes(png, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        h.Ctrl.OnPost(new TeamPostMsg
        {
            ProjectId = h.Project.Id, Text = "does this look right?", ClientId = "i1",
            To = JsonDocument.Parse("[\"Ada\"]").RootElement.Clone(), Image = png,
        });
        var (_, line) = Assert.Single(h.Typed);
        Assert.Contains("does this look right? (attached picture: " + png + " — Read it if you need to see it)", line);
        var row = h.Ledger.Single(e => e.Kind == "user");
        Assert.Equal(png, row.Image);
        Assert.Equal("does this look right?", row.Text);

        // Picture only, no words: still delivered. A path that isn't a picture
        // (or doesn't exist) is dropped, never typed.
        h.Ctrl.OnPost(new TeamPostMsg { ProjectId = h.Project.Id, Text = "", ClientId = "i2", To = JsonDocument.Parse("[\"Ada\"]").RootElement.Clone(), Image = png });
        Assert.Equal(2, h.Typed.Count);
        Assert.StartsWith("[Perch team] #", h.Typed[1].Line);
        Assert.Contains("(attached picture:", h.Typed[1].Line);
        h.Ctrl.OnPost(new TeamPostMsg { ProjectId = h.Project.Id, Text = "", ClientId = "i3", To = JsonDocument.Parse("[\"Ada\"]").RootElement.Clone(), Image = Path.Combine(h.Repo, "nope.png") });
        Assert.Equal(2, h.Typed.Count);
        TeamMarkers.Clear(h.Sessions.Single().Root.Id);
    }
    // ---- a tag wakes a bot that has no tab here ----------------------------

    /// The pulled-team case: the bot is on the roster, nothing runs it on
    /// this machine. The owner's post opens its tab, waits for its Claude,
    /// then goes in — instead of "isn't running, so this didn't reach them".
    [Fact]
    public async Task APostToABotWithNoTabHereStartsItAndDeliversWhenItsClaudeIsUp()
    {
        var h = new Harness();
        var ada = await h.CreateBot("Ada");
        var first = h.Sessions.Single();
        h.Sessions.Clear();
        ada.SessionId = null;
        TeamMarkers.Clear(first.Root.Id);

        h.Post("did you save the report?", "[\"Ada\"]");

        var sess = Assert.Single(h.Sessions);          // a tab was opened for the post
        Assert.Equal(sess.Id, ada.SessionId);
        Assert.Empty(h.Typed);                          // nothing typed into a booting pane
        Assert.DoesNotContain(h.Ledger, e => e.Event == "undelivered");
        var waiting = Assert.Single(h.Ledger, e => e.Event == "waiting");
        Assert.Contains("starting it", waiting.Text);
        Assert.Contains(h.Ledger, e => e.Event == "joined" && e.Text.Contains("a post was addressed to it"));
        var post = h.Ledger.Single(e => e.Kind == "user");
        Assert.False(post.Delivered);
        // The bot's "joined" row must not have taken the post's number.
        Assert.Equal(post.Seq, h.Ledger.Single(e => e.Event == "waiting").Seq - 1);

        // The cold-start clock is armed (the overdue timer), then Claude reports.
        Assert.Single(h.Delayed);
        h.Ctrl.OnAgentUp(sess);
        h.RunLastDelayed();                             // the flush after the settle delay
        var typed = Assert.Single(h.Typed);
        Assert.Equal(sess.Id, typed.Session);
        Assert.Contains("did you save the report?", typed.Line);
        Assert.Contains($"#{post.Seq} ", typed.Line);   // the line names the post the room shows
        Assert.Contains(h.Ledger, e => e.Event == "delivered" && e.Note == post.Seq.ToString());
        TeamMarkers.Clear(sess.Root.Id);
    }

    /// A post to everyone reaches the whole roster, so on a pulled team it
    /// starts every bot that has no tab here — the owner chose that over a
    /// "Send again" click per bot (2026-09-05).
    [Fact]
    public async Task APostToEveryoneStartsEveryBotWithNoTabHere()
    {
        var h = new Harness();
        var ada = await h.CreateBot("Ada");
        var bo = await h.CreateBot("Bo");
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
        h.Sessions.Clear();
        ada.SessionId = null;
        bo.SessionId = null;

        h.Post("stand-up in five", "\"everyone\"");

        Assert.Equal(2, h.Sessions.Count);                 // a tab per bot
        Assert.Equal(2, h.Ledger.Count(e => e.Event == "waiting" && e.Text.Contains("starting it")));
        Assert.DoesNotContain(h.Ledger, e => e.Event == "undelivered");
        Assert.Empty(h.Typed);                              // nothing typed into booting panes
        Assert.Contains(h.Sessions, s => s.Id == ada.SessionId);
        Assert.Contains(h.Sessions, s => s.Id == bo.SessionId);
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
    }

    [Fact]
    public async Task ASecondPostWhileTheBotStartsQueuesBehindTheFirst()
    {
        var h = new Harness();
        var ada = await h.CreateBot("Ada");
        TeamMarkers.Clear(h.Sessions.Single().Root.Id);
        h.Sessions.Clear();
        ada.SessionId = null;

        h.Post("one", "[\"Ada\"]", "c1");
        // The tab exists now but its pane has no terminal yet (the page has
        // not laid it out) — the second post must queue, not be typed.
        h.NoPty.Add(h.Sessions.Single().Root.Id);
        h.Post("two", "[\"Ada\"]", "c2");

        var sess = Assert.Single(h.Sessions);          // one tab, not two
        h.Ctrl.OnAgentUp(sess);
        h.RunLastDelayed();
        Assert.Equal(2, h.Typed.Count);
        Assert.Contains("one", h.Typed[0].Line);
        Assert.Contains("two", h.Typed[1].Line);
        TeamMarkers.Clear(sess.Root.Id);
    }

    /// The lead handing work to a bot that isn't running starts it; a message
    /// from an ordinary bot does not (the owner decides who comes up).
    [Fact]
    public async Task TheLeadsHandoffStartsTheReceiver_AnotherBotsDoesNot()
    {
        var h = new Harness();
        var ada = await h.CreateBot("Ada");
        var bo = await h.CreateBot("Bo", slug: ada.PositionSlug);
        var adaSess = h.Sessions.First(s => s.Id == ada.SessionId);
        var boSess = h.Sessions.First(s => s.Id == bo.SessionId);
        TeamMarkers.Clear(boSess.Root.Id);
        h.Sessions.Remove(boSess);
        bo.SessionId = null;

        // Ada is not the lead: her failed send is reported, Bo stays down.
        h.Store.Doc.LeadSlug = null;
        h.Ctrl.OnPeerMsg(adaSess, adaSess.Root.Id, new PeerMsgMessage("sent", "bo", "…", false,
            "HANDOFF: take the schema", null, "no session named bo"));
        Assert.Single(h.Sessions);
        Assert.Contains(h.Ledger, e => e.Event == "undelivered");

        // Ada leads: the same hand-off brings Bo up and waits for its Claude.
        h.Store.Doc.LeadSlug = ada.Slug;
        h.Ctrl.OnPeerMsg(adaSess, adaSess.Root.Id, new PeerMsgMessage("sent", "bo", "…", false,
            "HANDOFF: take the schema", null, "no session named bo"));
        Assert.Equal(2, h.Sessions.Count);
        var fresh = h.Sessions.First(s => s.Id != adaSess.Id);
        Assert.Equal(fresh.Id, bo.SessionId);
        h.Ctrl.OnAgentUp(fresh);
        h.RunLastDelayed();
        var typed = Assert.Single(h.Typed, t => t.Session == fresh.Id);
        Assert.Contains("take the schema", typed.Line);
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
    }

    [Fact]
    public async Task SendAgainOnABotWithNoTabStartsIt()
    {
        var h = new Harness();
        var ada = await h.CreateBot("Ada");
        TeamMarkers.Clear(h.Sessions.Single().Root.Id);
        h.Sessions.Clear();
        ada.SessionId = null;
        // The post starts a tab for Ada and waits for it — and then that tab
        // is gone (closed, or this is another machine) before her Claude ever
        // reported, so the post is still undelivered and she has no tab.
        h.Post("please", null);
        var post = h.Ledger.Single(e => e.Kind == "user");
        foreach (var s in h.Sessions) TeamMarkers.Clear(s.Root.Id);
        h.Sessions.Clear();
        ada.SessionId = null;
        Assert.False(post.Delivered);

        h.Ctrl.OnDeliverRetry(new TeamDeliverRetryMsg { ProjectId = h.Project.Id, Seq = post.Seq, BotId = ada.Slug });

        var sess = Assert.Single(h.Sessions);
        h.Ctrl.OnAgentUp(sess);
        h.RunLastDelayed();
        Assert.Contains(h.Typed, t => t.Session == sess.Id && t.Line.Contains("please"));
        TeamMarkers.Clear(sess.Root.Id);
    }
}
