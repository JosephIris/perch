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
    /// Press Enter in that pane — the retry when a typed line didn't submit.
    public required Func<Session, bool> PressEnter { get; init; }
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
/// session). A post that names nobody goes to everyone; the roster tells
/// each bot to judge from the text whether it is for them.
///
/// Typing is not delivery. The line is only IN once Claude Code submits it,
/// and the Enter that follows the text has been seen to vanish (a post sat
/// in a bot's input box for three minutes until the owner pressed Enter by
/// hand). So every typed line is held until the prompt-submit hook reports
/// a `[Perch team]` prompt in that pane; if it doesn't, Enter is pressed
/// again, twice, and then the room says the post is stuck.
///
/// A line is never typed into a pane that is showing a prompt of its own
/// (permission, a question): the keystrokes would answer it. Such posts are
/// parked, like posts for a Claude that isn't up yet, and flushed when the
/// pane's state moves on.
///
/// ## The room is one stream
///
/// Everything the room shows lives in the ledger with one sequence number:
/// the owner's posts, bot-to-bot messages observed by the hook, bots' notes,
/// lifecycle events, AND what each bot said and did — copied in from its
/// transcript as the page asks for the room. One ordered stream makes
/// incremental fetch and unread counts trivial; the cost is a ledger that
/// grows with the bots' work, which rotation bounds.
///
/// Not everything a bot says is for the owner. Its replies are copied in
/// only for turns a room post started; a turn a teammate's message started
/// shows the exchange itself (the sender's hook records it) and nothing
/// else, and a `(no reply)` answer is dropped.
internal sealed class TeamController
{
    private readonly TeamHost _h;

    private readonly Dictionary<Guid, TeamStore> _stores = new();
    /// Posts waiting for a Claude to come up (or to be free) in that session.
    private readonly Dictionary<Guid, List<(long Seq, string Line, string Nick)>> _parked = new();
    /// Sessions with a parked-flush already on the timer.
    private readonly HashSet<Guid> _flushPending = new();
    private readonly Dictionary<string, CancellationTokenSource> _briefJobs = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _openRooms = new();
    /// Per pane: how many collapsed transcript events are already in the
    /// ledger, for which Claude session, and whether the bot's current turn
    /// is answering a room post (its beats then belong in the room).
    private readonly Dictionary<Guid, (string Session, int Count, bool Answering)> _ingested = new();

    /// A typed line the prompt-submit hook hasn't reported yet, per session.
    private sealed class PendingSubmit
    {
        public required long Seq { get; init; }
        public required TeamBot Bot { get; init; }
        public int Tries;
    }
    private readonly Dictionary<Guid, PendingSubmit> _submits = new();

    /// Bots told to wrap up after the owner confirmed a task: session → the
    /// project, and whether the wrap-up post was seen submitted. A bot is
    /// reset (its context cleared) when its turn ends AFTER that post went
    /// in — a Done from the turn it was busy with doesn't count.
    private readonly Dictionary<Guid, (Guid Project, bool Confirmed)> _wrapping = new();

    /// After typing: how long to wait for the hook before pressing Enter
    /// again (first two), and before giving up (last).
    internal static readonly TimeSpan[] SubmitChecks =
    {
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5),
    };

    /// Multi-line posts: bracketed paste keeps the newlines but depends on the
    /// TUI honouring the sequence; off until the live check proves it, in
    /// which case posts are flattened to one line instead.
    internal static bool UseBracketedPaste = false;

    public TeamController(TeamHost host) { _h = host; }

    // ---- stores -----------------------------------------------------------

    /// The project's team, or null when it has none. Re-read when team.json
    /// changed on disk (a pull, a sync, a hand edit) — a bot created on
    /// another machine then appears here as "not running", ready to start —
    /// and re-opened when the folder vanished.
    public TeamStore? StoreFor(Guid projectId)
    {
        var proj = _h.ProjectById(projectId);
        if (proj == null) return null;
        if (_stores.TryGetValue(projectId, out var cached))
        {
            if (Directory.Exists(cached.Dir) && cached.RepoRoot == proj.Path)
            {
                if (cached.StaleOnDisk())
                {
                    Log.Info("Team.reload", $"project={projectId:N} team.json changed on disk");
                    cached.Reload();
                    RefreshRoster(proj, cached);
                    _h.PushState();
                }
                return cached;
            }
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
            lead = store.Doc.LeadSlug,
            task = TaskView(projectId, store),
            bots = store.Doc.Bots.Select(b =>
            {
                var pos = store.Doc.Position(b.PositionSlug);
                var look = TeamLooks.Normalize(b.Look);
                return new
                {
                    botId = b.Slug,
                    nickname = b.Nickname,
                    positionSlug = b.PositionSlug,
                    positionName = pos?.Name ?? b.PositionSlug,
                    sessionId = b.SessionId?.ToString("D") ?? "",
                    peerName = b.CcName,
                    // The face: hat from the position, the rest the bot's own.
                    look = new
                    {
                        hat = TeamLooks.NormalizeHat(pos?.Hat, pos?.Name ?? b.PositionSlug),
                        eyewear = look.Eyewear,
                        extra = look.Extra,
                        temper = look.Temper,
                    },
                };
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
                    hat = TeamLooks.NormalizeHat(p.Hat, p.Name),
                    hasBrief = brief.Trim().Length > 0,
                    brief,
                };
            }).ToArray(),
        };
    }

    /// The current task for the page: the board, every piece by nickname,
    /// and which bots are still wrapping up after a confirm.
    private object? TaskView(Guid projectId, TeamStore store)
    {
        var b = store.Tasks.Current;
        if (b == null) return null;
        string Nick(string slug) => slug == "you" ? "you" : store.Doc.Bot(slug)?.Nickname ?? slug;
        return new
        {
            id = b.Id,
            title = b.Title,
            status = b.Status,
            setBy = Nick(b.SetBy),
            reviewBy = b.ReviewBy == null ? null : Nick(b.ReviewBy),
            createdAtMs = b.CreatedAtMs,
            doneAtMs = b.DoneAtMs,
            items = b.Items.Select(i => new
            {
                botId = i.Bot, bot = Nick(i.Bot), title = i.Title, status = i.Status, note = i.Note, updatedAtMs = i.UpdatedAtMs,
            }).ToArray(),
            wrapping = _wrapping.Where(kv => kv.Value.Project == projectId)
                .Select(kv => store.Doc.BotBySession(kv.Key)?.Slug).Where(s => s != null).ToArray(),
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
                TeamMarkers.Publish(pane.Id, h.Store.SystemPathFor(h.Bot.Slug), h.Store.ContextPathFor(h.Bot.Slug));
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
    public void OnAgentUp(Session sess) => FlushParked(sess.Id, TimeSpan.FromSeconds(4));

    /// A hook status for one of `sess`'s panes, BEFORE the window applies it.
    /// Two things to learn from it: the prompt-submit hook echoing a
    /// `[Perch team]` prompt confirms the typed line went in; and a pane that
    /// is no longer showing its own prompt can take what was parked for it.
    public void OnAgentStatus(Session sess, StatusMessage msg)
    {
        var state = StateProjection.ParseAgentState(msg.State);
        var isPostEcho = state == AgentState.Working && (msg.Detail ?? "").StartsWith(TeamRender.PostPrefix, StringComparison.Ordinal);
        if (isPostEcho && _submits.Remove(sess.Id, out var p))
            Log.Info("Team.submit", $"session={sess.Id:N} seq={p.Seq} confirmed");
        if (state is not (AgentState.Permission or AgentState.Waiting) && _parked.ContainsKey(sess.Id))
            FlushParked(sess.Id, TimeSpan.FromSeconds(1));
        // Wrapping up: the wrap-up post going in arms the reset; the next
        // turn end fires it.
        if (_wrapping.TryGetValue(sess.Id, out var w))
        {
            if (isPostEcho && !w.Confirmed) _wrapping[sess.Id] = (w.Project, true);
            else if (w.Confirmed && state == AgentState.Done) ResetBot(sess, w.Project);
        }
    }

    /// Type the session's parked lines once it can take them. Re-checked at
    /// run time — the pane may have gone back to sleep or put up a prompt in
    /// the meantime — and coalesced, since a working pane reports status many
    /// times a second.
    private void FlushParked(Guid sid, TimeSpan delay)
    {
        if (!_parked.ContainsKey(sid) || !_flushPending.Add(sid)) return;
        _h.Delay(() =>
        {
            _flushPending.Remove(sid);
            var s = _h.SessionById(sid);
            if (s == null || !_parked.TryGetValue(sid, out var lines)) return;
            if (BotOfSession(sid) is not { } h) { _parked.Remove(sid); return; }
            if (s.Dormant || Blocked(s)) return;   // not yet; the next state change tries again
            var delivered = new List<RoomEntry>();
            foreach (var (seq, line, nick) in lines.ToList())
            {
                if (!_h.TypeToClaude(s, line)) break;
                lines.Remove((seq, line, nick));
                Expect(sid, h.Bot, seq);
                delivered.Add(h.Store.Ledger.Append(new RoomEntry
                {
                    Kind = "system", From = "perch", Event = "delivered",
                    Text = $"Delivered to {nick}", Note = seq.ToString(),
                }));
                Log.Info("Team.deliver", $"session={sid:N} seq={seq} (parked)");
            }
            if (lines.Count == 0) _parked.Remove(sid);
            if (delivered.Count > 0) PostEntries(h.Project.Id, h.Store, delivered);
        }, delay);
    }

    /// The pane is showing a prompt of its own — a permission dialog, a
    /// question — that keystrokes would answer. Nothing is typed into it.
    private static bool Blocked(Session sess)
        => PaneTree.AllLeaves(sess.Root).Any(p => p.IsTerminal && p.AgentState is AgentState.Permission or AgentState.Waiting);

    private static bool Working(Session sess)
        => PaneTree.AllLeaves(sess.Root).Any(p => p.IsTerminal && p.AgentState == AgentState.Working);

    // ---- submit confirmation ----------------------------------------------

    /// A line was just typed for post `seq`: wait for the prompt-submit hook,
    /// and press Enter again if it doesn't come. One pending line per
    /// session — a newer post supersedes the check for an older one.
    private void Expect(Guid sid, TeamBot bot, long seq)
    {
        _submits[sid] = new PendingSubmit { Seq = seq, Bot = bot };
        _h.Delay(() => CheckSubmitted(sid, seq), SubmitChecks[0]);
    }

    private void CheckSubmitted(Guid sid, long seq)
    {
        if (!_submits.TryGetValue(sid, out var p) || p.Seq != seq) return;   // confirmed, or superseded
        var s = _h.SessionById(sid);
        if (s == null || BotOfSession(sid) is not { } h) { _submits.Remove(sid); return; }
        var nick = p.Bot.Nickname;
        if (Blocked(s))
        {
            // Enter here would answer whatever Claude is asking. The line stays
            // where it is; the owner answers the prompt and sends it by hand.
            _submits.Remove(sid);
            Log.Info("Team.submit", $"session={sid:N} seq={seq} blocked by a prompt in the pane");
            Say(h, p.Bot, "waiting", seq,
                $"{nick} is waiting on you in their terminal — answer there, then press Enter to send the post");
            return;
        }
        p.Tries++;
        if (p.Tries < SubmitChecks.Length)
        {
            var ok = _h.PressEnter(s);
            Log.Info("Team.submit", $"session={sid:N} seq={seq} enter-again={p.Tries} ok={ok}");
            _h.Delay(() => CheckSubmitted(sid, seq), SubmitChecks[p.Tries]);
            return;
        }
        _submits.Remove(sid);
        if (Working(s))
        {
            // Mid-turn the line is queued, and the hook reports it only when
            // Claude gets to it. Not a failure, so nothing to say.
            Log.Info("Team.submit", $"session={sid:N} seq={seq} unconfirmed, bot is working (queued)");
            return;
        }
        Log.Info("Team.submit", $"session={sid:N} seq={seq} gave up");
        Say(h, p.Bot, "undelivered", seq,
            $"{nick} didn't take the post — it may be sitting in their terminal; open it and press Enter");
    }

    /// A system row about one bot, tied to a post. From = the bot, so the
    /// page can offer its terminal.
    private void Say((Project Project, TeamStore Store, TeamBot Bot) h, TeamBot bot, string ev, long seq, string text)
    {
        var e = h.Store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = bot.Slug, Event = ev, Note = seq.ToString(), Text = text,
        });
        PostEntries(h.Project.Id, h.Store, new[] { e });
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
        _submits.Remove(sess.Id);
        var wasWrapping = _wrapping.Remove(sess.Id);
        RefreshRoster(h.Project, h.Store);
        PostEntries(h.Project.Id, h.Store, new[] { e });
        if (wasWrapping) MaybeArchive(h.Project.Id);   // a closed tab has no context left to clear
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

    /// The owner posted. Resolve the recipients the page named — nobody named
    /// means everyone — record it; deliver it.
    public void OnPost(TeamPostMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        var text = (msg.Text ?? "").Trim();
        if (proj == null || store == null || text.Length == 0) return;

        // No one named = everyone. The owner talks to the room, not to a
        // picker; each bot judges from the text whether the post concerns it
        // (the roster says how, and what to answer when it doesn't). Nothing
        // sits between a post and its delivery.
        var (targets, everyone) = ResolveRecipients(store, msg.To);
        if (everyone) targets = store.Doc.Bots.ToList();
        if (targets.Count == 0)
        {
            Fallback(proj, store, "No bots on the team yet — add one first.");
            return;
        }

        var entry = new RoomEntry
        {
            Kind = "user", From = "you", Text = text, ClientId = msg.ClientId,
            To = everyone ? new List<string> { TeamRender.Everyone } : targets.Select(b => b.Slug).ToList(),
        };
        // Deliver FIRST, so the row lands with its verdict: "delivered" or
        // "waiting for the bot to wake" is a fact about this post, not a later
        // event the page has to reconcile.
        var attempts = Attempt(targets, text, everyone);
        entry.Delivered = attempts.All(a => a.Ok);
        store.Ledger.Append(entry);
        var events = Record(store, entry, attempts);
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
        // The first bot in a lead-type position leads. One lead only; the
        // owner can hand it to another bot from its menu.
        var becameLead = store.Doc.LeadSlug == null && pos.Hat == "captain";
        if (becameLead) store.Doc.LeadSlug = bot.Slug;
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
        var rows = new List<RoomEntry>
        {
            store.Ledger.Append(new RoomEntry
            {
                Kind = "system", From = "perch", Event = "joined", Text = $"{nickname} joined as {pos.Name}",
            }),
        };
        if (becameLead)
            rows.Add(store.Ledger.Append(new RoomEntry
            {
                Kind = "system", From = "perch", Event = "lead", Text = $"{nickname} leads the team",
            }));
        Log.Info("Team.create", $"project={proj.Id:N} bot={bot.Slug} cc={ccName} session={sess.Id:N} lead={becameLead}");
        _h.PushState();
        PostEntries(proj.Id, store, rows);
    }

    /// The owner hands the lead role to a bot. Its system prompt gains the
    /// role at its next launch; its per-prompt context tells it now.
    public void OnLeadSet(TeamLeadSetMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        var bot = store?.Doc.Bot(msg.BotId);
        if (proj == null || store == null || bot == null) return;
        if (store.Doc.IsLead(bot)) return;
        store.Doc.LeadSlug = bot.Slug;
        store.Save();
        store.RenderSystemFiles(proj.Name);
        RefreshRoster(proj, store);
        var e = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "lead", Text = $"{bot.Nickname} now leads the team",
        });
        Log.Info("Team.lead", $"project={proj.Id:N} bot={bot.Slug}");
        _h.PushState();
        PostEntries(proj.Id, store, new[] { e });
    }

    // ---- the task board -----------------------------------------------------

    /// `perch team task …` from a bot. The lead runs the board (main, assign,
    /// done); every bot keeps its own piece (mine). A bot overstepping gets a
    /// row in the room — it reads the board with its next prompt anyway.
    public void OnTeamTask(Session sess, Guid paneId, TeamTaskMessage msg)
    {
        if (BotOfSession(sess.Id) is not { } h) return;
        var op = (msg.Op ?? "").Trim().ToLowerInvariant();
        var title = (msg.Title ?? "").Trim();
        var isLead = h.Store.Doc.IsLead(h.Bot);
        var leadNick = h.Store.Doc.Lead?.Nickname;
        string NotLead(string what) => leadNick == null
            ? $"{h.Bot.Nickname} tried to {what}, but there is no lead yet — Joseph does that from the room"
            : $"{h.Bot.Nickname} tried to {what}; only the lead ({leadNick}) or Joseph can";
        switch (op)
        {
            case "main":
                if (!isLead) { Refuse(h.Project, h.Store, NotLead("set the task")); return; }
                if (title.Length == 0) { Refuse(h.Project, h.Store, $"{h.Bot.Nickname} set an empty task title"); return; }
                SetTask(h.Project, h.Store, title, h.Bot.Slug);
                break;
            case "assign":
                if (!isLead) { Refuse(h.Project, h.Store, NotLead("assign a piece")); return; }
                var name = (msg.Bot ?? "").Trim();
                var target = h.Store.Doc.Bots.FirstOrDefault(b =>
                    string.Equals(b.Slug, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(b.Nickname, name, StringComparison.OrdinalIgnoreCase)
                    || ClaudePeerNames.Matches(b.CcName, name));
                if (target == null) { Refuse(h.Project, h.Store, $"{h.Bot.Nickname} assigned a piece to \"{name}\", who isn't on the team"); return; }
                if (title.Length == 0) { Refuse(h.Project, h.Store, $"{h.Bot.Nickname} assigned {target.Nickname} an empty piece"); return; }
                UpsertItem(h.Project, h.Store, target, title, null, null, h.Bot);
                break;
            case "mine":
                UpsertItem(h.Project, h.Store, h.Bot, title, msg.Status, msg.Note, h.Bot);
                break;
            case "done":
                if (!isLead) { Refuse(h.Project, h.Store, NotLead("close the task")); return; }
                AskConfirm(h.Project, h.Store, h.Bot);
                break;
            default:
                Log.Info("Team.task", $"unknown op '{op}' from {h.Bot.Slug}");
                break;
        }
    }

    /// The owner sets or renames the task from the room.
    public void OnTaskSet(TeamTaskSetMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        var title = (msg.Title ?? "").Trim();
        if (proj == null || store == null || title.Length == 0) return;
        SetTask(proj, store, title, "you");
    }

    /// The owner confirms: the task is done, bots wrap up and reset.
    public void OnTaskConfirm(TeamTaskConfirmMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        if (proj == null || store == null) return;
        CompleteTask(proj, store);
    }

    /// The owner says not yet: the board goes back to open and the lead
    /// hears why, as an ordinary post from the owner.
    public void OnTaskReject(TeamTaskRejectMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        var board = store?.Tasks.Current;
        if (proj == null || store == null || board == null || board.Status != "review") return;
        board.Status = "open";
        board.ReviewBy = null;
        board.ReviewAtMs = null;
        store.SaveTasks();
        RefreshRoster(proj, store);
        var note = (msg.Note ?? "").Trim();
        var e = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "task", Text = "Not done yet — the task is open again",
        });
        _h.PushState();
        PostEntries(proj.Id, store, new[] { e });
        var lead = store.Doc.Lead;
        var text = note.Length > 0 ? $"Not done yet: {note}" : "Not done yet — keep going and ask again when it is.";
        OnPost(new TeamPostMsg
        {
            ProjectId = proj.Id, Text = text, ClientId = "",   // no optimistic row to reconcile: the owner didn't type this
            To = lead == null ? null : JsonDocument.Parse($"[\"{lead.Nickname}\"]").RootElement.Clone(),
        });
    }

    private void SetTask(Project proj, TeamStore store, string title, string setBy)
    {
        var tasks = store.Tasks;
        var who = setBy == "you" ? "Joseph" : store.Doc.Bot(setBy)?.Nickname ?? setBy;
        RoomEntry row;
        if (tasks.Current is { } cur && cur.Status != "done")
        {
            var old = cur.Title;
            cur.Title = title;
            if (cur.Status == "review") { cur.Status = "open"; cur.ReviewBy = null; cur.ReviewAtMs = null; }
            row = store.Ledger.Append(new RoomEntry
            {
                Kind = "system", From = "perch", Event = "task",
                Text = old == title ? $"Task set by {who}: {title}" : $"{who} renamed the task: {title}",
            });
        }
        else
        {
            if (tasks.Current != null) Archive(store, tasks.Current);   // a done board still wrapping: a new task supersedes it
            tasks.Current = new TaskBoard
            {
                Id = Guid.NewGuid().ToString("N")[..8], Title = title, SetBy = setBy,
                CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            row = store.Ledger.Append(new RoomEntry
            {
                Kind = "system", From = "perch", Event = "task", Text = $"Task set by {who}: {title}",
            });
        }
        store.SaveTasks();
        RefreshRoster(proj, store);
        Log.Info("Team.task", $"project={proj.Id:N} set by={setBy} title={TeamRender.OneLine(title, 80)}");
        _h.PushState();
        PostEntries(proj.Id, store, new[] { row });
    }

    private void UpsertItem(Project proj, TeamStore store, TeamBot bot, string title, string? status, string? note, TeamBot by)
    {
        var board = store.Tasks.Current;
        if (board == null || board.Status == "done")
        {
            Refuse(proj, store, $"{by.Nickname} reported a piece, but there is no task open — the lead sets one first");
            return;
        }
        var item = board.ItemOf(bot.Slug);
        if (item == null) { item = new TaskItem { Bot = bot.Slug }; board.Items.Add(item); }
        if (title.Length > 0) item.Title = title;
        var st = (status ?? "").Trim().ToLowerInvariant();
        if (TaskItem.Statuses.Contains(st)) item.Status = st;
        else if (by.Slug != bot.Slug && item.Status == "") item.Status = "todo";
        if (note != null) item.Note = note.Trim();
        item.UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (item.Title.Length == 0) item.Title = "(untitled)";
        store.SaveTasks();
        RefreshRoster(proj, store);
        var text = by.Slug == bot.Slug
            ? $"{bot.Nickname}: {item.Status} — {TeamRender.OneLine(item.Title, 120)}" + (item.Note.Length > 0 ? $" ({TeamRender.OneLine(item.Note, 120)})" : "")
            : $"{by.Nickname} gave {bot.Nickname}: {TeamRender.OneLine(item.Title, 120)}";
        var e = store.Ledger.Append(new RoomEntry { Kind = "system", From = "perch", Event = "task", Text = text });
        _h.PushState();
        PostEntries(proj.Id, store, new[] { e });
    }

    private void AskConfirm(Project proj, TeamStore store, TeamBot lead)
    {
        var board = store.Tasks.Current;
        if (board == null || board.Status != "open")
        {
            Refuse(proj, store, board == null ? $"{lead.Nickname} closed a task, but none is set" : $"{lead.Nickname} closed the task, but it is already {board.Status}");
            return;
        }
        board.Status = "review";
        board.ReviewBy = lead.Slug;
        board.ReviewAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.SaveTasks();
        RefreshRoster(proj, store);
        var e = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "task.review",
            Text = $"{lead.Nickname} says the task is done — confirm it in the board, or say not yet",
        });
        Log.Info("Team.task", $"project={proj.Id:N} review by={lead.Slug}");
        _h.PushState();
        PostEntries(proj.Id, store, new[] { e });
    }

    /// The owner confirmed. The board is done; every running bot is told to
    /// wrap up (memory first, then one line), and each is reset when that
    /// turn ends. Bots that aren't running have nothing to clear.
    private void CompleteTask(Project proj, TeamStore store)
    {
        var board = store.Tasks.Current;
        if (board == null || board.Status == "done") return;
        board.Status = "done";
        board.DoneAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.SaveTasks();
        RefreshRoster(proj, store);
        var rows = new List<RoomEntry>
        {
            store.Ledger.Append(new RoomEntry
            {
                Kind = "system", From = "perch", Event = "task.done", Text = $"Task done: {board.Title} — the team is wrapping up",
            }),
        };
        var text = $"The task \"{board.Title}\" is done. Wrap up now: update your memory file with what the next task will need " +
                   "(decisions, where things stand, unfinished threads), then reply with one line. Your context is cleared after that reply.";
        var attempts = Attempt(store.Doc.Bots.ToList(), text, everyone: true);
        var post = new RoomEntry
        {
            Kind = "user", From = "you", Text = text, To = new List<string> { TeamRender.Everyone },
            Delivered = attempts.All(a => a.Ok),
        };
        store.Ledger.Append(post);
        rows.Add(post);
        rows.AddRange(Record(store, post, attempts));
        foreach (var a in attempts)
            if (a.Ok && a.SessionId is Guid sid) _wrapping[sid] = (proj.Id, false);
        Log.Info("Team.task", $"project={proj.Id:N} done; wrapping={_wrapping.Count(kv => kv.Value.Project == proj.Id)}");
        _h.PushState();
        PostEntries(proj.Id, store, rows);
        MaybeArchive(proj.Id);
    }

    /// A wrapping bot's turn ended after the wrap-up post went in: clear its
    /// context. `/clear` re-fires the session-start hook, so the brief is
    /// re-applied and the next prompt carries the roster and its memory.
    private void ResetBot(Session sess, Guid projectId)
    {
        _wrapping.Remove(sess.Id);
        if (BotOfSession(sess.Id) is not { } h) { MaybeArchive(projectId); return; }
        var ok = _h.TypeToClaude(sess, "/clear");
        Log.Info("Team.reset", $"session={sess.Id:N} bot={h.Bot.Slug} ok={ok}");
        var e = h.Store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = h.Bot.Slug, Event = ok ? "reset" : "undelivered",
            Text = ok ? $"{h.Bot.Nickname} reset for the next task"
                      : $"{h.Bot.Nickname} couldn't be reset — open its terminal and run /clear",
        });
        PostEntries(h.Project.Id, h.Store, new[] { e });
        MaybeArchive(projectId);
    }

    /// Once nobody is left wrapping, a done board moves to the archive and
    /// the room is ready for the next task.
    private void MaybeArchive(Guid projectId)
    {
        if (_wrapping.Any(kv => kv.Value.Project == projectId)) return;
        var proj = _h.ProjectById(projectId);
        var store = StoreFor(projectId);
        var board = store?.Tasks.Current;
        if (proj == null || store == null || board == null || board.Status != "done") return;
        Archive(store, board);
        store.Tasks.Current = null;
        store.SaveTasks();
        RefreshRoster(proj, store);
        var e = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "task", Text = "Everyone is reset — ready for the next task",
        });
        _h.PushState();
        PostEntries(proj.Id, store, new[] { e });
    }

    private static void Archive(TeamStore store, TaskBoard board)
    {
        if (board.Status != "done") { board.Status = "done"; board.DoneAtMs ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
        store.Tasks.Done.Add(board);
        while (store.Tasks.Done.Count > TaskDoc.DoneKept) store.Tasks.Done.RemoveAt(0);
    }

    private void Refuse(Project proj, TeamStore store, string text)
    {
        Log.Info("Team.task.refused", text);
        Fallback(proj, store, text);
    }

    /// Start a bot that has no tab here: one that arrived with a pull from
    /// another machine, or whose tab was closed. Same nickname, same position,
    /// same face and memory; a fresh tab (and worktree, if the bot uses one)
    /// on this machine. The address is kept unless something here already
    /// answers to it.
    public async Task OnBotStartAsync(TeamBotStartMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        var bot = store?.Doc.Bot(msg.BotId);
        if (proj == null || store == null || bot == null) return;
        if (bot.SessionId is Guid existing && _h.SessionById(existing) != null)
        {
            Toast($"{bot.Nickname} is already running.");
            return;
        }
        var pos = store.Doc.Position(bot.PositionSlug);
        if (pos == null) { Toast($"{bot.Nickname}'s position is missing from the team file."); return; }

        var ccName = bot.CcName;
        var takenByLivePane = _h.Sessions().Any(s => PaneTree.AllLeaves(s.Root)
            .Any(p => ClaudePeerNames.Matches(p.PeerName ?? "", ccName) || ClaudePeerNames.Matches(p.PinnedPeerName ?? "", ccName)));
        if (takenByLivePane)
        {
            ccName = MintCcName(bot.Nickname);
            Log.Info("Team.start", $"bot={bot.Slug} address {bot.CcName} is taken here; now {ccName}");
            bot.CcName = ccName;
        }
        var model = string.IsNullOrWhiteSpace(bot.Model) ? pos.Model : bot.Model;
        store.RenderSystemFiles(proj.Name);
        RefreshRoster(proj, store);

        var sess = await _h.CreateTab(proj, bot.Nickname, bot.Worktree, model, ccName);
        if (sess == null) return;   // CreateTab already toasted why
        bot.SessionId = sess.Id;
        store.Save();
        PublishMarkers(sess);
        RefreshRoster(proj, store);
        var e = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "joined", Text = $"{bot.Nickname} started here as {pos.Name}",
        });
        Log.Info("Team.start", $"project={proj.Id:N} bot={bot.Slug} cc={ccName} session={sess.Id:N}");
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

    private void Fallback(Project proj, TeamStore store, string text)
    {
        var e = store.Ledger.Append(new RoomEntry { Kind = "system", From = "perch", Event = "error", Text = text });
        PostEntries(proj.Id, store, new[] { e });
    }

    // ---- delivery ---------------------------------------------------------

    /// Ok = the line was typed; Missing = the bot has no tab at all; Blocked =
    /// its pane is showing a prompt of its own; none of them = parked (asleep
    /// or booting).
    private sealed record Attempted(TeamBot Bot, bool Ok, bool Missing, bool Blocked, string Line, Guid? SessionId);

    /// Try to type the post into each target's terminal now. Anything not
    /// typed is parked, to be flushed when the session is up and free.
    private List<Attempted> Attempt(List<TeamBot> targets, string text, bool everyone)
    {
        var results = new List<Attempted>();
        foreach (var bot in targets)
        {
            var line = DeliveryLine(text, everyone ? null : bot.Nickname);
            var sess = bot.SessionId is Guid id ? _h.SessionById(id) : null;
            if (sess == null) { results.Add(new Attempted(bot, false, true, false, line, null)); continue; }
            if (sess.Dormant) _h.Wake(sess);
            var blocked = !sess.Dormant && Blocked(sess);
            var ok = !sess.Dormant && !blocked && _h.TypeToClaude(sess, line);
            results.Add(new Attempted(bot, ok, false, blocked, line, sess.Id));
        }
        return results;
    }

    /// Write the attempts into the ledger against the post's seq: "undelivered"
    /// for a bot with no tab, a "waiting" row (with the bot's terminal one
    /// click away) for one whose pane is asking something, and parking (with
    /// a log line) for those and the rest. Successful deliveries are recorded
    /// on the post itself (Delivered) rather than as rows — the page shows
    /// them as the post's own status line, not as chatter — and each typed
    /// line is then held until the hook confirms it (Expect).
    private List<RoomEntry> Record(TeamStore store, RoomEntry post, List<Attempted> attempts)
    {
        var events = new List<RoomEntry>();
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
                Expect(a.SessionId!.Value, a.Bot, post.Seq);
            }
            else if (a.SessionId is Guid sid)
            {
                Log.Info("Team.parked", $"session={sid:N} seq={post.Seq} bot={a.Bot.Slug}{(a.Blocked ? " (pane is asking something)" : "")}");
                if (!_parked.TryGetValue(sid, out var list)) _parked[sid] = list = new();
                list.Add((post.Seq, a.Line, a.Bot.Nickname));
                if (a.Blocked)
                    events.Add(store.Ledger.Append(new RoomEntry
                    {
                        Kind = "system", From = a.Bot.Slug, Event = "waiting", Note = post.Seq.ToString(),
                        Text = $"{a.Bot.Nickname} is waiting on you in their terminal — this goes through once that's answered",
                    }));
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

    /// Who the page named. Nobody it could name — null, an empty list, only
    /// unknown names — is everyone.
    private (List<TeamBot> Targets, bool Everyone) ResolveRecipients(TeamStore store, JsonElement? to)
    {
        var targets = new List<TeamBot>();
        if (to is not { } el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return (targets, true);
        if (el.ValueKind == JsonValueKind.String)
        {
            // "everyone", or a single nickname (what `perch test team.post
            // --to Ada` can express; the page always sends an array).
            var s = (el.GetString() ?? "").Trim();
            if (string.Equals(s, "everyone", StringComparison.OrdinalIgnoreCase)) return (targets, true);
            var one = store.Doc.Bots.FirstOrDefault(b =>
                string.Equals(b.Nickname, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(b.Slug, s, StringComparison.OrdinalIgnoreCase)
                || ClaudePeerNames.Matches(b.CcName, s));
            if (one != null) targets.Add(one);
            return (targets, targets.Count == 0);
        }
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var name = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
                if (string.Equals(name, "everyone", StringComparison.OrdinalIgnoreCase)) return (targets, true);
                var bot = store.Doc.Bots.FirstOrDefault(b =>
                    string.Equals(b.Nickname, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(b.Slug, name, StringComparison.OrdinalIgnoreCase)
                    || ClaudePeerNames.Matches(b.CcName, name));
                if (bot != null && !targets.Contains(bot)) targets.Add(bot);
            }
        }
        return (targets, targets.Count == 0);
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
    ///
    /// What started the bot's turn decides whether what it says belongs in
    /// the room. A room post (a typed `[Perch team]` prompt) does: the reply
    /// is for the owner. A teammate's message doesn't: the exchange itself is
    /// already in the room from the sender's hook, and "Replied to Bo with a
    /// hello" on top of it is narration nobody asked for. Anything else the
    /// owner typed into the terminal is answered in the terminal. And a
    /// `(no reply)` — the answer to a post that wasn't for this bot — is
    /// dropped. Tool calls are kept regardless; they fold.
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
                // First sight of this transcript in this process (a fresh bot,
                // or Perch restarted under a running one). Skip only what the
                // ledger already holds from this bot — by time, not by count —
                // so a reply that landed before the room's first poll is kept
                // and a restart doesn't replay yesterday.
                var newest = store.Ledger.ReadAll()
                    .Where(r => r.From == bot.Slug && r.Kind is "beat" or "work")
                    .Select(r => r.TsMs).DefaultIfEmpty(0).Max();
                var start = 0;
                while (start < events.Count && ParseTs(events[start].Ts) <= newest) start++;
                _ingested[key] = (pane.ClaudeSessionId!, start, AnsweringAt(events, start));
                seen = _ingested[key];
            }
            var answering = seen.Answering;
            var from = Math.Min(seen.Count, events.Count);
            for (var i = from; i < events.Count; i++)
            {
                var e = events[i];
                RoomEntry? entry = null;
                switch (e.Kind)
                {
                    case "prompt":
                    case "peer":
                        answering = StartsAnswer(e);
                        break;
                    case "beat" when answering && e.Text.Trim().Length > 0 && !IsNoReply(e.Text):
                        entry = new RoomEntry
                        {
                            Kind = "beat", From = bot.Slug, Text = e.Text.Trim(), PaneId = pane.Id.ToString("D"),
                            TsMs = ParseTs(e.Ts),
                        };
                        break;
                    case "work":
                    case "skill":
                        entry = new RoomEntry
                        {
                            Kind = "work", From = bot.Slug, Text = "", Verb = e.Verb, Target = e.Target,
                            Note = string.IsNullOrEmpty(e.Note) ? null : e.Note, Repeat = e.Repeat,
                            PaneId = pane.Id.ToString("D"), TsMs = ParseTs(e.Ts),
                        };
                        break;
                }
                if (entry != null) added.Add(store.Ledger.Append(entry));
            }
            _ingested[key] = (pane.ClaudeSessionId!, events.Count, answering);
        }
        // Not posted separately: the caller is about to reply with everything
        // since the page's watermark, which includes these.
    }

    /// Whether a turn-starting event is a room post — the one kind of turn
    /// whose replies are for the owner.
    private static bool StartsAnswer(InspectorEvent e)
        => e.Kind == "prompt" && e.Text.StartsWith(TeamRender.PostPrefix, StringComparison.Ordinal);

    /// The answering state as of `index`: what the last turn-starter before
    /// it was. For a transcript first seen mid-way.
    internal static bool AnsweringAt(IReadOnlyList<InspectorEvent> events, int index)
    {
        for (var i = Math.Min(index, events.Count) - 1; i >= 0; i--)
            if (events[i].Kind is "prompt" or "peer") return StartsAnswer(events[i]);
        return false;
    }

    internal static bool IsNoReply(string text)
        => text.TrimStart().StartsWith(TeamRender.NoReply.TrimEnd(')'), StringComparison.OrdinalIgnoreCase);

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
