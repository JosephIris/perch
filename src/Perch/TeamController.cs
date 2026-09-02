using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Perch;

/// What TeamController needs from the window, as delegates. The controller is
/// the BoardController shape — UI-thread state, no window reference — so the
/// tests can drive it with plain lambdas and no WPF.
internal sealed class TeamHost
{
    public required Func<Guid, Project?> ProjectById { get; init; }
    public required Func<IEnumerable<Project>> Projects { get; init; }
    public required Func<Guid, Session?> SessionById { get; init; }
    public required Func<IEnumerable<Session>> Sessions { get; init; }
    public required Func<Session, PaneNode, string> ResolveCwd { get; init; }
    /// TranscriptReader.Read — incremental per pane, so calling it on every
    /// room request costs only the rows appended since the last one.
    public required Func<Guid, string?, string?, InspectorData?> ReadTranscript { get; init; }
    /// Type one line into a session's running Claude pane (MainWindow.TypeToClaude).
    public required Func<Session, string, bool> TypeToClaude { get; init; }
    public required Action<Session> Wake { get; init; }
    /// Open a project tab for a bot: (project, name, worktree, model, ccName) → session.
    public required Func<Project, string, bool, string?, string, Task<Session?>> CreateTab { get; init; }
    /// Close a session (sessionId, removeWorktree) the way the sidebar does.
    public required Action<Guid, bool> CloseSession { get; init; }
    /// Serialize and post a message to the page.
    public required Action<object> Post { get; init; }
    public required Action PushState { get; init; }
    /// Run `action` on the UI thread after `delay` (a DispatcherTimer).
    public required Action<Action, TimeSpan> Delay { get; init; }
}

/// The team feature's host-side brain: per-project team stores, the marker
/// files that give a bot its brief and roster, the room ledger, delivery of
/// the owner's posts into bot terminals, and the headless jobs (brief
/// generation, routing).
///
/// ## Delivery
///
/// The owner's post is TYPED into the bot's terminal as one line prefixed
/// `[Perch team]` — the documented way to talk to a session, read at the
/// bot's next step even mid-turn, and carrying the owner's authority (unlike
/// a peer message, which Claude Code tells the receiver came from another
/// session). When no Claude is up in that tab — asleep, still booting, or
/// exited — the line is parked and flushed a few seconds after the
/// session-start hook says the agent is listening.
///
/// ## The room is one stream
///
/// Everything the room shows lives in the ledger with one sequence number:
/// the owner's posts, bot-to-bot messages observed by the hook, bots' notes,
/// lifecycle events, AND what each bot said and did — copied in from its
/// transcript as the page asks for the room. One ordered stream makes
/// incremental fetch and unread counts trivial; the cost is a ledger that
/// grows with the bots' work, which rotation bounds.
internal sealed class TeamController
{
    private readonly TeamHost _h;

    private readonly Dictionary<Guid, TeamStore> _stores = new();
    /// Posts waiting for a Claude to come up in that session.
    private readonly Dictionary<Guid, List<(long Seq, string Line, string Nick)>> _parked = new();
    private readonly Dictionary<string, CancellationTokenSource> _briefJobs = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _openRooms = new();
    /// Per pane: how many collapsed transcript events are already in the
    /// ledger, and for which Claude session.
    private readonly Dictionary<Guid, (string Session, int Count)> _ingested = new();

    /// Multi-line posts: bracketed paste keeps the newlines but depends on the
    /// TUI honouring the sequence; off until the live check proves it, in
    /// which case posts are flattened to one line instead.
    internal static bool UseBracketedPaste = false;

    /// Minimum router confidence to deliver without asking.
    internal const double RouteThreshold = 0.6;

    public TeamController(TeamHost host) { _h = host; }

    // ---- stores -----------------------------------------------------------

    /// The project's team, or null when it has none. Re-opened when the
    /// folder vanished or team.json changed on disk (a hand edit, a sync).
    public TeamStore? StoreFor(Guid projectId)
    {
        var proj = _h.ProjectById(projectId);
        if (proj == null) return null;
        if (_stores.TryGetValue(projectId, out var cached))
        {
            if (Directory.Exists(cached.Dir) && cached.RepoRoot == proj.Path) return cached;
            _stores.Remove(projectId);
        }
        var store = TeamStore.Open(proj.Path);
        if (store != null) _stores[projectId] = store;
        return store;
    }

    private TeamStore StoreOrCreate(Project proj)
    {
        var store = StoreFor(proj.Id);
        if (store != null) return store;
        store = TeamStore.Create(proj.Path);
        _stores[proj.Id] = store;
        return store;
    }

    /// Which team (and which bot in it) a session belongs to, if any.
    public (Project Project, TeamStore Store, TeamBot Bot)? BotOfSession(Guid sessionId)
    {
        foreach (var proj in _h.Projects())
        {
            var store = StoreFor(proj.Id);
            var bot = store?.Doc.BotBySession(sessionId);
            if (store != null && bot != null) return (proj, store, bot);
        }
        return null;
    }

    /// The `team` block for a project row in the state snapshot; null when the
    /// project has no team at all (the sidebar then shows no team row).
    public object? ProjectTeamView(Guid projectId)
    {
        var store = StoreFor(projectId);
        if (store == null || (store.Doc.Bots.Count == 0 && store.Doc.Positions.Count == 0)) return null;
        return new
        {
            bots = store.Doc.Bots.Select(b => new
            {
                botId = b.Slug,
                nickname = b.Nickname,
                positionSlug = b.PositionSlug,
                positionName = store.Doc.Position(b.PositionSlug)?.Name ?? b.PositionSlug,
                sessionId = b.SessionId?.ToString("D") ?? "",
                peerName = b.CcName,
            }).ToArray(),
            positions = store.Doc.Positions.Select(p =>
            {
                // The brief rides along (capped) so "Edit brief…" opens on the
                // text without a round trip. Positions are few and the state
                // push is already the hot path, hence the cap.
                var brief = store.ReadBrief(p.Slug);
                if (brief.Length > 16 * 1024) brief = brief[..(16 * 1024)];
                return new
                {
                    slug = p.Slug,
                    name = p.Name,
                    purpose = p.Purpose,
                    model = p.Model,
                    hasBrief = brief.Trim().Length > 0,
                    brief,
                };
            }).ToArray(),
        };
    }

    // ---- markers ----------------------------------------------------------

    /// Point every terminal pane of a bot's session at its brief and the
    /// team roster; clear the markers for any other session, so a recycled
    /// pane id can never inherit a brief. Called before every spawn.
    public void PublishMarkers(Session sess)
    {
        var hit = BotOfSession(sess.Id);
        foreach (var pane in PaneTree.AllLeaves(sess.Root))
        {
            if (!pane.IsTerminal) continue;
            if (hit is { } h)
                TeamMarkers.Publish(pane.Id, h.Store.SystemPathFor(h.Bot.Slug), h.Store.RosterPath);
            else
                TeamMarkers.Clear(pane.Id);
        }
    }

    // ---- observed traffic -------------------------------------------------

    /// A bot's SendMessage, as the hook saw it. Only the "sent" phase carries
    /// the verdict; the ledger records the full body when the hook had it.
    public void OnPeerMsg(Session sess, Guid paneId, PeerMsgMessage msg)
    {
        if ((msg.Phase ?? "") != "sent") return;
        if (BotOfSession(sess.Id) is not { } h) return;
        var target = (msg.Target ?? "").Trim();
        var tbot = h.Store.Doc.Bots.FirstOrDefault(b => ClaudePeerNames.Matches(b.CcName, target));
        var entry = h.Store.Ledger.Append(new RoomEntry
        {
            Kind = "peer",
            From = h.Bot.Slug,
            To = new List<string> { tbot?.Slug ?? target },
            Text = msg.Message ?? msg.Text ?? "",
            Summary = msg.Summary,
            Ok = msg.Ok,
        });
        PostEntries(h.Project.Id, h.Store, new[] { entry });
    }

    /// `perch team post` from a bot: a note for the owner, pinging nobody.
    public void OnTeamPost(Session sess, Guid paneId, TeamPostMessage msg)
    {
        if (BotOfSession(sess.Id) is not { } h) return;
        var text = (msg.Text ?? "").Trim();
        if (text.Length == 0) return;
        var entry = h.Store.Ledger.Append(new RoomEntry
        {
            Kind = "note", From = h.Bot.Slug, To = new List<string> { TeamRender.Everyone }, Text = text,
        });
        PostEntries(h.Project.Id, h.Store, new[] { entry });
    }

    // ---- lifecycle --------------------------------------------------------

    /// The session-start hook fired in `sess`: a Claude is listening. Flush
    /// anything parked for it, after the same settle delay the pairing intro
    /// uses so the line lands in a painted input box.
    public void OnAgentUp(Session sess)
    {
        if (!_parked.ContainsKey(sess.Id)) return;
        var sid = sess.Id;
        _h.Delay(() =>
        {
            var s = _h.SessionById(sid);
            if (s == null || !_parked.TryGetValue(sid, out var lines)) return;
            if (BotOfSession(sid) is not { } h) { _parked.Remove(sid); return; }
            var delivered = new List<RoomEntry>();
            foreach (var (seq, line, nick) in lines.ToList())
            {
                if (!_h.TypeToClaude(s, line)) break;
                lines.Remove((seq, line, nick));
                delivered.Add(h.Store.Ledger.Append(new RoomEntry
                {
                    Kind = "system", From = "perch", Event = "delivered",
                    Text = $"Delivered to {nick}", Note = seq.ToString(),
                }));
                Log.Info("Team.deliver", $"session={sid:N} seq={seq} (parked)");
            }
            if (lines.Count == 0) _parked.Remove(sid);
            if (delivered.Count > 0) PostEntries(h.Project.Id, h.Store, delivered);
        }, TimeSpan.FromSeconds(4));
    }

    public void OnSessionSlept(Session sess) => Lifecycle(sess, "asleep", b => $"{b.Nickname} is asleep");
    public void OnSessionWoke(Session sess) => Lifecycle(sess, "woke", b => $"{b.Nickname} woke up");

    /// A bot's tab closed from the sidebar (not via team.bot.remove): the bot
    /// stays on the roster as "not running" and can be relaunched.
    public void OnSessionClosed(Session sess)
    {
        if (BotOfSession(sess.Id) is not { } h) return;
        h.Bot.SessionId = null;
        h.Store.Save();
        var e = h.Store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "left", Text = $"{h.Bot.Nickname}'s tab was closed",
        });
        _parked.Remove(sess.Id);
        RefreshRoster(h.Project, h.Store);
        PostEntries(h.Project.Id, h.Store, new[] { e });
    }

    private void Lifecycle(Session sess, string ev, Func<TeamBot, string> text)
    {
        if (BotOfSession(sess.Id) is not { } h) return;
        var e = h.Store.Ledger.Append(new RoomEntry { Kind = "system", From = "perch", Event = ev, Text = text(h.Bot) });
        RefreshRoster(h.Project, h.Store);
        PostEntries(h.Project.Id, h.Store, new[] { e });
    }

    // ---- page verbs -------------------------------------------------------

    public void OnRoom(TeamRoomMsg msg)
    {
        if (msg.Open) _openRooms.Add(msg.ProjectId); else _openRooms.Remove(msg.ProjectId);
    }

    /// The room asking for entries. Copies the bots' newest transcript rows
    /// into the ledger first, so "what Ada said" is as fresh as the poll.
    public void OnRequest(TeamRequestMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        if (proj == null || store == null)
        {
            _h.Post(new { type = "team.data", projectId = msg.ProjectId.ToString("D"), entries = Array.Empty<object>(), lastSeq = 0L });
            return;
        }
        IngestTranscripts(store);
        RefreshRoster(proj, store);
        var (entries, truncated) = store.Ledger.ReadSince(msg.SinceSeq ?? 0);
        _h.Post(new
        {
            type = "team.data",
            projectId = msg.ProjectId.ToString("D"),
            entries = entries.Select(e => EntryView(store, e)).ToArray(),
            lastSeq = store.Ledger.LastSeq,
            truncated,
        });
    }

    /// The owner posted. Resolve the recipients the page named, or route an
    /// unaddressed post; record it; deliver it.
    public void OnPost(TeamPostMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        var text = (msg.Text ?? "").Trim();
        if (proj == null || store == null || text.Length == 0) return;

        var (targets, everyone, unaddressed) = ResolveRecipients(store, msg.To);
        if (everyone) targets = store.Doc.Bots.ToList();

        var entry = new RoomEntry
        {
            Kind = "user", From = "you", Text = text, ClientId = msg.ClientId,
            To = everyone ? new List<string> { TeamRender.Everyone }
               : unaddressed ? null
               : targets.Select(b => b.Slug).ToList(),
        };
        if (unaddressed)
        {
            // The router is async; the page needs the echo now.
            store.Ledger.Append(entry);
            PostEntries(proj.Id, store, new[] { entry });
            _ = RouteAsync(proj, store, entry, text);
            return;
        }
        // Deliver FIRST, so the row lands with its verdict: "delivered" or
        // "waiting for the bot to wake" is a fact about this post, not a later
        // event the page has to reconcile.
        var attempts = Attempt(targets, text, everyone);
        entry.Delivered = attempts.All(a => a.Ok);
        store.Ledger.Append(entry);
        var events = Record(store, entry, attempts, routed: null);
        PostEntries(proj.Id, store, new[] { entry }.Concat(events));
    }

    public async Task OnBotCreateAsync(TeamBotCreateMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        if (proj == null) return;
        var nickname = (msg.Nickname ?? "").Trim();
        if (!ValidNickname(nickname)) { Toast("That nickname won't work as an address. Letters, digits, - and _ only."); return; }
        var store = StoreOrCreate(proj);
        if (!store.Readable) { Toast(store.Problem); return; }
        if (store.Doc.Bots.Any(b => string.Equals(b.Nickname, nickname, StringComparison.OrdinalIgnoreCase)))
        { Toast($"{nickname} is already on the team."); return; }

        TeamPosition? pos = null;
        if (!string.IsNullOrWhiteSpace(msg.PositionSlug)) pos = store.Doc.Position(msg.PositionSlug!);
        else if (msg.Position is { } spec)
        {
            pos = store.AddPosition(spec.Name, spec.Purpose, spec.ReferencePath ?? "", spec.Model ?? "");
            if (!string.IsNullOrWhiteSpace(spec.Brief)) store.WriteBrief(pos.Slug, spec.Brief!);
        }
        if (pos == null) { Toast("Pick a position for the bot first."); return; }

        var ccName = MintCcName(nickname);
        var model = string.IsNullOrWhiteSpace(msg.Position?.Model) ? pos.Model : msg.Position!.Model!;
        var bot = store.AddBot(nickname, pos.Slug, ccName, msg.Worktree ?? true, "");
        store.Save();
        store.RenderSystemFiles(proj.Name);
        RefreshRoster(proj, store);

        var sess = await _h.CreateTab(proj, nickname, msg.Worktree ?? true, model, ccName);
        if (sess == null)
        {
            store.RemoveBot(bot.Slug);
            store.Save();
            RefreshRoster(proj, store);
            return;   // CreateTab already toasted why (a worktree that couldn't be cut)
        }
        bot.SessionId = sess.Id;
        store.Save();
        PublishMarkers(sess);   // before the page's first pane.resize spawns the shell
        var e = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "joined", Text = $"{nickname} joined as {pos.Name}",
        });
        Log.Info("Team.create", $"project={proj.Id:N} bot={bot.Slug} cc={ccName} session={sess.Id:N}");
        _h.PushState();
        PostEntries(proj.Id, store, new[] { e });
    }

    public void OnBotRemove(TeamBotRemoveMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        var bot = store?.Doc.Bot(msg.BotId);
        if (proj == null || store == null || bot == null) return;
        var sid = bot.SessionId;
        store.RemoveBot(bot.Slug);
        store.Save();
        var e = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "left", Text = $"{bot.Nickname} left the team",
        });
        RefreshRoster(proj, store);
        if (sid is Guid id)
        {
            _parked.Remove(id);
            var sess = _h.SessionById(id);
            if (sess != null)
            {
                foreach (var p in PaneTree.AllLeaves(sess.Root)) { p.PinnedPeerName = null; TeamMarkers.Clear(p.Id); }
                if (msg.CloseTab == true) _h.CloseSession(id, msg.RemoveWorktree == true);
            }
        }
        _h.PushState();
        PostEntries(proj.Id, store, new[] { e });
    }

    public void OnPositionUpdate(TeamPositionUpdateMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        var pos = store?.Doc.Position(msg.Slug);
        if (proj == null || store == null || pos == null) return;
        if (msg.Name is { } n && n.Trim().Length > 0) pos.Name = n.Trim();
        if (msg.Purpose is { } p) pos.Purpose = p.Trim();
        if (msg.Brief is { } b)
        {
            store.WriteBrief(pos.Slug, b);
            pos.BriefGeneratedAtMs = 0;   // edited by hand from here on
        }
        store.Save();
        store.RenderSystemFiles(proj.Name);
        RefreshRoster(proj, store);
        _h.PushState();
    }

    // ---- brief generation -------------------------------------------------

    public void OnBriefGenerate(TeamBriefGenerateMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        if (proj == null) return;
        if (_briefJobs.ContainsKey(msg.JobId)) return;
        var reference = string.IsNullOrWhiteSpace(msg.ReferencePath) ? proj.Path : msg.ReferencePath!.Trim();
        if (!Directory.Exists(reference))
        {
            _h.Post(new { type = "team.brief.result", jobId = msg.JobId, error = "The reference folder doesn't exist." });
            return;
        }
        var cts = new CancellationTokenSource();
        _briefJobs[msg.JobId] = cts;
        _ = GenerateBriefAsync(msg, proj, reference, cts);
    }

    public void OnBriefCancel(TeamBriefCancelMsg msg)
    {
        if (_briefJobs.Remove(msg.JobId, out var cts)) cts.Cancel();
    }

    private async Task GenerateBriefAsync(TeamBriefGenerateMsg msg, Project proj, string reference, CancellationTokenSource cts)
    {
        var started = DateTimeOffset.UtcNow;
        var pos = new TeamPosition { Name = msg.PositionName, Purpose = msg.Purpose, ReferenceRepo = reference };
        var prompt = TeamRender.BriefPrompt(pos, proj.Name);
        Log.Info("Team.brief.start", $"job={msg.JobId} position={msg.PositionName} ref={reference}");
        Progress(msg.JobId, "Reading the repository…", 0);

        // Progress ticks while the run is in flight — the page shows elapsed
        // time and a phase word so a three-minute read doesn't look hung.
        var ticking = true;
        void Tick()
        {
            if (!ticking || cts.IsCancellationRequested) return;
            var ms = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
            Progress(msg.JobId, ms < 45_000 ? "Reading the repository…" : ms < 150_000 ? "Writing the brief…" : "Still writing… this can take a few minutes", ms);
            _h.Delay(Tick, TimeSpan.FromSeconds(5));
        }
        _h.Delay(Tick, TimeSpan.FromSeconds(5));

        HeadlessResult r;
        try
        {
            r = await ClaudeHeadless.RunAsync(prompt, reference, msg.Model ?? "", "claude.headless.brief",
                new[] { "--restricted", "--tools", "Read,Glob,Grep", "--max-budget-usd", "2" },
                timeoutMs: 300_000, ct: cts.Token);
        }
        catch (OperationCanceledException) { r = new HeadlessResult(false, "", "canceled", 0, 0, ""); }
        ticking = false;
        _briefJobs.Remove(msg.JobId);
        if (cts.IsCancellationRequested)
        {
            Log.Info("Team.brief.canceled", $"job={msg.JobId}");
            return;   // the page dismissed it; nothing to show
        }
        if (r.Ok && r.Text.Trim().Length > 0)
        {
            Log.Info("Team.brief.done", $"job={msg.JobId} cost={r.CostUsd:F3} ms={r.DurationMs}");
            _h.Post(new { type = "team.brief.result", jobId = msg.JobId, brief = r.Text.Trim(), costUsd = r.CostUsd });
        }
        else
        {
            Log.Info("Team.brief.fail", $"job={msg.JobId} error={r.Error} raw={TeamRender.OneLine(r.RawJson, 300)}");
            _h.Post(new { type = "team.brief.result", jobId = msg.JobId, error = r.Error ?? "Claude didn't return a brief." });
        }
    }

    private void Progress(string jobId, string phase, long elapsedMs)
        => _h.Post(new { type = "team.brief.progress", jobId, phase, elapsedMs });

    // ---- routing ----------------------------------------------------------

    /// Decide who an unaddressed post is for. One bot: obviously them. Several:
    /// a small, tool-less, budget-capped model call over the positions' purposes;
    /// below the confidence bar the room says so and delivers nothing, because
    /// a guess that lands on the wrong bot costs a whole turn.
    private async Task RouteAsync(Project proj, TeamStore store, RoomEntry post, string text)
    {
        var bots = store.Doc.Bots;
        if (bots.Count == 0)
        {
            Fallback(proj, store, "No bots on the team yet — add one first.");
            return;
        }
        if (bots.Count == 1)
        {
            Log.Info("Team.route", $"seq={post.Seq} ok=true to={bots[0].Slug} conf=1.00 reason=the only bot on the team");
            Routed(proj, store, post, bots.ToList(), text, "the only bot on the team");
            return;
        }
        var prompt = TeamRender.RouterPrompt(store.Doc, text);
        var r = await ClaudeHeadless.RunAsync(prompt, proj.Path, "haiku", "claude.headless.route",
            new[] { "--tools", "", "--json-schema", TeamRender.RouterSchema, "--max-budget-usd", "0.05" },
            timeoutMs: 20_000);
        var verdict = ParseRoute(r, store.Doc);
        Log.Info("Team.route", $"seq={post.Seq} ok={r.Ok} to={string.Join(",", verdict.To.Select(b => b.Slug))} conf={verdict.Confidence:F2} reason={verdict.Reason}");
        if (!r.Ok || verdict.To.Count == 0 || verdict.Confidence < RouteThreshold)
        {
            var names = string.Join(", ", bots.Take(3).Select(b => "@" + b.Nickname));
            Fallback(proj, store, $"Not sure who that's for — say {names} or @everyone.");
            return;
        }
        Routed(proj, store, post, verdict.To, text, verdict.Reason);
    }

    /// Deliver a post the router (or the single-bot shortcut) addressed. The
    /// post is already in the ledger; its Delivered flag is set in memory and
    /// re-sent so the page's row flips from "unaddressed" to its verdict.
    private void Routed(Project proj, TeamStore store, RoomEntry post, List<TeamBot> targets, string text, string reason)
    {
        var attempts = Attempt(targets, text, everyone: false);
        post.Delivered = attempts.Count > 0 && attempts.All(a => a.Ok);
        post.To = targets.Select(b => b.Slug).ToList();
        var events = Record(store, post, attempts, routed: reason);
        PostEntries(proj.Id, store, new[] { post }.Concat(events));
    }

    internal static (List<TeamBot> To, double Confidence, string Reason) ParseRoute(HeadlessResult r, TeamDoc doc)
    {
        var to = new List<TeamBot>();
        if (!r.Ok) return (to, 0, r.Error ?? "");
        var json = r.Structured ?? r.Text;
        try
        {
            using var d = JsonDocument.Parse(json);
            var root = d.RootElement;
            var conf = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
            var reason = root.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString() ?? "" : "";
            if (root.TryGetProperty("to", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                {
                    var slug = el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
                    var bot = doc.Bot(slug) ?? doc.Bots.FirstOrDefault(b =>
                        string.Equals(b.Nickname, slug, StringComparison.OrdinalIgnoreCase) || ClaudePeerNames.Matches(b.CcName, slug));
                    if (bot != null && !to.Contains(bot)) to.Add(bot);
                }
            return (to, conf, reason);
        }
        catch (JsonException) { return (to, 0, "unreadable answer"); }
    }

    private void Fallback(Project proj, TeamStore store, string text)
    {
        var e = store.Ledger.Append(new RoomEntry { Kind = "system", From = "perch", Event = "error", Text = text });
        PostEntries(proj.Id, store, new[] { e });
    }

    // ---- delivery ---------------------------------------------------------

    private sealed record Attempted(TeamBot Bot, bool Ok, bool Missing, string Line, Guid? SessionId);

    /// Try to type the post into each target's terminal now. Ok = it landed;
    /// Missing = the bot has no tab at all; neither = parked (asleep or
    /// booting), to be flushed by OnAgentUp.
    private List<Attempted> Attempt(List<TeamBot> targets, string text, bool everyone)
    {
        var results = new List<Attempted>();
        foreach (var bot in targets)
        {
            var line = DeliveryLine(text, everyone ? null : bot.Nickname);
            var sess = bot.SessionId is Guid id ? _h.SessionById(id) : null;
            if (sess == null) { results.Add(new Attempted(bot, false, true, line, null)); continue; }
            if (sess.Dormant) _h.Wake(sess);
            var ok = !sess.Dormant && _h.TypeToClaude(sess, line);
            results.Add(new Attempted(bot, ok, false, line, sess.Id));
        }
        return results;
    }

    /// Write the attempts into the ledger against the post's seq: a "routed"
    /// line when a model chose the recipients, "undelivered" for a bot with no
    /// tab, and parking (with a log line) for the rest. Successful deliveries
    /// are recorded on the post itself (Delivered) rather than as rows — the
    /// page shows them as the post's own status line, not as chatter.
    private List<RoomEntry> Record(TeamStore store, RoomEntry post, List<Attempted> attempts, string? routed)
    {
        var events = new List<RoomEntry>();
        if (routed != null)
        {
            events.Add(store.Ledger.Append(new RoomEntry
            {
                Kind = "system", From = "perch", Event = "routed", Note = post.Seq.ToString(),
                To = attempts.Select(a => a.Bot.Slug).ToList(),
                Text = $"Sent to {string.Join(", ", attempts.Select(a => a.Bot.Nickname))} — {routed}",
            }));
        }
        foreach (var a in attempts)
        {
            if (a.Missing)
            {
                events.Add(store.Ledger.Append(new RoomEntry
                {
                    Kind = "system", From = "perch", Event = "undelivered", Note = post.Seq.ToString(),
                    Text = $"{a.Bot.Nickname} isn't running, so this didn't reach them",
                }));
            }
            else if (a.Ok)
            {
                Log.Info("Team.deliver", $"session={a.SessionId:N} seq={post.Seq} bot={a.Bot.Slug}");
            }
            else if (a.SessionId is Guid sid)
            {
                Log.Info("Team.parked", $"session={sid:N} seq={post.Seq} bot={a.Bot.Slug}");
                if (!_parked.TryGetValue(sid, out var list)) _parked[sid] = list = new();
                list.Add((post.Seq, a.Line, a.Bot.Nickname));
            }
        }
        return events;
    }

    /// The line typed into a bot's terminal for one of the owner's posts.
    internal static string DeliveryLine(string text, string? nickname)
    {
        var who = nickname == null ? "@everyone" : "@" + nickname;
        var body = text.Replace("\r\n", "\n").Trim();
        if (body.Contains('\n'))
        {
            body = UseBracketedPaste
                ? "\x1b[200~" + body + "\x1b[201~"
                : body.Replace("\n", " ⏎ ");
        }
        return $"{TeamRender.PostPrefix} Joseph → {who}: {body}";
    }

    // ---- helpers ----------------------------------------------------------

    private (List<TeamBot> Targets, bool Everyone, bool Unaddressed) ResolveRecipients(TeamStore store, JsonElement? to)
    {
        var targets = new List<TeamBot>();
        if (to is not { } el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return (targets, false, true);
        if (el.ValueKind == JsonValueKind.String)
        {
            // "everyone", or a single nickname (what `perch test team.post
            // --to Ada` can express; the page always sends an array).
            var s = (el.GetString() ?? "").Trim();
            if (string.Equals(s, "everyone", StringComparison.OrdinalIgnoreCase)) return (targets, true, false);
            var one = store.Doc.Bots.FirstOrDefault(b =>
                string.Equals(b.Nickname, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(b.Slug, s, StringComparison.OrdinalIgnoreCase)
                || ClaudePeerNames.Matches(b.CcName, s));
            if (one != null) targets.Add(one);
            return targets.Count == 0 ? (targets, false, true) : (targets, false, false);
        }
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var name = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
                if (string.Equals(name, "everyone", StringComparison.OrdinalIgnoreCase)) return (targets, true, false);
                var bot = store.Doc.Bots.FirstOrDefault(b =>
                    string.Equals(b.Nickname, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(b.Slug, name, StringComparison.OrdinalIgnoreCase)
                    || ClaudePeerNames.Matches(b.CcName, name));
                if (bot != null && !targets.Contains(bot)) targets.Add(bot);
            }
        }
        return targets.Count == 0 ? (targets, false, true) : (targets, false, false);
    }

    /// A session name for a new bot: the nickname's slug, made unique against
    /// every address any live pane answers to and every bot in every team, so
    /// the roster's `ada` is never a different session's `ada`.
    private string MintCcName(string nickname)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _h.Sessions())
            foreach (var p in PaneTree.AllLeaves(s.Root))
            {
                if (!string.IsNullOrEmpty(p.PeerName)) taken.Add(p.PeerName!);
                if (!string.IsNullOrEmpty(p.PinnedPeerName)) taken.Add(p.PinnedPeerName!);
                if (!string.IsNullOrEmpty(s.Title)) taken.Add(ClaudePeerNames.ForTitle(s.Title));
            }
        foreach (var proj in _h.Projects())
            foreach (var b in StoreFor(proj.Id)?.Doc.Bots ?? new List<TeamBot>())
                taken.Add(b.CcName);
        return TeamStore.UniqueSlug(nickname, "bot", taken.Contains);
    }

    internal static bool ValidNickname(string nick)
        => System.Text.RegularExpressions.Regex.IsMatch(nick, "^[A-Za-z0-9][A-Za-z0-9_-]{0,23}$")
           && !new[] { "everyone", "all", "you", "perch", "me" }.Contains(nick.ToLowerInvariant());

    private void RefreshRoster(Project proj, TeamStore store)
        => store.RenderRoster(proj.Name, Presence(store));

    private Dictionary<string, string> Presence(TeamStore store)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bot in store.Doc.Bots)
        {
            var sess = bot.SessionId is Guid id ? _h.SessionById(id) : null;
            if (sess == null) { map[bot.Slug] = "not running"; continue; }
            if (sess.Dormant) { map[bot.Slug] = "asleep"; continue; }
            var leaves = PaneTree.AllLeaves(sess.Root).Where(p => p.IsTerminal).ToList();
            map[bot.Slug] = leaves.Any(p => p.AgentState is AgentState.Waiting or AgentState.Permission) ? "waiting for the owner"
                          : leaves.Any(p => p.AgentState == AgentState.Working) ? "working"
                          : "idle";
        }
        return map;
    }

    /// Copy each bot's newest transcript rows into the ledger. A pane seen for
    /// the first time in this process skips its history — those rows were
    /// ledgered by the run that watched them happen (or predate the team).
    private void IngestTranscripts(TeamStore store)
    {
        var added = new List<RoomEntry>();
        foreach (var bot in store.Doc.Bots)
        {
            var sess = bot.SessionId is Guid id ? _h.SessionById(id) : null;
            var pane = sess == null ? null : PaneTree.AllLeaves(sess.Root).FirstOrDefault(p => p.IsTerminal);
            if (sess == null || pane == null || string.IsNullOrEmpty(pane.ClaudeSessionId)) continue;
            var data = _h.ReadTranscript(pane.Id, pane.ClaudeSessionId, _h.ResolveCwd(sess, pane));
            if (data == null) continue;
            var events = data.Events;
            var key = pane.Id;
            if (!_ingested.TryGetValue(key, out var seen) || seen.Session != pane.ClaudeSessionId)
            {
                // First sight of this transcript in this process: don't replay
                // history — unless nothing is in the ledger yet (a brand-new
                // bot whose first rows we simply haven't copied).
                var start = seen.Session == null && store.Ledger.LastSeq > 0 ? events.Count : 0;
                _ingested[key] = (pane.ClaudeSessionId!, start);
                seen = _ingested[key];
            }
            var from = Math.Min(seen.Count, events.Count);
            for (var i = from; i < events.Count; i++)
            {
                var e = events[i];
                RoomEntry? entry = e.Kind switch
                {
                    "beat" when e.Text.Trim().Length > 0 => new RoomEntry
                    {
                        Kind = "beat", From = bot.Slug, Text = e.Text.Trim(), PaneId = pane.Id.ToString("D"),
                        TsMs = ParseTs(e.Ts),
                    },
                    "work" or "skill" => new RoomEntry
                    {
                        Kind = "work", From = bot.Slug, Text = "", Verb = e.Verb, Target = e.Target,
                        Note = string.IsNullOrEmpty(e.Note) ? null : e.Note, Repeat = e.Repeat,
                        PaneId = pane.Id.ToString("D"), TsMs = ParseTs(e.Ts),
                    },
                    _ => null,
                };
                if (entry != null) added.Add(store.Ledger.Append(entry));
            }
            _ingested[key] = (pane.ClaudeSessionId!, events.Count);
        }
        // Not posted separately: the caller is about to reply with everything
        // since the page's watermark, which includes these.
    }

    private static long ParseTs(string iso)
        => DateTimeOffset.TryParse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUnixTimeMilliseconds() : 0;

    private object EntryView(TeamStore store, RoomEntry e)
    {
        string Nick(string slug) => slug switch
        {
            "you" or "perch" => slug,
            _ => store.Doc.Bot(slug)?.Nickname ?? slug,
        };
        object? to = e.To == null ? null
            : e.To.Contains(TeamRender.Everyone) ? "everyone"
            : e.To.Select(Nick).ToArray();
        // A dictionary, not an anonymous object: absent fields must be ABSENT on
        // the wire (the page's types say `to?: …`, and a null where it expects
        // undefined threw inside the feed render and blanked the room).
        var view = new Dictionary<string, object?>
        {
            ["seq"] = e.Seq,
            ["ts"] = DateTimeOffset.FromUnixTimeMilliseconds(e.TsMs).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["kind"] = e.Kind,
            ["from"] = Nick(e.From),
            ["text"] = e.Text,
        };
        void Put(string key, object? value) { if (value != null) view[key] = value; }
        Put("to", to);
        Put("botId", e.From is "you" or "perch" ? null : e.From);
        Put("paneId", e.PaneId);
        Put("verb", e.Verb);
        Put("target", e.Target);
        Put("note", e.Note);
        Put("repeat", e.Repeat);
        Put("clientId", e.ClientId);
        Put("event", e.Event);
        Put("delivered", e.Delivered);
        Put("ok", e.Ok);
        Put("summary", e.Summary);
        return view;
    }

    /// Push freshly appended entries to the page. Always — the page merges by
    /// seq and ignores a project whose room it isn't showing.
    private void PostEntries(Guid projectId, TeamStore store, IEnumerable<RoomEntry> entries)
    {
        _h.Post(new
        {
            type = "team.data",
            projectId = projectId.ToString("D"),
            entries = entries.Select(e => EntryView(store, e)).ToArray(),
            lastSeq = store.Ledger.LastSeq,
        });
    }

    private void Toast(string text) => _h.Post(new { type = "toast", text, level = "warn" });

    /// For the control pipe: the team as JSON plus the ledger's tail.
    public string Dump(Guid projectId)
    {
        var store = StoreFor(projectId);
        if (store == null) return "{\"team\":null}";
        return JsonSerializer.Serialize(new
        {
            team = new
            {
                positions = store.Doc.Positions.Select(p => new { p.Slug, p.Name, p.Purpose, p.Model, brief = store.ReadBrief(p.Slug).Length }),
                bots = store.Doc.Bots.Select(b => new { b.Slug, b.Nickname, b.PositionSlug, b.CcName, sessionId = b.SessionId?.ToString("D"), b.Worktree }),
            },
            ledger = store.Ledger.Tail(50).Select(e => EntryView(store, e)),
            parked = _parked.Sum(kv => kv.Value.Count),
        });
    }
}
