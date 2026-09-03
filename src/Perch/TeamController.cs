using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    /// Raw bytes into a pane's PTY (PaneManager.Write) — for answering a
    /// dialog with keys, as opposed to typing a line for Claude to read.
    public required Action<Guid, byte[]> WriteRaw { get; init; }
    /// A prompt on this pane was answered from the room (a permission card):
    /// drop the pane's Permission/Waiting state so posts stop being parked
    /// for a dialog that will never be shown. Optional: tests leave it null.
    public Action<Guid>? ClearPrompt { get; init; }
    /// Change a pane's Claude model (the `pane.model` path: persists the
    /// alias and types `/model <alias>` live). "" = the account default.
    public Action<Guid, string> SetPaneModel { get; init; } = (_, _) => { };
    /// Does this pane have a live terminal (PaneManager.Has)? Null = assume yes.
    public Func<Guid, bool>? HasPty { get; init; }
    /// Start the terminals of a session whose panes never spawned — a tab
    /// restored but not looked at since the restart — arming its resume first
    /// so its Claude comes back with its conversation. Optional.
    public Action<Session>? EnsureRunning { get; init; }
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
    /// Bot slug → the pane whose Claude is stuck on its start-up question
    /// ("trust this folder?"), until the owner answers from the room.
    private readonly Dictionary<string, Guid> _awaitingTrust = new(StringComparer.OrdinalIgnoreCase);
    /// Pane → when the owner last answered one of its permission prompts from
    /// the room. A prompt notice arriving within PromptAnswerGrace of that is
    /// the same prompt, already settled, and must not mark the pane as asking.
    private readonly Dictionary<Guid, DateTimeOffset> _permAnswered = new();
    internal static readonly TimeSpan PromptAnswerGrace = TimeSpan.FromSeconds(20);

    /// True when a permission prompt on this pane was answered from the room
    /// within the last PromptAnswerGrace — the window then treats a "permission"
    /// status for it as working, not as a dialog waiting for a person.
    public bool PromptAnsweredRecently(Guid paneId)
        => _permAnswered.TryGetValue(paneId, out var at) && DateTimeOffset.UtcNow - at < PromptAnswerGrace;
    /// Per pane: how many collapsed transcript events are already in the
    /// ledger, for which Claude session, and whether the bot's current turn
    /// is answering a room post (its beats then belong in the room).
    private readonly Dictionary<Guid, (string Session, int Count, bool Answering)> _ingested = new();

    /// A typed line the prompt-submit hook hasn't reported yet, per session.
    private sealed class PendingSubmit
    {
        public required long Seq { get; init; }
        public required TeamBot Bot { get; init; }
        /// The exact line that was typed, so it can be typed again.
        public required string Line { get; init; }
        public int Tries;
        /// Whether the line has already been typed a second time.
        public bool Retried;
    }
    private readonly Dictionary<Guid, PendingSubmit> _submits = new();

    /// Bots told to wrap up after the owner confirmed a task: session → the
    /// project, and whether the wrap-up post was seen submitted. A bot is
    /// reset (its context cleared) when its turn ends AFTER that post went
    /// in — a Done from the turn it was busy with doesn't count.
    private readonly Dictionary<Guid, (Guid Project, string TaskId, bool Confirmed)> _wrapping = new();

    /// Permission requests a bot's hook is holding for the room: id → (bot
    /// slug, pane). The hook polls for the answer file; the owner's click
    /// writes it (OnPermAnswer).
    private readonly Dictionary<string, (string Slug, Guid PaneId)> _awaitingPerm = new(StringComparer.OrdinalIgnoreCase);

    /// Ask cards not yet answered: id → the asking bot's slug.
    private readonly Dictionary<string, string> _asks = new(StringComparer.OrdinalIgnoreCase);

    /// Model rate limits as last reported; bots moved off a limited model,
    /// slug → the alias they were on; and when each bot was last switched
    /// (a limit list refreshes often, a bot is moved at most once a minute).
    private IReadOnlyList<ModelUsageLimit> _limits = Array.Empty<ModelUsageLimit>();
    private readonly Dictionary<string, string> _modelSwitched = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _modelActedAt = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly string[] ModelFallback = { "fable", "opus", "sonnet", "haiku" };
    internal static readonly TimeSpan ModelDebounce = TimeSpan.FromSeconds(60);
    /// The clock, swappable so a test can step past the debounce.
    internal Func<DateTimeOffset> Now = () => DateTimeOffset.UtcNow;

    // ---- model limits -------------------------------------------------------

    /// The account's rate limits changed. A running bot whose model is at
    /// its limit is moved to the first free one in fable → opus → sonnet →
    /// haiku and told so in the room; when its model comes back, it is moved
    /// back. A bot whose model Perch can't tell (no alias set, transcript not
    /// yet naming one) is left alone.
    public void OnModelLimits(IReadOnlyList<ModelUsageLimit> limits)
    {
        _limits = limits ?? Array.Empty<ModelUsageLimit>();
        bool Limited(string alias) => _limits.Any(l => l.AtLimit && string.Equals(l.Alias, alias, StringComparison.OrdinalIgnoreCase));
        var now = Now();
        foreach (var proj in _h.Projects())
        {
            var store = StoreFor(proj.Id);
            if (store == null) continue;
            var rows = new List<RoomEntry>();
            foreach (var bot in store.Doc.Bots)
            {
                var sess = bot.SessionId is Guid id ? _h.SessionById(id) : null;
                var pane = sess == null ? null : PaneTree.AllLeaves(sess.Root).FirstOrDefault(p => p.IsTerminal);
                if (sess == null || pane == null || sess.Dormant) continue;
                if (_modelActedAt.TryGetValue(bot.Slug, out var last) && now - last < ModelDebounce) continue;

                if (_modelSwitched.TryGetValue(bot.Slug, out var original))
                {
                    // Moved off `original` earlier: back the moment it is free.
                    if (Limited(original)) continue;
                    _modelSwitched.Remove(bot.Slug);
                    _modelActedAt[bot.Slug] = now;
                    _h.SetPaneModel(pane.Id, original);
                    Log.Info("Team.model", $"bot={bot.Slug} back to {original}");
                    rows.Add(store.Ledger.Append(new RoomEntry
                    {
                        Kind = "system", From = "perch", Event = "model", To = new List<string> { bot.Slug },
                        Text = $"{original} is back — {bot.Nickname} switched back",
                    }));
                    continue;
                }

                var current = EffectiveModel(sess, pane);
                if (current == null || !Limited(current)) continue;
                var next = ModelFallback.FirstOrDefault(a => !Limited(a));
                if (next == null || string.Equals(next, current, StringComparison.OrdinalIgnoreCase)) continue;
                _modelSwitched[bot.Slug] = current;
                _modelActedAt[bot.Slug] = now;
                _h.SetPaneModel(pane.Id, next);
                var until = ResetWord(_limits.First(l => l.AtLimit && string.Equals(l.Alias, current, StringComparison.OrdinalIgnoreCase)));
                Log.Info("Team.model", $"bot={bot.Slug} {current} at limit{until} → {next}");
                rows.Add(store.Ledger.Append(new RoomEntry
                {
                    Kind = "system", From = "perch", Event = "model", To = new List<string> { bot.Slug },
                    Text = $"{current} is at its limit{until} — {bot.Nickname} switched to {next}",
                }));
            }
            RefreshRoster(proj, store);
            if (rows.Count > 0) PostEntries(proj.Id, store, rows);
        }
    }

    /// The alias a bot is on: the pane's setting, else what its transcript
    /// says the model actually is; null when neither is known.
    private string? EffectiveModel(Session sess, PaneNode pane)
    {
        if (!string.IsNullOrWhiteSpace(pane.Model)) return pane.Model.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(pane.ClaudeSessionId)) return null;
        var data = _h.ReadTranscript(pane.Id, pane.ClaudeSessionId, _h.ResolveCwd(sess, pane));
        return AliasFromModelId(data?.Vitals?.Model);
    }

    /// "claude-fable-5-1" → "fable"; null when the id names none of the aliases.
    internal static string? AliasFromModelId(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var m = modelId.ToLowerInvariant();
        return ModelFallback.FirstOrDefault(a => m.Contains(a));
    }

    /// " until 14:05" (local time) when the limit says when it lifts, else "".
    private static string ResetWord(ModelUsageLimit l)
        => l.ResetsAtMs is long ms ? $" until {DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime():HH:mm}" : "";

    /// The roster's "Model limits right now" line, or null when none is active.
    private string? ModelLimitsLine()
    {
        var active = _limits.Where(l => l.AtLimit).Select(l => l.Alias + ResetWord(l)).ToList();
        return active.Count == 0 ? null : "Model limits right now: " + string.Join(", ", active);
    }

    /// After typing: how long to wait for the hook's confirmation before
    /// looking again. The first two checks press Enter again (a line the paste
    /// detector swallowed needs it); the rest only wait.
    ///
    /// The waiting is long on purpose. Claude reports a queued line when it
    /// gets to it, which is when its current turn's tool calls finish — often
    /// minutes. The old three-check, ten-second budget declared "didn't take
    /// the post" while the bot was simply busy, which is the false alarm
    /// Joseph kept seeing; the pane's state at that instant isn't proof
    /// either, since a bot can look idle between two tool calls.
    internal static readonly TimeSpan[] SubmitChecks =
    {
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120),
    };

    /// How many of those checks press Enter again before it just waits.
    internal const int SubmitEnterTries = 2;

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
                    cached.RenderSystemFiles(proj.Name);
                    RefreshRoster(proj, cached);
                    _h.PushState();
                }
                return cached;
            }
            _stores.Remove(projectId);
        }
        var store = TeamStore.Open(proj.Path);
        if (store != null)
        {
            _stores[projectId] = store;
            // First sight of this team in this process: render the local
            // files now. They live under local/ (never committed), so a fresh
            // clone, a pull, or the folder's migration leaves them missing —
            // and a marker pointing at a missing system.md is dropped, which
            // would launch the bot without its brief.
            store.RenderSystemFiles(proj.Name);
            RefreshRoster(proj, store);
        }
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
            tasks = TasksView(projectId, store),
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

    /// The open tasks for the page, open ones first: each board, every piece
    /// by nickname, and which bots are still wrapping up after its confirm.
    private object[] TasksView(Guid projectId, TeamStore store)
    {
        string Nick(string slug) => slug == "you" ? "you" : store.Doc.Bot(slug)?.Nickname ?? slug;
        return store.Tasks.Open
            .OrderBy(b => b.Status == "done" ? 1 : 0).ThenBy(b => b.CreatedAtMs)
            .Select(b => (object)new
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
                wrapping = _wrapping.Where(kv => kv.Value.Project == projectId && kv.Value.TaskId == b.Id)
                    .Select(kv => store.Doc.BotBySession(kv.Key)?.Slug).Where(s => s != null).ToArray(),
            }).ToArray();
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
        var tbot = ResolvePeerTarget(h.Store, target);
        var (label, body) = SplitHandoff(msg.Message ?? msg.Text ?? "");
        var to = tbot?.Nickname ?? TeamRender.OneLine(target, 40);
        if (tbot == null) Log.Info("Team.peer", $"unresolved target '{TeamRender.OneLine(target, 80)}' from {h.Bot.Slug}");

        // A send that didn't land is not a message — showing its body would put
        // words in a teammate's inbox that never arrived, and a bot that retries
        // (a shared name needs a ref, an address goes stale) would fill the room
        // with what reads as the same message three times. One quiet line
        // instead, and the retry that works is the only bubble.
        if (msg.Ok == false)
        {
            var why = (msg.Reason ?? "").Trim();
            var failed = h.Store.Ledger.Append(new RoomEntry
            {
                Kind = "system", From = h.Bot.Slug, Event = "peer.failed",
                To = new List<string> { tbot?.Slug ?? "(another session)" },
                Text = $"{h.Bot.Nickname} couldn't reach {to}" + (why.Length > 0 ? $" — {why}" : "") + "; it will try again",
            });
            Log.Info("Team.peer.failed", $"{h.Bot.Slug} → '{TeamRender.OneLine(target, 40)}': {TeamRender.OneLine(why, 120)}");
            PostEntries(h.Project.Id, h.Store, new[] { failed });
            return;
        }

        // Same body, same pair, moments ago: the send went out twice (a retry
        // whose first attempt did land, a hook that fired twice). One bubble.
        if (RecentlySaid(h.Store, h.Bot.Slug, tbot?.Slug, body))
        {
            Log.Info("Team.peer.dupe", $"{h.Bot.Slug} → {to}: same message again, not shown twice");
            return;
        }
        var entry = h.Store.Ledger.Append(new RoomEntry
        {
            Kind = "peer",
            From = h.Bot.Slug,
            To = new List<string> { tbot?.Slug ?? "(another session)" },
            Text = body,
            Summary = msg.Summary,
            Ok = msg.Ok,
            Note = label,
        });
        PostEntries(h.Project.Id, h.Store, new[] { entry });
    }

    /// How long the same bot→bot body counts as a repeat rather than a new
    /// message. A retry after a name clash comes seconds later; a bot saying
    /// the same thing again a quarter of an hour on means it.
    internal static readonly TimeSpan PeerRepeatWindow = TimeSpan.FromMinutes(15);

    /// Did this pair already carry this exact body, just now? Reads the tail of
    /// the ledger rather than keeping state, so it survives a restart.
    internal static bool RecentlySaid(TeamStore store, string from, string? toSlug, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var (recent, _) = store.Ledger.ReadSince(store.Ledger.LastSeq - 60);
        var cutoff = DateTimeOffset.UtcNow.Subtract(PeerRepeatWindow).ToUnixTimeMilliseconds();
        return recent.Any(e => e.Kind == "peer" && e.TsMs >= cutoff
                               && string.Equals(e.From, from, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(e.To?.FirstOrDefault() ?? "", toSlug ?? "(another session)", StringComparison.OrdinalIgnoreCase)
                               && string.Equals(e.Text, body, StringComparison.Ordinal));
    }

    /// The bot a SendMessage target names. Bots address each other by session
    /// name, but a reply often goes to the ADDRESS the message came with
    /// (`uds:\\.\pipe\…`), and cc disambiguates a shared name with a bracketed
    /// id (`ada [7d217e]`); all of those must land on the same roster row.
    internal TeamBot? ResolvePeerTarget(TeamStore store, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var t = target.Trim();
        // "name [7d217e]" → "name"
        var bracket = t.LastIndexOf(" [", StringComparison.Ordinal);
        if (bracket > 0 && t.EndsWith("]", StringComparison.Ordinal)) t = t[..bracket].Trim();
        var byName = store.Doc.Bots.FirstOrDefault(b =>
            string.Equals(b.Nickname, t, StringComparison.OrdinalIgnoreCase)
            || string.Equals(b.Slug, t, StringComparison.OrdinalIgnoreCase)
            || ClaudePeerNames.Matches(b.CcName, t));
        if (byName != null) return byName;
        // A reply address: match a pane's own inbox socket, with or without
        // the `uds:` prefix and regardless of slash direction.
        static string Norm(string s)
        {
            s = s.Trim();
            if (s.StartsWith("uds:", StringComparison.OrdinalIgnoreCase)) s = s[4..];
            return s.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        }
        var want = Norm(t);
        if (want.Length == 0) return null;
        foreach (var bot in store.Doc.Bots)
        {
            var sess = bot.SessionId is Guid id ? _h.SessionById(id) : null;
            if (sess == null) continue;
            foreach (var p in PaneTree.AllLeaves(sess.Root))
                if (!string.IsNullOrEmpty(p.MessagingSocket) && Norm(p.MessagingSocket!) == want) return bot;
        }
        return null;
    }

    /// The handoff kind a teammate message starts with (`HANDOFF:`,
    /// `REPORT:`, `QUESTION:`, `ANSWER:`, `FYI:`), lower-cased, and the text
    /// without it. No prefix → (null, text).
    internal static (string? Label, string Text) SplitHandoff(string text)
    {
        var t = (text ?? "").TrimStart();
        foreach (var k in new[] { "handoff", "report", "question", "answer", "fyi" })
        {
            if (t.Length > k.Length + 1 && t.StartsWith(k, StringComparison.OrdinalIgnoreCase) && t[k.Length] == ':')
                return (k, t[(k.Length + 1)..].TrimStart());
        }
        return (null, text ?? "");
    }

    /// `perch team post` from a bot: a note for the owner, pinging nobody —
    /// with a picture when it attached one.
    ///
    /// A note long enough to be a document is stored as one instead. A ticket
    /// draft pasted into the feed pushes everything else off the screen and is
    /// unreadable there anyway; as an artefact it is a card the owner opens
    /// when he wants it, and the conversation stays a conversation.
    public void OnTeamPost(Session sess, Guid paneId, TeamPostMessage msg)
    {
        if (BotOfSession(sess.Id) is not { } h) return;
        var text = (msg.Text ?? "").Trim();
        var image = (msg.Image ?? "").Trim();
        if (image.Length > 0 && !(IsImagePath(image) && File.Exists(image))) image = "";
        if (text.Length == 0 && image.Length == 0) return;
        if (image.Length == 0 && IsLongEnoughToStore(text))
        {
            var (title, summary) = TitleAndSummaryOf(text);
            Log.Info("Team.artefact.promoted", $"bot={h.Bot.Slug} chars={text.Length} title={TeamRender.OneLine(title, 60)}");
            StoreArtefact(h.Project, h.Store, h.Bot.Slug, title, summary, "md", text);
            return;
        }
        var entry = h.Store.Ledger.Append(new RoomEntry
        {
            Kind = "note", From = h.Bot.Slug, To = new List<string> { TeamRender.Everyone }, Text = text,
            Image = image.Length > 0 ? image : null,
        });
        PostEntries(h.Project.Id, h.Store, new[] { entry });
    }

    // ---- artefacts ----------------------------------------------------------

    /// Formats an artefact may be, all of them text: the room shows the body,
    /// it never runs it. An extension outside this list is refused rather than
    /// stored, so "here is the installer" can't become an artefact.
    internal static readonly string[] ArtefactKinds =
    {
        "md", "txt", "html", "json", "csv", "log", "diff", "sql", "ts", "cs", "py",
    };

    /// Biggest body kept. Past this the tail is dropped with a marker: the
    /// point of an artefact is that the owner can read it, and the page holds
    /// the whole thing in memory.
    internal const int ArtefactMaxBytes = 256 * 1024;

    /// A `perch team post` this long is a document, not a message.
    internal const int ArtefactPromoteChars = 1200;
    internal const int ArtefactPromoteLines = 14;

    internal static bool IsLongEnoughToStore(string text)
        => text.Length > ArtefactPromoteChars
           || text.AsSpan().Count('\n') + 1 > ArtefactPromoteLines;

    /// The title and one-line summary a promoted post gets: its own first line
    /// (a bot writes one), then whatever the next line says.
    internal static (string Title, string? Summary) TitleAndSummaryOf(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var i = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (i < 0) return ("Untitled", null);
        var title = lines[i].Trim().TrimStart('#', '*', '-', ' ').Trim();
        if (title.Length > 80) title = title[..80].TrimEnd() + "…";
        var j = Array.FindIndex(lines, i + 1, l => l.Trim().Length > 0);
        var summary = j < 0 ? null : TeamRender.OneLine(lines[j].Trim(), 120);
        return (title.Length == 0 ? "Untitled" : title, string.IsNullOrWhiteSpace(summary) ? null : summary);
    }

    /// `perch team artefact` from a bot.
    public void OnTeamArtefact(Session sess, Guid paneId, TeamArtefactMessage msg)
    {
        if (BotOfSession(sess.Id) is not { } h) return;
        var path = (msg.Path ?? "").Trim();
        var text = msg.Text ?? "";
        var ext = (msg.Ext ?? "").Trim().TrimStart('.').ToLowerInvariant();
        var title = (msg.Title ?? "").Trim();
        var summary = string.IsNullOrWhiteSpace(msg.Summary) ? null : TeamRender.OneLine(msg.Summary, 200);

        if (path.Length > 0)
        {
            if (ext.Length == 0) ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            try
            {
                if (!File.Exists(path)) { Fallback(h.Project, h.Store, $"{h.Bot.Nickname} shared a file that isn't there: {TeamRender.OneLine(path, 80)}"); return; }
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                Log.Error("Team.artefact.read", ex);
                Fallback(h.Project, h.Store, $"{h.Bot.Nickname} shared a file Perch couldn't read: {TeamRender.OneLine(path, 80)}");
                return;
            }
            if (title.Length == 0) title = HeadingOf(text) ?? Path.GetFileName(path);
        }
        if (ext.Length == 0) ext = "md";
        if (!ArtefactKinds.Contains(ext))
        {
            Fallback(h.Project, h.Store,
                $"{h.Bot.Nickname} tried to share a .{TeamRender.OneLine(ext, 12)} — the room shows text files ({string.Join(", ", ArtefactKinds)})");
            return;
        }
        if (text.Trim().Length == 0) { Fallback(h.Project, h.Store, $"{h.Bot.Nickname} shared an empty artefact"); return; }
        if (title.Length == 0) title = HeadingOf(text) ?? TitleAndSummaryOf(text).Title;
        StoreArtefact(h.Project, h.Store, h.Bot.Slug, title, summary, ext, text);
    }

    /// A markdown file's first heading, when it has one — the title a bot
    /// meant, without being asked for it twice.
    private static string? HeadingOf(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith('#')) return null;
            var t = line.TrimStart('#').Trim();
            return t.Length == 0 ? null : t.Length > 80 ? t[..80].TrimEnd() + "…" : t;
        }
        return null;
    }

    /// Write the body under the team's local folder and put its card in the
    /// room. The id names the file, so the page can ask for it back by id
    /// alone and no path from a bot is ever opened by the page.
    private void StoreArtefact(Project proj, TeamStore store, string from, string title, string? summary, string ext, string body)
    {
        var truncated = false;
        if (Encoding.UTF8.GetByteCount(body) > ArtefactMaxBytes)
        {
            body = CutToBytes(body, ArtefactMaxBytes) + "\n\n… (cut here: the artefact was over 256 KB)";
            truncated = true;
        }
        var id = TaskDoc.NewId();
        try
        {
            Directory.CreateDirectory(store.ArtefactsDir);
            AtomicFile.WriteAllText(store.ArtefactPathFor(id, ext), body);
        }
        catch (Exception ex)
        {
            Log.Error("Team.artefact.write", ex);
            Fallback(proj, store, "Perch couldn't save that artefact — the room kept nothing.");
            return;
        }
        var entry = store.Ledger.Append(new RoomEntry
        {
            Kind = "artefact", From = from, To = new List<string> { TeamRender.Everyone },
            Text = title, Target = id, Note = ext,
            Summary = truncated ? (summary == null ? "cut at 256 KB" : summary + " (cut at 256 KB)") : summary,
        });
        Log.Info("Team.artefact", $"project={proj.Id:N} id={id} ext={ext} from={from} bytes={body.Length}");
        PostEntries(proj.Id, store, new[] { entry });
    }

    /// The room opening an artefact: its body, by id.
    public void OnArtefactOpen(TeamArtefactOpenMsg msg)
    {
        var store = StoreFor(msg.ProjectId);
        var row = store == null ? null : ArtefactRows(store).FirstOrDefault(e => e.Target == msg.Id);
        if (store == null || row == null)
        {
            _h.Post(new { type = "team.artefact.data", projectId = msg.ProjectId.ToString("D"), id = msg.Id, error = "The room doesn't have that artefact." });
            return;
        }
        var path = store.ArtefactPathFor(row.Target!, row.Note ?? "md");
        string content;
        try
        {
            if (!File.Exists(path))
            {
                _h.Post(new { type = "team.artefact.data", projectId = msg.ProjectId.ToString("D"), id = msg.Id, error = "That artefact's file is gone." });
                return;
            }
            content = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Log.Error("Team.artefact.open", ex);
            _h.Post(new { type = "team.artefact.data", projectId = msg.ProjectId.ToString("D"), id = msg.Id, error = "Perch couldn't read that artefact." });
            return;
        }
        // A file edited by hand since it was stored could be any size; the page
        // gets at most the cap either way.
        var truncated = Encoding.UTF8.GetByteCount(content) > ArtefactMaxBytes;
        if (truncated) content = CutToBytes(content, ArtefactMaxBytes);
        _h.Post(new
        {
            type = "team.artefact.data",
            projectId = msg.ProjectId.ToString("D"),
            id = row.Target,
            title = row.Text,
            kind = row.Note ?? "md",
            from = row.From,
            tsMs = row.TsMs,
            content,
            truncated,
        });
    }

    /// Everything the room still has, newest first — the artefacts menu.
    public void OnArtefactList(TeamArtefactListMsg msg)
    {
        var store = StoreFor(msg.ProjectId);
        var items = new List<object>();
        if (store != null)
        {
            var rows = ArtefactRows(store);
            for (var i = rows.Count - 1; i >= 0 && items.Count < ArtefactListMax; i--)
            {
                var e = rows[i];
                if (!File.Exists(store.ArtefactPathFor(e.Target!, e.Note ?? "md"))) continue;   // deleted by hand
                items.Add(new { id = e.Target, title = e.Text, kind = e.Note ?? "md", from = e.From, tsMs = e.TsMs, summary = e.Summary });
            }
        }
        _h.Post(new { type = "team.artefact.index", projectId = msg.ProjectId.ToString("D"), items });
    }

    internal const int ArtefactListMax = 50;

    /// The longest prefix of `text` that fits in `max` UTF-8 bytes, never
    /// splitting a character in half. The cap is a byte cap because that is
    /// what the file and the page's memory cost.
    internal static string CutToBytes(string text, int max)
    {
        if (Encoding.UTF8.GetByteCount(text) <= max) return text;
        var cut = Math.Min(text.Length, max);
        while (cut > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, cut)) > max) cut--;
        // A lead surrogate left at the end has lost its pair: drop it.
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut];
    }

    /// The artefact rows in the ledger, oldest first. Read from the log rather
    /// than a second index file: the room's order IS the ledger, and one file
    /// can't then disagree with the other.
    private static List<RoomEntry> ArtefactRows(TeamStore store)
        => store.Ledger.ReadAll().Where(e => e.Kind == "artefact" && !string.IsNullOrEmpty(e.Target)).ToList();

    /// `perch team ask` from a bot: a card the owner answers.
    public void OnTeamAsk(Session sess, Guid paneId, TeamAskMessage msg)
    {
        if (BotOfSession(sess.Id) is not { } h) return;
        var text = (msg.Text ?? "").Trim();
        if (text.Length == 0) return;
        var id = string.IsNullOrWhiteSpace(msg.Id) ? Guid.NewGuid().ToString("N")[..8] : msg.Id!.Trim();
        _asks[id] = h.Bot.Slug;
        var choices = (msg.Choices ?? Array.Empty<string>()).Select(c => c.Trim()).Where(c => c.Length > 0).Take(6).ToList();
        var entry = h.Store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "ask", Note = id, To = new List<string> { h.Bot.Slug },
            Text = text, Choices = choices.Count > 0 ? choices : null,
        });
        Log.Info("Team.ask", $"bot={h.Bot.Slug} id={id}");
        RefreshRoster(h.Project, h.Store);
        PostEntries(h.Project.Id, h.Store, new[] { entry });
    }

    /// The owner answered an ask card: the answer goes to the asking bot as a
    /// post, and the card is marked answered.
    public void OnAskAnswer(TeamAskAnswerMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        var answer = (msg.Answer ?? "").Trim();
        if (proj == null || store == null || answer.Length == 0) return;
        if (!_asks.Remove(msg.Id, out var slug))
        {
            // Not in memory (a restart): find the card in the ledger.
            slug = store.Ledger.ReadAll().LastOrDefault(e => e.Event == "ask" && e.Note == msg.Id)?.To?.FirstOrDefault();
        }
        var bot = slug == null ? null : store.Doc.Bot(slug);
        if (bot == null) return;
        var done = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "ask.answered", Note = msg.Id, To = new List<string> { bot.Slug },
            Text = $"You answered {bot.Nickname}: {TeamRender.OneLine(answer, 120)}",
        });
        PostEntries(proj.Id, store, new[] { done });
        RefreshRoster(proj, store);
        OnPost(new TeamPostMsg
        {
            ProjectId = proj.Id, Text = answer, ClientId = "",
            To = JsonDocument.Parse($"[{JsonSerializer.Serialize(bot.Nickname)}]").RootElement.Clone(),
        });
    }

    /// A bot's PermissionRequest hook is holding a permission prompt for the
    /// room: a card with what it wants to run.
    public void OnPermAsk(Session sess, Guid paneId, PermAskMessage msg)
    {
        if (BotOfSession(sess.Id) is not { } h) return;
        var id = (msg.Id ?? "").Trim();
        if (id.Length == 0) return;
        _awaitingPerm[id] = (h.Bot.Slug, paneId);
        var tool = (msg.Tool ?? "tool").Trim();
        var summary = TeamRender.OneLine(msg.Summary, 300);
        var entry = h.Store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "permission", Note = id, To = new List<string> { h.Bot.Slug },
            Text = $"{h.Bot.Nickname} wants to run {tool}: {summary}",
            Summary = msg.Input,
            PaneId = paneId.ToString("D"),
        });
        Log.Info("Team.perm.ask", $"bot={h.Bot.Slug} id={id} tool={tool}");
        RefreshRoster(h.Project, h.Store);
        PostEntries(h.Project.Id, h.Store, new[] { entry });
        // The hook only waits so long. After that Claude shows the prompt in
        // the bot's own terminal, and a card still offering Allow would be
        // lying — pressing it then does nothing at all.
        _h.Delay(() => PermTimedOut(h.Project.Id, id), PermCardWait);
    }

    /// How long a permission card is answerable: the hook's own wait plus a
    /// moment, so the room never says "expired" while the bot is still
    /// listening.
    internal static readonly TimeSpan PermCardWait = TimeSpan.FromSeconds(575);

    /// Nobody answered in time. Say so, with the bot's terminal one click
    /// away, and stop the card offering buttons that can no longer work.
    private void PermTimedOut(Guid projectId, string id)
    {
        if (!_awaitingPerm.TryGetValue(id, out var who)) return;   // answered in time
        _awaitingPerm.Remove(id);
        var proj = _h.ProjectById(projectId);
        var store = StoreFor(projectId);
        if (proj == null || store == null) return;
        var bot = store.Doc.Bot(who.Slug);
        var e = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = who.Slug, Event = "permission.expired", Note = id,
            To = bot == null ? null : new List<string> { bot.Slug },
            PaneId = who.PaneId == Guid.Empty ? null : who.PaneId.ToString("D"),
            Text = $"{bot?.Nickname ?? "The bot"} waited ten minutes — the question is in its own terminal now",
        });
        Log.Info("Team.perm.timeout", $"id={id} bot={who.Slug}");
        RefreshRoster(proj, store);
        PostEntries(proj.Id, store, new[] { e });
    }

    /// The owner answered a permission card: write the decision where the
    /// hook is polling, and say so in the room.
    public void OnPermAnswer(TeamPermAnswerMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        if (proj == null || store == null) return;
        var allow = string.Equals(msg.Decision, "allow", StringComparison.OrdinalIgnoreCase);
        TeamPaths.Write(TeamPaths.PermAnswerPathFor(msg.Id), allow ? "allow" : "deny");
        _awaitingPerm.Remove(msg.Id, out var who);
        var bot = store.Doc.Bot(who.Slug ?? "");
        if (bot == null)
            bot = store.Doc.Bot(store.Ledger.ReadAll().LastOrDefault(e => e.Event == "permission" && e.Note == msg.Id)?.To?.FirstOrDefault() ?? "");
        var e = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "permission.answered", Note = msg.Id,
            To = bot == null ? null : new List<string> { bot.Slug },
            Text = allow ? $"You allowed {bot?.Nickname ?? "the bot"}" : $"You denied {bot?.Nickname ?? "the bot"}",
        });
        Log.Info("Team.perm.answer", $"id={msg.Id} allow={allow}");
        // The prompt was answered HERE, so no terminal dialog will ever be
        // shown or dismissed — nothing else clears the pane's "on a prompt"
        // state, and a post arriving meanwhile would sit parked for good.
        // Claude's own "permission prompt" notice can also arrive AFTER this
        // answer (hooks run side by side; the answer came in half a second),
        // so the pane remembers the answer and the window ignores a prompt
        // notice that follows it within a few seconds.
        if (who.PaneId != Guid.Empty)
        {
            _permAnswered[who.PaneId] = DateTimeOffset.UtcNow;
            _h.ClearPrompt?.Invoke(who.PaneId);
        }
        if (bot?.SessionId is Guid sid && _parked.ContainsKey(sid)) FlushParked(sid, TimeSpan.FromSeconds(2));
        RefreshRoster(proj, store);
        PostEntries(proj.Id, store, new[] { e });
    }

    /// Auto mode's classifier blocked a bot's tool call: information for the
    /// room, with the bot's terminal one click away.
    public void OnPermDenied(Session sess, Guid paneId, PermDeniedMessage msg)
    {
        if (BotOfSession(sess.Id) is not { } h) return;
        var tool = (msg.Tool ?? "tool").Trim();
        var e = h.Store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = h.Bot.Slug, Event = "denied", PaneId = paneId.ToString("D"),
            Text = $"{h.Bot.Nickname}: auto mode blocked {tool}: {TeamRender.OneLine(msg.Summary, 200)}"
                 + (string.IsNullOrWhiteSpace(msg.Reason) ? "" : $" — {TeamRender.OneLine(msg.Reason, 200)}"),
        });
        PostEntries(h.Project.Id, h.Store, new[] { e });
    }

    /// The room asking for a picture's bytes.
    public void OnImage(TeamImageMsg msg)
    {
        var path = (msg.Path ?? "").Trim();
        string? error = null;
        string mediaType = "", data = "";
        try
        {
            if (!IsImagePath(path)) error = "Not an image file.";
            else if (!File.Exists(path)) error = "The file is gone.";
            else
            {
                var info = new FileInfo(path);
                if (info.Length > 8 * 1024 * 1024) error = "The image is over 8 MB.";
                else
                {
                    data = Convert.ToBase64String(File.ReadAllBytes(path));
                    mediaType = Path.GetExtension(path).ToLowerInvariant() switch
                    {
                        ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", _ => "image/jpeg",
                    };
                }
            }
        }
        catch (Exception ex) { error = ex.Message; }
        if (error != null)
            _h.Post(new { type = "team.image.data", projectId = msg.ProjectId.ToString("D"), path, error });
        else
            _h.Post(new { type = "team.image.data", projectId = msg.ProjectId.ToString("D"), path, mediaType, data });
    }

    internal static bool IsImagePath(string path)
        => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path)
           && Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

    // ---- reactions ----------------------------------------------------------

    /// `perch team react` from a bot.
    public void OnTeamReact(Session sess, Guid paneId, TeamReactMessage msg)
    {
        if (BotOfSession(sess.Id) is not { } h) return;
        var target = ResolveReactionTarget(h.Store, msg.Target ?? "");
        if (target == null) { Fallback(h.Project, h.Store, $"{h.Bot.Nickname} reacted to something the room doesn't have ({TeamRender.OneLine(msg.Target, 40)})"); return; }
        React(h.Project, h.Store, h.Bot.Slug, target, msg.Emoji ?? "");
    }

    /// The owner reacting from the room. A bot whose row it was hears about it.
    public void OnReact(TeamReactMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        if (proj == null || store == null) return;
        var target = store.Ledger.ReadAll().FirstOrDefault(e => e.Seq == msg.Seq);
        if (target == null) return;
        if (!React(proj, store, "you", target, msg.Emoji ?? "")) return;
        var bot = store.Doc.Bot(target.From);
        if (bot == null) return;   // the owner's own row, or the app's
        var line = ReactionLine(msg.Seq, (msg.Emoji ?? "").Trim(), target.Text);
        var attempts = Attempt(new List<TeamBot> { bot }, line, everyone: false, seq: 0, raw: true);
        Log.Info("Team.react", $"seq={msg.Seq} bot={bot.Slug} delivered={attempts.All(a => a.Ok)}");
        var post = new RoomEntry
        {
            Kind = "system", From = "perch", Event = "delivered", Note = msg.Seq.ToString(),
            To = new List<string> { bot.Slug }, Text = $"Told {bot.Nickname} about your reaction",
            Delivered = attempts.All(a => a.Ok),
        };
        if (!attempts.All(a => a.Ok)) { store.Ledger.Append(post); Record(store, post, attempts); }
    }

    /// The line typed into a bot's terminal when the owner reacts to its row.
    internal static string ReactionLine(long seq, string emoji, string text)
    {
        var snippet = TeamRender.OneLine(text, 60);
        return $"{TeamRender.PostPrefix} Joseph reacted {emoji} to #{seq} \"{snippet}\"";
    }

    /// `#<seq>` → that row; `@<nick>` → that bot's latest beat/peer/note row.
    internal RoomEntry? ResolveReactionTarget(TeamStore store, string target)
    {
        var t = target.Trim();
        if (t.StartsWith('#') && long.TryParse(t[1..], out var seq))
            return store.Ledger.ReadAll().FirstOrDefault(e => e.Seq == seq);
        if (t.StartsWith('@'))
        {
            var name = t[1..];
            var bot = store.Doc.Bots.FirstOrDefault(b =>
                string.Equals(b.Nickname, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(b.Slug, name, StringComparison.OrdinalIgnoreCase)
                || ClaudePeerNames.Matches(b.CcName, name));
            if (bot == null) return null;
            return store.Ledger.ReadAll().LastOrDefault(e => e.From == bot.Slug && e.Kind is "beat" or "peer" or "note");
        }
        return null;
    }

    /// Append a reaction row unless the same one is already there. `from` is
    /// a bot slug or "you".
    private bool React(Project proj, TeamStore store, string from, RoomEntry target, string emoji)
    {
        emoji = emoji.Trim();
        if (emoji.Length == 0 || emoji.Length > 8) return false;
        var seq = target.Seq.ToString();
        if (store.Ledger.ReadAll().Any(e => e.Kind == "reaction" && e.From == from && e.Note == seq && e.Text == emoji))
            return false;   // the same reaction twice is one reaction
        var e = store.Ledger.Append(new RoomEntry { Kind = "reaction", From = from, Note = seq, Text = emoji });
        PostEntries(proj.Id, store, new[] { e });
        return true;
    }

    // ---- lifecycle --------------------------------------------------------

    /// The session-start hook fired in `sess`: a Claude is listening. Flush
    /// anything parked for it, after the same settle delay the pairing intro
    /// uses so the line lands in a painted input box.
    public void OnAgentUp(Session sess)
    {
        // Claude is listening now, so a pane we started for a post is no
        // longer "starting".
        _coldStart.Remove(sess.Id);
        FlushParked(sess.Id, TimeSpan.FromSeconds(4));
    }

    /// Panes Perch started because a post was addressed to them, and the
    /// moment it started them. Nothing is typed into one until its
    /// session-start hook fires — see Attempt.
    private readonly Dictionary<Guid, DateTimeOffset> _coldStart = new();

    /// How long a starting pane is given to report a Claude before Perch says
    /// so. Generous: a cold Claude on a big repository takes a while.
    internal static readonly TimeSpan ColdStartGrace = TimeSpan.FromSeconds(120);

    /// The pane never reported a Claude. Stop holding the post hostage: say so
    /// in the room, with the terminal one click away, and let the ordinary
    /// flush try anyway — the worst case is the line sits in a shell, which
    /// the submit check will then report.
    private void ColdStartOverdue(Guid sid)
    {
        if (!_coldStart.Remove(sid)) return;              // Claude came up in time
        if (BotOfSession(sid) is not { } h) return;
        if (!_parked.ContainsKey(sid)) return;            // nothing waiting after all
        Log.Info("Team.start.overdue", $"session={sid:N} bot={h.Bot.Slug}");
        var seq = _parked[sid].FirstOrDefault().Seq;
        Say(h, h.Bot, "undelivered", seq,
            $"{h.Bot.Nickname} hasn't finished starting, so this hasn't gone in yet — open its terminal to see why");
        FlushParked(sid, TimeSpan.FromSeconds(1));
    }

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
            if (isPostEcho && !w.Confirmed) _wrapping[sess.Id] = (w.Project, w.TaskId, true);
            else if (w.Confirmed && state == AgentState.Done) ResetBot(sess, w.Project, w.TaskId);
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
            // Still booting: the shell answers long before Claude does, and a
            // line typed into that gap is lost. OnAgentUp releases this.
            if (_coldStart.ContainsKey(sid)) return;
            var delivered = new List<RoomEntry>();
            foreach (var (seq, line, nick) in lines.ToList())
            {
                if (!_h.TypeToClaude(s, line)) break;
                lines.Remove((seq, line, nick));
                Expect(sid, h.Bot, seq, line);
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
    private void Expect(Guid sid, TeamBot bot, long seq, string line)
    {
        _submits[sid] = new PendingSubmit { Seq = seq, Bot = bot, Line = line };
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
                $"{nick} has a question open — the post goes in as soon as it is answered");
            return;
        }
        p.Tries++;
        if (p.Tries < SubmitChecks.Length)
        {
            if (p.Tries <= SubmitEnterTries)
            {
                var ok = _h.PressEnter(s);
                Log.Info("Team.submit", $"session={sid:N} seq={seq} enter-again={p.Tries} ok={ok}");
            }
            // Past the Enter retries this only waits: the line is in the pane,
            // and a busy bot confirms it when its turn reaches the queue.
            _h.Delay(() => CheckSubmitted(sid, seq), SubmitChecks[p.Tries]);
            return;
        }
        _submits.Remove(sid);
        if (Working(s))
        {
            // Still mid-turn after the whole budget. Not a failure — a long
            // tool run is exactly where a post waits longest.
            Log.Info("Team.submit", $"session={sid:N} seq={seq} unconfirmed, bot is working (queued)");
            return;
        }
        // The bot is idle and never took it: the line went somewhere it
        // shouldn't have — a shell that was still starting, a screen that
        // redrew over it. Type it once more before telling the owner it
        // failed; the whole point of the room is that a post reaches its bot.
        if (!p.Retried && _h.TypeToClaude(s, p.Line))
        {
            Log.Info("Team.submit", $"session={sid:N} seq={seq} typed again");
            _submits[sid] = new PendingSubmit { Seq = seq, Bot = p.Bot, Line = p.Line, Retried = true };
            _h.Delay(() => CheckSubmitted(sid, seq), SubmitChecks[0]);
            return;
        }
        Log.Info("Team.submit", $"session={sid:N} seq={seq} gave up");
        Say(h, p.Bot, "undelivered", seq,
            $"{nick} didn't take the post — open its terminal, or send it again from here");
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

    /// A bot's Claude painted a screen and went quiet before its session
    /// started: it is waiting on a question only a person can answer — for a
    /// bot in a fresh folder, "trust this folder?". Put the question in the
    /// room as a card the owner can answer, instead of a terminal they'd have
    /// to go find.
    public void OnPromptStuck(Session sess, Guid paneId)
    {
        if (BotOfSession(sess.Id) is not { } h) return;
        _awaitingTrust[h.Bot.Slug] = paneId;
        var e = h.Store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "trust", PaneId = paneId.ToString("D"),
            To = new List<string> { h.Bot.Slug },
            Text = $"{h.Bot.Nickname} is waiting on a question before it can start — usually \"trust this folder?\" for its new folder.",
        });
        Log.Info("Team.trust.ask", $"bot={h.Bot.Slug} pane={paneId:N}");
        RefreshRoster(h.Project, h.Store);
        PostEntries(h.Project.Id, h.Store, new[] { e });
    }

    /// The owner answered a bot's start-up question from the room. "trust"
    /// picks "Yes, I trust this folder" (Down, then Enter — the dialog's
    /// default is "No, exit"); anything else takes that default.
    public void OnBotAnswer(TeamBotAnswerMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        var bot = store?.Doc.Bot(msg.BotId);
        if (proj == null || store == null || bot == null) return;
        Guid paneId;
        if (!_awaitingTrust.TryGetValue(bot.Slug, out paneId))
        {
            var sess = bot.SessionId is Guid sid ? _h.SessionById(sid) : null;
            var pane = sess == null ? null : PaneTree.AllLeaves(sess.Root).FirstOrDefault(p => p.IsTerminal);
            if (pane == null) { Toast($"{bot.Nickname} isn't running."); return; }
            paneId = pane.Id;
        }
        var trust = string.Equals(msg.Answer, "trust", StringComparison.OrdinalIgnoreCase);
        try
        {
            _h.WriteRaw(paneId, trust ? new byte[] { 0x1b, (byte)'[', (byte)'B', (byte)'\r' } : new byte[] { (byte)'\r' });
        }
        catch (Exception ex) { Log.Info("Team.trust.answer", $"write failed: {ex.Message}"); }
        _awaitingTrust.Remove(bot.Slug);
        Log.Info("Team.trust.answer", $"bot={bot.Slug} pane={paneId:N} trust={trust}");
        var e = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = trust ? "trusted" : "exited",
            To = new List<string> { bot.Slug },
            Text = trust ? $"You trusted the folder for {bot.Nickname}" : $"You told {bot.Nickname} not to start",
        });
        RefreshRoster(proj, store);
        PostEntries(proj.Id, store, new[] { e });
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
        // Opening the room sweeps up any card an older build left pinned: a
        // confirmed card now leaves the board when it is confirmed, so a "done"
        // card still sitting there is finished by definition.
        if (msg.Open) MaybeArchive(msg.ProjectId);
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
        // A pasted picture is a post on its own; it must exist and be a picture.
        var image = (msg.Image ?? "").Trim();
        if (image.Length > 0 && !(File.Exists(image) && IsImagePath(image))) image = "";
        if (proj == null || store == null || (text.Length == 0 && image.Length == 0)) return;

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
            Image = image.Length > 0 ? image : null,
        };
        // Deliver FIRST, so the row lands with its verdict: "delivered" or
        // "waiting for the bot to wake" is a fact about this post, not a later
        // event the page has to reconcile. The row's number is known before
        // it exists (appends are single-threaded), so the typed line carries
        // it and a bot can react to `#<n>`.
        var seq = store.Ledger.NextSeq;
        var attempts = Attempt(targets, WithImage(text, image), everyone, seq);
        entry.Delivered = attempts.All(a => a.Ok);
        store.Ledger.Append(entry);
        var events = Record(store, entry, attempts);

        // The lead keeps the board. Work handed straight to a teammate that
        // no open task covers is copied to the lead, so a card appears without
        // anyone having to ask for one.
        var lead = store.Doc.Lead;
        if (lead != null && !everyone && !targets.Contains(lead)
            && !targets.All(t => store.Tasks.Active.Any(b => b.ItemOf(t.Slug) != null)))
        {
            var names = string.Join(", ", targets.Select(t => "@" + t.Nickname));
            var cc = $"{TeamRender.PostPrefix} #{seq} Joseph → {names} (cc {lead.Nickname} for the board): {Flatten(text)}";
            var ccAttempt = Attempt(new List<TeamBot> { lead }, cc, everyone: false, seq, raw: true);
            events.Add(store.Ledger.Append(new RoomEntry
            {
                Kind = "system", From = "perch", Event = "cc", Note = seq.ToString(), To = new List<string> { lead.Slug },
                Text = $"Copied to {lead.Nickname} for the board",
                Delivered = ccAttempt.All(a => a.Ok),
            }));
            var parkedEvents = Record(store, entry, ccAttempt);
            events.AddRange(parkedEvents);
        }
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
        TaskBoard? Named(string what)
        {
            var id = (msg.TaskId ?? "").Trim();
            if (id.Length == 0)
            {
                Refuse(h.Project, h.Store, $"{h.Bot.Nickname} tried to {what} without saying which task (the id is on the board)");
                return null;
            }
            var b = h.Store.Tasks.Board(id);
            if (b == null) Refuse(h.Project, h.Store, $"{h.Bot.Nickname} tried to {what} task {id}, which isn't on the board");
            return b;
        }
        switch (op)
        {
            case "new":
            case "main":
                if (!isLead) { Refuse(h.Project, h.Store, NotLead("open a task")); return; }
                if (title.Length == 0) { Refuse(h.Project, h.Store, $"{h.Bot.Nickname} opened a task with no title"); return; }
                var created = CreateTask(h.Project, h.Store, title, h.Bot.Slug);
                // The pipe runs one way; the CLI reads the new id from this file.
                TeamPaths.Write(TeamPaths.TaskReplyPathFor(paneId), created.Id);
                break;
            case "assign":
            {
                if (!isLead) { Refuse(h.Project, h.Store, NotLead("assign a piece")); return; }
                var board = Named("assign a piece on");
                if (board == null) return;
                var name = (msg.Bot ?? "").Trim();
                var target = h.Store.Doc.Bots.FirstOrDefault(b =>
                    string.Equals(b.Slug, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(b.Nickname, name, StringComparison.OrdinalIgnoreCase)
                    || ClaudePeerNames.Matches(b.CcName, name));
                if (target == null) { Refuse(h.Project, h.Store, $"{h.Bot.Nickname} assigned a piece to \"{name}\", who isn't on the team"); return; }
                var hasStatus = TaskItem.Statuses.Contains((msg.Status ?? "").Trim().ToLowerInvariant());
                if (title.Length == 0 && !hasStatus)
                { Refuse(h.Project, h.Store, $"{h.Bot.Nickname} assigned {target.Nickname} an empty piece"); return; }
                // --status and --note on an assign are honoured: the lead has
                // to be able to close (or unblock) a teammate's piece, or an
                // abandoned piece keeps the card alive forever.
                UpsertItem(h.Project, h.Store, board, target, title, msg.Status, msg.Note, h.Bot);
                break;
            }
            case "mine":
            {
                TaskBoard? board;
                if (!string.IsNullOrWhiteSpace(msg.TaskId)) { board = Named("report a piece on"); if (board == null) return; }
                else
                {
                    // No id: the bot's one open piece, or the one open task.
                    var mine = h.Store.Tasks.Active.Where(b => b.ItemOf(h.Bot.Slug) != null).ToList();
                    var active = h.Store.Tasks.Active.ToList();
                    board = mine.Count == 1 ? mine[0] : mine.Count == 0 && active.Count == 1 ? active[0] : null;
                    if (board == null)
                    {
                        Refuse(h.Project, h.Store, active.Count == 0
                            ? $"{h.Bot.Nickname} reported a piece, but there is no task open — the lead opens one first"
                            : $"{h.Bot.Nickname} reported a piece without saying which task (say `perch team task mine <id> …`)");
                        return;
                    }
                }
                UpsertItem(h.Project, h.Store, board, h.Bot, title, msg.Status, msg.Note, h.Bot);
                break;
            }
            case "done":
            {
                if (!isLead) { Refuse(h.Project, h.Store, NotLead("close a task")); return; }
                var board = Named("close");
                if (board == null) return;
                AskConfirm(h.Project, h.Store, board, h.Bot);
                break;
            }
            default:
                Log.Info("Team.task", $"unknown op '{op}' from {h.Bot.Slug}");
                break;
        }
    }

    /// The owner opens a task from the room.
    public void OnTaskSet(TeamTaskSetMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        var title = (msg.Title ?? "").Trim();
        if (proj == null || store == null || title.Length == 0) return;
        CreateTask(proj, store, title, "you");
    }

    /// The owner renames an open task.
    public void OnTaskRename(TeamTaskRenameMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        var title = (msg.Title ?? "").Trim();
        var board = store?.Tasks.Board(msg.TaskId);
        if (proj == null || store == null || board == null || title.Length == 0 || board.Status == "done") return;
        board.Title = title;
        store.SaveTasks();
        RefreshRoster(proj, store);
        var e = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "task", TaskId = board.Id, Text = $"Joseph renamed the task: {title}",
        });
        _h.PushState();
        PostEntries(proj.Id, store, new[] { e });
    }

    /// The owner confirms: the task is done, its bots wrap up and reset.
    public void OnTaskConfirm(TeamTaskConfirmMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        var board = store?.Tasks.Board(msg.TaskId);
        if (proj == null || store == null || board == null) return;
        CompleteTask(proj, store, board);
    }

    /// The owner says not yet: the board goes back to open and the lead
    /// hears why, as an ordinary post from the owner.
    public void OnTaskReject(TeamTaskRejectMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        var board = store?.Tasks.Board(msg.TaskId);
        if (proj == null || store == null || board == null || board.Status != "review") return;
        board.Status = "open";
        board.ReviewBy = null;
        board.ReviewAtMs = null;
        store.SaveTasks();
        RefreshRoster(proj, store);
        var note = (msg.Note ?? "").Trim();
        var e = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "task", TaskId = board.Id,
            Text = $"Not done yet — \"{TeamRender.OneLine(board.Title, 60)}\" is open again",
        });
        _h.PushState();
        PostEntries(proj.Id, store, new[] { e });
        var lead = store.Doc.Lead;
        var text = note.Length > 0 ? $"Not done yet (task {board.Id}): {note}" : $"Not done yet (task {board.Id}) — keep going and ask again when it is.";
        OnPost(new TeamPostMsg
        {
            ProjectId = proj.Id, Text = text, ClientId = "",   // no optimistic row to reconcile: the owner didn't type this
            To = lead == null ? null : JsonDocument.Parse($"[{JsonSerializer.Serialize(lead.Nickname)}]").RootElement.Clone(),
        });
    }

    private TaskBoard CreateTask(Project proj, TeamStore store, string title, string setBy)
    {
        var who = setBy == "you" ? "Joseph" : store.Doc.Bot(setBy)?.Nickname ?? setBy;
        var board = new TaskBoard
        {
            Id = TaskDoc.NewId(), Title = title, SetBy = setBy,
            CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        store.Tasks.Open.Add(board);
        store.SaveTasks();
        RefreshRoster(proj, store);
        var row = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "task", TaskId = board.Id, Text = $"Task opened by {who}: {title}",
        });
        Log.Info("Team.task", $"project={proj.Id:N} new id={board.Id} by={setBy} title={TeamRender.OneLine(title, 80)}");
        _h.PushState();
        PostEntries(proj.Id, store, new[] { row });
        return board;
    }

    private void UpsertItem(Project proj, TeamStore store, TaskBoard board, TeamBot bot, string title, string? status, string? note, TeamBot by)
    {
        if (board.Status == "done")
        {
            Refuse(proj, store, $"{by.Nickname} reported a piece on task {board.Id}, but it is done");
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
        var e = store.Ledger.Append(new RoomEntry { Kind = "system", From = "perch", Event = "task", TaskId = board.Id, Text = text });
        _h.PushState();
        PostEntries(proj.Id, store, new[] { e });
    }

    private void AskConfirm(Project proj, TeamStore store, TaskBoard board, TeamBot lead)
    {
        // Closing a card that is already done is how a stuck card gets cleared:
        // do it, don't argue. (It used to refuse, which left no way to take a
        // pinned card off the board from a bot.)
        if (board.Status == "done") { CloseCard(proj, store, board, $"{lead.Nickname} cleared"); return; }
        if (board.Status == "review")
        {
            Refuse(proj, store, $"{lead.Nickname} closed task {board.Id}, which is already waiting for Joseph to confirm");
            return;
        }
        board.Status = "review";
        board.ReviewBy = lead.Slug;
        board.ReviewAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.SaveTasks();
        RefreshRoster(proj, store);
        var e = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "task.review", TaskId = board.Id,
            Text = $"{lead.Nickname} says \"{TeamRender.OneLine(board.Title, 60)}\" is done — confirm it on the card, or say not yet",
        });
        Log.Info("Team.task", $"project={proj.Id:N} review id={board.Id} by={lead.Slug}");
        _h.PushState();
        PostEntries(proj.Id, store, new[] { e });
    }

    /// The owner confirmed. The board is done; the running bots whose open
    /// pieces are ALL on it are told to wrap up (memory first, then one line)
    /// and each is reset when that turn ends. A bot with a piece on another
    /// open task is told and carries on; bots that aren't running have
    /// nothing to clear.
    private void CompleteTask(Project proj, TeamStore store, TaskBoard board)
    {
        if (board.Status == "done") return;
        board.Status = "done";
        board.DoneAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.SaveTasks();
        RefreshRoster(proj, store);
        var rows = new List<RoomEntry>
        {
            store.Ledger.Append(new RoomEntry
            {
                Kind = "system", From = "perch", Event = "task.done", TaskId = board.Id,
                Text = $"Task done: {board.Title} — its bots are wrapping up",
            }),
        };
        var others = store.Tasks.Active.Where(b => b.Id != board.Id).ToList();
        bool Elsewhere(TeamBot b) => others.Any(o => o.ItemOf(b.Slug) != null);
        var resetting = store.Doc.Bots.Where(b => !Elsewhere(b)).ToList();
        var staying = store.Doc.Bots.Where(Elsewhere).ToList();

        if (resetting.Count > 0)
        {
            var text = $"The task \"{board.Title}\" is done. Wrap up now: update your memory file with what the next task will need " +
                       "(decisions, where things stand, unfinished threads), then reply with one line. Your context is cleared after that reply.";
            var seq = store.Ledger.NextSeq;
            var attempts = Attempt(resetting, text, everyone: true, seq);
            var post = new RoomEntry
            {
                Kind = "user", From = "you", Text = text, To = new List<string> { TeamRender.Everyone },
                Delivered = attempts.All(a => a.Ok), TaskId = board.Id,
            };
            store.Ledger.Append(post);
            rows.Add(post);
            rows.AddRange(Record(store, post, attempts));
            foreach (var a in attempts)
                if (a.Ok && a.SessionId is Guid sid) _wrapping[sid] = (proj.Id, board.Id, false);
        }
        if (staying.Count > 0)
        {
            var text = $"The task \"{board.Title}\" is done. You still have a piece on another open task, so you carry on; " +
                       "update your memory file with anything from this one worth keeping.";
            var seq = store.Ledger.NextSeq;
            var attempts = Attempt(staying, text, everyone: false, seq);
            var post = new RoomEntry
            {
                Kind = "user", From = "you", Text = text, To = staying.Select(b => b.Slug).ToList(),
                Delivered = attempts.All(a => a.Ok), TaskId = board.Id,
            };
            store.Ledger.Append(post);
            rows.Add(post);
            rows.AddRange(Record(store, post, attempts));
        }
        // The card leaves the board NOW. Wrapping up is the bots' business and
        // takes as long as it takes; it used to hold the card in place, so one
        // bot that never finished its wrap-up (or picked up a piece on a new
        // task first) pinned a confirmed card on the board for good.
        Archive(store, board);
        store.Tasks.Open.Remove(board);
        store.SaveTasks();
        RefreshRoster(proj, store);
        rows.Add(store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "task", TaskId = board.Id,
            Text = $"\"{TeamRender.OneLine(board.Title, 60)}\" is off the board"
                 + (store.Tasks.Active.Any() ? "" : " — ready for the next task"),
        }));
        Log.Info("Team.task", $"project={proj.Id:N} done id={board.Id}; wrapping={_wrapping.Count(kv => kv.Value.TaskId == board.Id)} staying={staying.Count}");
        _h.PushState();
        PostEntries(proj.Id, store, rows);
    }

    /// Take a card off the board with no ceremony: no confirmation, no
    /// wrap-up, no reset. The owner's "remove this card" and a bot closing a
    /// card that is already done both land here.
    private void CloseCard(Project proj, TeamStore store, TaskBoard board, string who)
    {
        Archive(store, board);
        store.Tasks.Open.Remove(board);
        foreach (var sid in _wrapping.Where(kv => kv.Value.Project == proj.Id && kv.Value.TaskId == board.Id)
                                     .Select(kv => kv.Key).ToList())
            _wrapping.Remove(sid);
        store.SaveTasks();
        RefreshRoster(proj, store);
        var e = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = "perch", Event = "task", TaskId = board.Id,
            Text = $"{who} \"{TeamRender.OneLine(board.Title, 60)}\" off the board",
        });
        Log.Info("Team.task", $"project={proj.Id:N} closed id={board.Id} by={who}");
        _h.PushState();
        PostEntries(proj.Id, store, new[] { e });
    }

    /// "Send again" on a post that never landed: type the same line into that
    /// bot again, from the room, without making a second post out of it.
    public void OnDeliverRetry(TeamDeliverRetryMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        if (proj == null || store == null) return;
        var bot = store.Doc.Bot(msg.BotId ?? "");
        var sess = bot?.SessionId is Guid id ? _h.SessionById(id) : null;
        var post = store.Ledger.ReadAll().LastOrDefault(e => e.Seq == msg.Seq && e.Kind == "user");
        if (bot == null || sess == null || post == null)
        {
            Log.Info("Team.retry", $"seq={msg.Seq} bot={msg.BotId}: nothing to send again");
            return;
        }
        if (sess.Dormant) _h.Wake(sess);
        else if (!SessionRunning(sess)) _h.EnsureRunning?.Invoke(sess);
        var everyone = post.To == null || post.To.Contains(TeamRender.Everyone);
        var line = DeliveryLine(post.Text, everyone ? null : bot.Nickname, post.Seq);
        var ok = !sess.Dormant && !Blocked(sess) && _h.TypeToClaude(sess, line);
        Log.Info("Team.retry", $"seq={msg.Seq} bot={bot.Slug} ok={ok}");
        if (ok) Expect(sess.Id, bot, post.Seq, line);
        else
        {
            // Not ready yet: park it, and the ordinary flush delivers it the
            // moment the pane can take it.
            if (!_parked.TryGetValue(sess.Id, out var list)) _parked[sess.Id] = list = new();
            list.Add((post.Seq, line, bot.Nickname));
        }
        var e = store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = bot.Slug, Event = ok ? "delivered" : "waiting", Note = post.Seq.ToString(),
            Text = ok ? $"Sent to {bot.Nickname} again" : $"{bot.Nickname} isn't ready yet — this goes in as soon as it is",
        });
        PostEntries(proj.Id, store, new[] { e });
    }

    /// The owner takes a card off the board by hand — the escape hatch for a
    /// card nobody is going to finish, or one that is done in everything but
    /// name. Nothing is asked of the bots.
    public void OnTaskClose(TeamTaskCloseMsg msg)
    {
        var proj = _h.ProjectById(msg.ProjectId);
        var store = StoreFor(msg.ProjectId);
        var board = store?.Tasks.Board(msg.TaskId);
        if (proj == null || store == null || board == null) return;
        CloseCard(proj, store, board, "Joseph took");
    }

    /// A wrapping bot's turn ended after the wrap-up post went in: clear its
    /// context. `/clear` re-fires the session-start hook, so the brief is
    /// re-applied and the next prompt carries the roster and its memory.
    private void ResetBot(Session sess, Guid projectId, string taskId)
    {
        _wrapping.Remove(sess.Id);
        if (BotOfSession(sess.Id) is not { } h) { MaybeArchive(projectId, taskId); return; }
        var ok = _h.TypeToClaude(sess, "/clear");
        Log.Info("Team.reset", $"session={sess.Id:N} bot={h.Bot.Slug} ok={ok}");
        var e = h.Store.Ledger.Append(new RoomEntry
        {
            Kind = "system", From = h.Bot.Slug, Event = "reset", TaskId = taskId,
            Text = ok ? $"{h.Bot.Nickname} reset for the next task"
                      : $"{h.Bot.Nickname} couldn't be reset — open its terminal and run /clear",
        });
        if (!ok) e.Event = "undelivered";
        PostEntries(h.Project.Id, h.Store, new[] { e });
        MaybeArchive(projectId, taskId);
    }

    /// Once nobody is left wrapping for a done board, it moves to the archive.
    /// With no task named, every done board is checked.
    private void MaybeArchive(Guid projectId, string? taskId = null)
    {
        var proj = _h.ProjectById(projectId);
        var store = StoreFor(projectId);
        if (proj == null || store == null) return;
        var done = store.Tasks.Open.Where(b => b.Status == "done" && (taskId == null || b.Id == taskId)).ToList();
        var rows = new List<RoomEntry>();
        foreach (var board in done)
        {
            // No wrapping gate: a confirmed card leaves the board when it is
            // confirmed (CompleteTask). This is now only the sweep for a card
            // an older build left pinned.
            Archive(store, board);
            store.Tasks.Open.Remove(board);
            rows.Add(store.Ledger.Append(new RoomEntry
            {
                Kind = "system", From = "perch", Event = "task", TaskId = board.Id,
                Text = $"\"{TeamRender.OneLine(board.Title, 60)}\" is wrapped up" + (store.Tasks.Active.Any() ? "" : " — ready for the next task"),
            }));
        }
        if (rows.Count == 0) return;
        store.SaveTasks();
        RefreshRoster(proj, store);
        _h.PushState();
        PostEntries(proj.Id, store, rows);
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
    private List<Attempted> Attempt(List<TeamBot> targets, string text, bool everyone, long seq = 0, bool raw = false)
    {
        var results = new List<Attempted>();
        foreach (var bot in targets)
        {
            var line = raw ? text : DeliveryLine(text, everyone ? null : bot.Nickname, seq);
            var sess = bot.SessionId is Guid id ? _h.SessionById(id) : null;
            if (sess == null) { results.Add(new Attempted(bot, false, true, false, line, null)); continue; }
            var starting = false;
            if (sess.Dormant) { _h.Wake(sess); starting = true; }
            // A tab restored after a restart but never looked at has no
            // terminal yet — a post to it must start it, or it sits parked
            // until the owner happens to click the tab.
            else if (!SessionRunning(sess))
            {
                Log.Info("Team.start.needed", $"session={sess.Id:N} bot={bot.Slug}");
                _h.EnsureRunning?.Invoke(sess);
                starting = true;
            }
            // A pane that is only just starting is NOT ready for a line: the
            // shell is up long before Claude is, and typing into that gap put
            // the post into a shell prompt where it sat unread. It waits for
            // the session-start hook (OnAgentUp) instead.
            if (starting)
            {
                _coldStart[sess.Id] = DateTimeOffset.UtcNow;
                var waking = sess.Id;
                _h.Delay(() => ColdStartOverdue(waking), ColdStartGrace);
            }
            var blocked = !sess.Dormant && Blocked(sess);
            var ok = !starting && !sess.Dormant && !blocked && _h.TypeToClaude(sess, line);
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
                Expect(a.SessionId!.Value, a.Bot, post.Seq, a.Line);
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
                        Text = $"{a.Bot.Nickname} has a question open — this goes in as soon as it is answered",
                    }));
            }
        }
        return events;
    }

    /// The line typed into a bot's terminal for one of the owner's posts. The
    /// post's room number rides along (`#12`) so the bot can react to it.
    /// The text a bot is typed when the post carries a picture: the path is
    /// named so the bot can Read the file when it needs to see it, and the
    /// wording says so — a bot should not open every picture it is sent.
    internal static string WithImage(string text, string image)
        => image.Length == 0 ? text
         : (text.Length == 0 ? "" : text + " ") + $"(attached picture: {image} — Read it if you need to see it)";

    internal static string DeliveryLine(string text, string? nickname, long seq = 0)
    {
        var who = nickname == null ? "@everyone" : "@" + nickname;
        var num = seq > 0 ? $" #{seq}" : "";
        return $"{TeamRender.PostPrefix}{num} Joseph → {who}: {Flatten(text)}";
    }

    /// A post's text as one typed line (or a bracketed paste, when enabled).
    internal static string Flatten(string text)
    {
        var body = text.Replace("\r\n", "\n").Trim();
        if (body.Contains('\n'))
        {
            body = UseBracketedPaste
                ? "\x1b[200~" + body + "\x1b[201~"
                : body.Replace("\n", " ⏎ ");
        }
        return body;
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
        => store.RenderRoster(proj.Name, Presence(store), ModelLimitsLine());

    /// Does any terminal pane of the session have a live PTY? A session whose
    /// tab was restored but never viewed has none — its Claude isn't up.
    private bool SessionRunning(Session sess)
        => _h.HasPty == null || PaneTree.AllLeaves(sess.Root).Any(p => p.IsTerminal && _h.HasPty(p.Id));

    private Dictionary<string, string> Presence(TeamStore store)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bot in store.Doc.Bots)
        {
            var sess = bot.SessionId is Guid id ? _h.SessionById(id) : null;
            if (sess == null) { map[bot.Slug] = "not running"; continue; }
            if (sess.Dormant) { map[bot.Slug] = "asleep"; continue; }
            if (!SessionRunning(sess)) { map[bot.Slug] = "not started"; continue; }
            if (_awaitingTrust.ContainsKey(bot.Slug)) { map[bot.Slug] = "waiting for the owner to answer its start-up question"; continue; }
            if (_awaitingPerm.Values.Any(v => string.Equals(v.Slug, bot.Slug, StringComparison.OrdinalIgnoreCase)))
            { map[bot.Slug] = "waiting for your permission"; continue; }
            if (_asks.Values.Any(s => string.Equals(s, bot.Slug, StringComparison.OrdinalIgnoreCase)))
            { map[bot.Slug] = "waiting for your answer"; continue; }
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
        Put("image", e.Image);
        Put("taskId", e.TaskId);
        Put("choices", e.Choices);
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
                lead = store.Doc.LeadSlug,
            },
            tasks = TasksView(projectId, store),
            ledger = store.Ledger.Tail(50).Select(e => EntryView(store, e)),
            parked = _parked.Sum(kv => kv.Value.Count),
            awaitingPerm = _awaitingPerm.Keys.ToArray(),
        });
    }
}
