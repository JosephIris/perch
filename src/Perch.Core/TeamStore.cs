using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Perch;

/// Reads and writes a project's team folder. File mechanics only — deciding
/// WHEN to render, deliver, or relaunch lives in TeamController.
///
/// ## Layout
///
///   &lt;repo&gt;\.perch\team\                SHARED — tracked in git, so a pull
///     team.json                          brings the team to every machine
///     positions\&lt;slug&gt;\brief.md          the position's standing brief, hand-editable
///     bots\&lt;slug&gt;\memory.md              the bot's own notes; it edits them, they travel
///     local\                             LOCAL — this machine only, git-ignored
///       sessions.json                    which tab each bot runs in here
///       room.jsonl                       the room ledger (RoomLedger)
///       roster.md                        rendered, with this machine's presence
///       bots\&lt;slug&gt;\system.md            rendered; appended to the bot's system prompt at launch
///       bots\&lt;slug&gt;\context.md           rendered; roster + memory, inlined into every prompt
///
/// Rooted at the MAIN checkout, never a worktree: every bot of a project, in
/// whichever worktree it runs, is told absolute paths into this one folder.
///
/// ## Shared versus local
///
/// The team is meant to travel with the repository: positions, briefs, bots
/// and their faces, and each bot's memory are what make the team, and a
/// teammate (or the owner on another PC) who pulls should get them. What
/// cannot travel is which Perch tab a bot runs in — a session id means
/// nothing on another machine — and the room's chat, which is the
/// conversation with the bots running HERE. Those live under `local/`.
///
/// `.perch/.gitignore` is Perch's own file (BoardStore writes "*" so boards
/// and screenshots never get committed). When a team is created or opened,
/// that file is rewritten to keep ignoring boards but track `team/` minus
/// `team/local/` (EnsureShareable). A .gitignore the user removed or wrote
/// themselves is left alone.
///
/// ## Refusing to save over something we could not read
///
/// Same rule as BoardStore: a team.json that exists but does not parse (or
/// was written by a newer Perch) makes the store Readable == false, and Save
/// becomes a no-op. Overwriting would destroy the roster behind every running
/// bot; surfacing the problem is the safe failure.
internal sealed class TeamStore
{
    public string RepoRoot { get; }
    public string Dir { get; }
    public string LocalDir => Path.Combine(Dir, "local");
    public bool Readable { get; private set; } = true;
    public string Problem { get; private set; } = "";
    public TeamDoc Doc { get; private set; } = new();

    public string JsonPath => Path.Combine(Dir, "team.json");
    public string TasksPath => Path.Combine(Dir, "tasks.json");
    public string LocalJsonPath => Path.Combine(LocalDir, "sessions.json");
    public string RosterPath => Path.Combine(LocalDir, "roster.md");
    public string LedgerPath => Path.Combine(LocalDir, "room.jsonl");
    public string PositionsDir => Path.Combine(Dir, "positions");
    public string BotsDir => Path.Combine(Dir, "bots");
    /// Where a bot's long piece of work is kept: a draft ticket, a table, a
    /// plan. Local, like the room it is shown in — an artefact is part of the
    /// conversation with the bots running HERE, not something a teammate
    /// pulling the repository should get.
    public string ArtefactsDir => Path.Combine(LocalDir, "artefacts");
    public string ArtefactPathFor(string id, string ext) => Path.Combine(ArtefactsDir, $"{id}.{ext}");
    public string BriefPathFor(string positionSlug) => Path.Combine(PositionsDir, positionSlug, "brief.md");
    public string MemoryPathFor(string botSlug) => Path.Combine(BotsDir, botSlug, "memory.md");
    /// Facts every bot needs (which table a product reads, an environment
    /// quirk, a rule from the owner): one file, shared through the repository,
    /// inlined into every bot's prompt. Any bot adds a line (`perch team
    /// learn`); the lead prunes by editing the file.
    public string KnowledgePath => Path.Combine(Dir, "knowledge.md");
    /// Procedures any bot follows: one folder per skill with a SKILL.md, shared
    /// like memory. Listed (name, summary, path) in every prompt; a bot Reads
    /// the file when a task matches.
    public string SkillsDir => Path.Combine(Dir, "skills");
    public string SkillPathFor(string slug) => Path.Combine(SkillsDir, slug, "SKILL.md");
    /// The system prompt a bot's run is started with; local, one per run.
    public string RunPromptPathFor(string botSlug, string runId) => Path.Combine(LocalDir, "bots", botSlug, $"run-{runId}.md");
    public string SystemPathFor(string botSlug) => Path.Combine(LocalDir, "bots", botSlug, "system.md");
    public string ContextPathFor(string botSlug) => Path.Combine(LocalDir, "bots", botSlug, "context.md");

    /// Largest memory a bot gets back per prompt. The roster is ~1.5 KB for a
    /// five-bot team and the hook inlines at most 6 KB, so this leaves room.
    public const int MemoryMaxBytes = 2048;

    private RoomLedger? _ledger;
    public RoomLedger Ledger => _ledger ??= new RoomLedger(LedgerPath);
    private TeamOutbox? _outbox;
    public TeamOutbox Outbox => _outbox ??= new TeamOutbox(Path.Combine(LocalDir, "outbox.json"));

    /// The task board (tasks.json beside team.json; shared). Read on first
    /// use and on Reload; a missing or unreadable file is an empty board —
    /// never fatal, the team file is the one that matters.
    ///
    /// The file is the source of truth, not this copy: a hand edit (or a pull,
    /// or another Perch) is picked up on the next read, checked at most once a
    /// second so the hot paths that walk the board stay a memory read. Without
    /// that, Perch wrote its stale copy back over an edit within the minute and
    /// a card the owner had deleted by hand came straight back.
    private TaskDoc? _tasks;
    private DateTime _tasksStamp;
    private long _tasksCheckedAt;
    private string? _tasksSource;
    public bool TasksReadable { get; private set; } = true;

    public TaskDoc Tasks
    {
        get
        {
            if (_tasks != null && !TasksChangedOnDisk()) return _tasks;
            return _tasks = LoadTasks();
        }
    }

    internal const int TasksDiskCheckMs = 1000;

    private bool TasksChangedOnDisk()
    {
        var now = Environment.TickCount64;
        if (now - _tasksCheckedAt < TasksDiskCheckMs) return false;
        _tasksCheckedAt = now;
        try
        {
            if (!File.Exists(TasksPath)) return _tasksStamp != default;
            return File.GetLastWriteTimeUtc(TasksPath) != _tasksStamp;
        }
        catch { return false; }
    }

    private void StampTasks()
    {
        try { _tasksStamp = File.Exists(TasksPath) ? File.GetLastWriteTimeUtc(TasksPath) : default; }
        catch { /* a stamp we can't read just means the next read re-reads */ }
        _tasksCheckedAt = Environment.TickCount64;
    }

    private TaskDoc LoadTasks()
    {
        try
        {
            StampTasks();
            _tasksSource = File.Exists(TasksPath) ? File.ReadAllText(TasksPath) : null;
            TasksReadable = true;
            if (_tasksSource == null) return new TaskDoc();
            var doc = JsonSerializer.Deserialize(_tasksSource, TaskJsonContext.Default.TaskDoc);
            if (doc == null) throw new JsonException("Task document is null.");
            doc.Done ??= new();
            doc.Open ??= new();
            doc.Migrate();   // a v1 file's single `current` becomes the first open board
            foreach (var b in doc.Open) { b.Items ??= new(); b.WrappedBy ??= new(); }
            foreach (var b in doc.Done) { b.Items ??= new(); b.WrappedBy ??= new(); }
            return doc;
        }
        catch (Exception ex)
        {
            TasksReadable = false;
            Log.Error("TeamStore.ReadTasks", ex);
            return new TaskDoc();
        }
    }

    public void SaveTasks()
    {
        if (!Readable) return;
        try
        {
            var doc = _tasks ?? Tasks;
            if (!TasksReadable) return;
            // Check on EVERY write, including inside the read-side debounce.
            // Never replace an outside edit with our previously loaded board.
            var current = File.Exists(TasksPath) ? File.ReadAllText(TasksPath) : null;
            if (current != _tasksSource)
            {
                _tasks = LoadTasks();
                Log.Info("TeamStore.SaveTasks", "Task board changed externally; stale save refused.");
                return;
            }
            while (doc.Done.Count > TaskDoc.DoneKept) doc.Done.RemoveAt(0);
            var json = JsonSerializer.Serialize(doc, TaskJsonContext.Default.TaskDoc);
            AtomicFile.WriteAllText(TasksPath, json);
            _tasksSource = json;
            StampTasks();   // our own write is not an outside edit
        }
        catch (Exception ex) { Log.Error("TeamStore.SaveTasks", ex); }
    }

    /// team.json as last read, to notice a pull or a hand edit (StaleOnDisk).
    private DateTime _jsonStamp;

    /// Source of a new bot's random look. Swappable so tests can pin one.
    internal static Func<Random> Rng = () => new Random();

    private TeamStore(string repoRoot)
    {
        RepoRoot = repoRoot;
        Dir = DirFor(repoRoot);
    }

    public static string DirFor(string repoRoot) => Path.Combine(repoRoot, ".perch", "team");

    // ---- open / create ----------------------------------------------------

    /// Open the team of `repoRoot`, or null when the project has no team
    /// folder yet. "No team" and "empty team" are different states to the UI
    /// (no room versus a room with nobody in it), so they are different here.
    public static TeamStore? Open(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot)) return null;
        var dir = DirFor(repoRoot);
        if (!Directory.Exists(dir)) return null;
        var store = new TeamStore(repoRoot);
        store.Load();
        store.MigrateLayout();
        EnsureShareable(repoRoot);
        return store;
    }

    /// Create the team folder (idempotent) and return the store.
    public static TeamStore Create(string repoRoot)
    {
        var existing = Open(repoRoot);
        if (existing != null) return existing;
        var store = new TeamStore(repoRoot);
        Directory.CreateDirectory(store.Dir);
        Directory.CreateDirectory(store.LocalDir);
        BoardStore.EnsureGitIgnored(repoRoot);
        EnsureShareable(repoRoot);
        store.Save();
        return store;
    }

    /// True when team.json changed on disk since it was read — a pull, a
    /// sync, a hand edit. The controller then reloads instead of trusting
    /// its copy.
    public bool StaleOnDisk()
    {
        try
        {
            var now = File.Exists(JsonPath) ? File.GetLastWriteTimeUtc(JsonPath) : DateTime.MinValue;
            return now != _jsonStamp;
        }
        catch { return false; }
    }

    /// Re-read both files (and the task board). Session ids come from the
    /// local file, so a bot another machine added shows up "not running" and
    /// one running here keeps its tab.
    public void Reload() { Load(); _tasks = null; }

    private void Load()
    {
        Readable = true;
        Problem = "";
        if (!File.Exists(JsonPath))
        {
            Doc = new TeamDoc();     // a folder with no index yet is empty, not broken
            _jsonStamp = DateTime.MinValue;
            return;
        }
        string text;
        try
        {
            text = File.ReadAllText(JsonPath);
            _jsonStamp = File.GetLastWriteTimeUtc(JsonPath);
        }
        catch (Exception ex)
        {
            Log.Error("TeamStore.Read", ex);
            Fail("The team file could not be read.");
            return;
        }
        TeamDoc doc;
        try
        {
            var parsed = JsonSerializer.Deserialize(text, TeamJsonContext.Default.TeamDoc);
            if (parsed == null) { Fail("The team file could not be parsed."); return; }
            if (parsed.V > 1) { Fail($"This team was written by a newer version of Perch (v{parsed.V})."); return; }
            parsed.Positions ??= new();
            parsed.Bots ??= new();
            doc = parsed;
        }
        catch (Exception ex)
        {
            Log.Error("TeamStore.Parse", ex);
            Fail("The team file could not be parsed.");
            return;
        }

        // Faces: documents from before them get a hat per position and a
        // look per bot, saved back so every machine agrees from now on.
        var dirty = false;
        foreach (var pos in doc.Positions)
        {
            var hat = TeamLooks.NormalizeHat(pos.Hat, pos.Name);
            if (hat != pos.Hat) { pos.Hat = hat; dirty = true; }
        }
        foreach (var bot in doc.Bots)
        {
            if (bot.Look == null) { bot.Look = TeamLooks.RandomLook(Rng()); dirty = true; }
            else
            {
                var n = TeamLooks.Normalize(bot.Look);
                if (n.Eyewear != bot.Look.Eyewear || n.Extra != bot.Look.Extra || n.Temper != bot.Look.Temper)
                { bot.Look = n; dirty = true; }
            }
        }

        // Session ids: the local file wins; a pre-split document's inline id
        // is taken once and never written to the shared file again.
        var local = LoadLocal();
        foreach (var bot in doc.Bots)
        {
            if (local.Sessions.TryGetValue(bot.Slug, out var sid)) bot.SessionId = sid;
            else if (bot.LegacySessionId is Guid legacy) { bot.SessionId = legacy; dirty = true; }
            if (bot.LegacySessionId != null) { bot.LegacySessionId = null; dirty = true; }
        }
        Doc = doc;
        if (dirty) Save();
    }

    private TeamLocalDoc LoadLocal()
    {
        try
        {
            if (!File.Exists(LocalJsonPath)) return new TeamLocalDoc();
            var l = JsonSerializer.Deserialize(File.ReadAllText(LocalJsonPath), TeamJsonContext.Default.TeamLocalDoc);
            if (l == null) return new TeamLocalDoc();
            l.Sessions ??= new();
            return l;
        }
        catch (Exception ex)
        {
            Log.Error("TeamStore.ReadLocal", ex);
            return new TeamLocalDoc();
        }
    }

    /// A folder written before the shared/local split: the chat and the
    /// rendered files sat beside team.json. Move the chat (it is the room's
    /// history), drop the rendered files (they are rendered again), so a
    /// commit of the team folder never carries them.
    private void MigrateLayout()
    {
        try
        {
            Directory.CreateDirectory(LocalDir);
            var oldLedger = Path.Combine(Dir, "room.jsonl");
            if (File.Exists(oldLedger) && !File.Exists(LedgerPath)) File.Move(oldLedger, LedgerPath);
            var oldRoster = Path.Combine(Dir, "roster.md");
            if (File.Exists(oldRoster)) File.Delete(oldRoster);
            if (Directory.Exists(BotsDir))
                foreach (var stale in Directory.GetFiles(BotsDir, "system.md", SearchOption.AllDirectories))
                    File.Delete(stale);
        }
        catch (Exception ex) { Log.Error("TeamStore.Migrate", ex); }
    }

    private void Fail(string problem)
    {
        Readable = false;
        Problem = problem;
        Doc = new TeamDoc();
    }

    public void Save()
    {
        if (!Readable)
        {
            Log.Info("TeamStore.Save.refused", $"unreadable team at {Dir}");
            return;
        }
        try
        {
            AtomicFile.WriteAllText(JsonPath, JsonSerializer.Serialize(Doc, TeamJsonContext.Default.TeamDoc));
            _jsonStamp = File.GetLastWriteTimeUtc(JsonPath);
        }
        catch (Exception ex) { Log.Error("TeamStore.Save", ex); }
        try
        {
            var local = new TeamLocalDoc();
            foreach (var bot in Doc.Bots)
                if (bot.SessionId is Guid sid) local.Sessions[bot.Slug] = sid;
            Directory.CreateDirectory(LocalDir);
            AtomicFile.WriteAllText(LocalJsonPath, JsonSerializer.Serialize(local, TeamJsonContext.Default.TeamLocalDoc));
        }
        catch (Exception ex) { Log.Error("TeamStore.SaveLocal", ex); }
    }

    // ---- git ----------------------------------------------------------------

    /// The `.perch/.gitignore` that keeps boards local and lets the team
    /// travel. Only Perch's own boards-only file ("*" as the sole rule) is
    /// rewritten; a missing file (the user chose to track everything) or a
    /// hand-written one is respected.
    public static void EnsureShareable(string repoRoot)
    {
        var path = Path.Combine(repoRoot, ".perch", ".gitignore");
        try
        {
            if (!File.Exists(path)) return;
            var rules = File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToList();
            if (rules.Count == 1 && rules[0] == "*")
                AtomicFile.WriteAllText(path, ShareableGitIgnore);
        }
        catch (Exception ex) { Log.Error("TeamStore.EnsureShareable", ex); }
    }

    internal const string ShareableGitIgnore =
        "# Perch: boards are local staging context, not source; the team is shared.\n" +
        "# Positions, briefs, bots and their memory travel with the repository;\n" +
        "# team/local (which tab runs each bot here, and the room's chat) does not.\n" +
        "# Delete this file if you want boards tracked in git too.\n" +
        "*\n" +
        "!.gitignore\n" +
        "!team/\n" +
        "!team/**\n" +
        "team/local/\n";

    // ---- positions --------------------------------------------------------

    public TeamPosition AddPosition(string name, string purpose, string referenceRepo, string model)
    {
        var slug = UniqueSlug(name, "position", s => Doc.Position(s) != null);
        var pos = new TeamPosition
        {
            Slug = slug,
            Name = name.Trim(),
            Purpose = purpose.Trim(),
            ReferenceRepo = string.IsNullOrWhiteSpace(referenceRepo) ? RepoRoot : referenceRepo.Trim(),
            Model = (model ?? "").Trim(),
            CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Hat = TeamLooks.HatFor(name),
        };
        Doc.Positions.Add(pos);
        return pos;
    }

    /// Remove a position. Refused (false) while a bot holds it: the brief is
    /// what that bot's session is built on.
    public bool RemovePosition(string slug)
    {
        if (Doc.PositionInUse(slug)) return false;
        return Doc.Positions.RemoveAll(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    private readonly Dictionary<string, (DateTime Stamp, long Length, string Text)> _briefs = new();
    public string ReadBriefPreview(string positionSlug)
    {
        var path = BriefPathFor(positionSlug);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) { _briefs.Remove(positionSlug); return ""; }
            if (_briefs.TryGetValue(positionSlug, out var hit) && hit.Stamp == file.LastWriteTimeUtc && hit.Length == file.Length) return hit.Text;
            using var reader = File.OpenText(path);
            var chars = new char[16 * 1024];
            var text = new string(chars, 0, reader.ReadBlock(chars, 0, chars.Length));
            if (_briefs.Count >= 64) _briefs.Clear();
            _briefs[positionSlug] = (file.LastWriteTimeUtc, file.Length, text);
            return text;
        }
        catch (Exception ex) { Log.Error("TeamStore.ReadBriefPreview", ex); return ""; }
    }

    public string ReadBrief(string positionSlug)
    {
        var path = BriefPathFor(positionSlug);
        if (!File.Exists(path)) return "";
        try { return File.ReadAllText(path); }
        catch (Exception ex) { Log.Error("TeamStore.ReadBrief", ex); return ""; }
    }

    public void WriteBrief(string positionSlug, string text)
    {
        try { AtomicFile.WriteAllText(BriefPathFor(positionSlug), (text ?? "").Trim() + "\n"); }
        catch (Exception ex) { Log.Error("TeamStore.WriteBrief", ex); }
    }

    // ---- bots -------------------------------------------------------------

    /// Add a bot. `ccName` is the app-wide-unique session name the caller
    /// already minted; the slug is unique within this team. The bot's look is
    /// drawn here, once, and its memory file is started so the bot has
    /// something to edit from its first turn.
    public TeamBot AddBot(string nickname, string positionSlug, string ccName, bool worktree, string model)
    {
        var slug = UniqueSlug(nickname, "bot", s => Doc.Bot(s) != null);
        var bot = new TeamBot
        {
            Slug = slug,
            Nickname = nickname.Trim(),
            PositionSlug = positionSlug,
            CcName = ccName,
            Worktree = worktree,
            Model = (model ?? "").Trim(),
            CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Look = TeamLooks.RandomLook(Rng()),
        };
        Doc.Bots.Add(bot);
        if (!File.Exists(MemoryPathFor(slug)))
            WriteMemory(slug, TeamRender.MemorySeed(bot));
        return bot;
    }

    public bool RemoveBot(string slug)
    {
        var removed = Doc.Bots.RemoveAll(b => string.Equals(b.Slug, slug, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            if (string.Equals(Doc.LeadSlug, slug, StringComparison.OrdinalIgnoreCase)) Doc.LeadSlug = null;
            foreach (var board in Tasks.Open)
                board.Items.RemoveAll(i => string.Equals(i.Bot, slug, StringComparison.OrdinalIgnoreCase));
            foreach (var dir in new[] { Path.Combine(BotsDir, slug), Path.Combine(LocalDir, "bots", slug) })
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch (Exception ex) { Log.Error("TeamStore.RemoveBot", ex); }
            }
        }
        return removed;
    }

    /// The part of the bot's memory that rides in with every prompt (it edits
    /// the file itself): everything above a line that is exactly `---` when
    /// there is one within the cap, else the first MemoryMaxBytes. The rest
    /// stays on disk for the bot to Read when it needs it — a memory the bot
    /// had to keep cutting to fit was the wrong rule. "" when there is none yet.
    public string ReadMemory(string botSlug)
    {
        var path = MemoryPathFor(botSlug);
        if (!File.Exists(path)) return "";
        try { return InlineMemory(File.ReadAllText(path)); }
        catch (Exception ex) { Log.Error("TeamStore.ReadMemory", ex); return ""; }
    }

    /// The cut rule, on its own so it can be tested without a file.
    internal static string InlineMemory(string raw)
    {
        var text = (raw ?? "").Replace("\r\n", "\n").Trim();
        const string suffix = "\n[the rest is in the file — Read it when you need it]";
        // The marker is a line that is exactly `---`; probe with a trailing
        // newline so one at the very end still counts.
        var probe = text + "\n";
        var marker = probe.IndexOf("\n---\n", StringComparison.Ordinal);
        if (probe.StartsWith("---\n", StringComparison.Ordinal)) marker = 0;
        if (marker >= 0 && System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, marker)) <= MemoryMaxBytes)
        {
            var top = text[..marker].TrimEnd();
            var restStart = marker + 5;
            return restStart >= text.Length ? top : top + suffix;
        }
        if (System.Text.Encoding.UTF8.GetByteCount(text) <= MemoryMaxBytes) return text;
        var cut = Math.Min(text.Length, MemoryMaxBytes);
        while (cut > 0 && System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, cut)) > MemoryMaxBytes) cut--;
        return text[..cut].TrimEnd() + suffix;
    }

    public void WriteMemory(string botSlug, string text)
    {
        try { AtomicFile.WriteAllText(MemoryPathFor(botSlug), (text ?? "").Trim() + "\n"); }
        catch (Exception ex) { Log.Error("TeamStore.WriteMemory", ex); }
    }

    // ---- team knowledge ---------------------------------------------------

    /// How much of knowledge.md rides in with every prompt. Past this the lead
    /// is told to prune; the file itself is not cut.
    public const int KnowledgeMaxBytes = 3072;

    /// The knowledge file as a bot sees it: whole while it fits, else the first
    /// cap with a note. "" when there is none yet.
    public string ReadKnowledge()
    {
        if (!File.Exists(KnowledgePath)) return "";
        try
        {
            var text = File.ReadAllText(KnowledgePath).Replace("\r\n", "\n").Trim();
            if (Encoding.UTF8.GetByteCount(text) <= KnowledgeMaxBytes) return text;
            var cut = Math.Min(text.Length, KnowledgeMaxBytes);
            while (cut > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, cut)) > KnowledgeMaxBytes) cut--;
            return text[..cut].TrimEnd() + "\n[the file is over the cap — the lead prunes it; Read it for the rest]";
        }
        catch (Exception ex) { Log.Error("TeamStore.ReadKnowledge", ex); return ""; }
    }

    /// Whether the inlined part is being cut — the lead's cue to prune.
    public bool KnowledgeOverCap()
    {
        try { return File.Exists(KnowledgePath) && new FileInfo(KnowledgePath).Length > KnowledgeMaxBytes; }
        catch { return false; }
    }

    /// Add one fact as a line, signed with who and when. False when the same
    /// fact (ignoring case, punctuation at the end and the signature) is
    /// already there — a bot re-learning what a teammate wrote adds nothing.
    public bool AppendKnowledge(string fact, string by, out string line)
    {
        fact = OneLineOf(fact);
        line = $"- {fact} ({by}, {DateTime.UtcNow:yyyy-MM-dd})";
        try
        {
            var existing = File.Exists(KnowledgePath) ? File.ReadAllText(KnowledgePath) : "";
            var key = KnowledgeKey(fact);
            foreach (var raw in existing.Replace("\r\n", "\n").Split('\n'))
            {
                var l = raw.Trim();
                if (!l.StartsWith("- ", StringComparison.Ordinal)) continue;
                if (KnowledgeKey(l[2..]) == key) return false;
            }
            var sb = new StringBuilder(existing.TrimEnd());
            if (sb.Length == 0)
                sb.Append("# Team knowledge\n\nFacts every bot on this team needs. One line each, newest last; the lead prunes what goes stale.\n");
            sb.Append('\n').Append(line).Append('\n');
            AtomicFile.WriteAllText(KnowledgePath, sb.ToString());
            return true;
        }
        catch (Exception ex) { Log.Error("TeamStore.AppendKnowledge", ex); return false; }
    }

    /// A fact stripped of its signature, case, and trailing punctuation.
    internal static string KnowledgeKey(string line)
    {
        var s = line.Trim();
        // "(nick, 2026-09-06)" at the end is the signature, not the fact.
        var open = s.LastIndexOf(" (", StringComparison.Ordinal);
        if (open > 0 && s.EndsWith(')') && s.IndexOf(',', open) > open) s = s[..open];
        return s.TrimEnd('.', ' ', ';').ToLowerInvariant();
    }

    private static string OneLineOf(string text)
        => System.Text.RegularExpressions.Regex.Replace((text ?? "").Trim(), @"\s+", " ");

    // ---- team skills ------------------------------------------------------

    /// One skill: its folder key, the title on its first line, one line of
    /// what it is for, and the file a bot Reads to follow it.
    internal sealed record TeamSkill(string Slug, string Name, string Summary, string Path);

    public IReadOnlyList<TeamSkill> ListSkills()
    {
        var list = new List<TeamSkill>();
        try
        {
            if (!Directory.Exists(SkillsDir)) return list;
            foreach (var dir in Directory.GetDirectories(SkillsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var path = System.IO.Path.Combine(dir, "SKILL.md");
                if (!File.Exists(path)) continue;
                var slug = System.IO.Path.GetFileName(dir);
                string name = slug, summary = "";
                foreach (var raw in File.ReadLines(path))
                {
                    var l = raw.Trim();
                    if (l.Length == 0) continue;
                    if (l.StartsWith('#')) { if (name == slug) name = l.TrimStart('#').Trim(); continue; }
                    if (l.StartsWith('_') && l.EndsWith('_')) continue;   // the provenance line
                    summary = TeamRender.OneLine(l, 160);
                    break;
                }
                list.Add(new TeamSkill(slug, name, summary, path));
            }
        }
        catch (Exception ex) { Log.Error("TeamStore.ListSkills", ex); }
        return list;
    }

    /// Write (or overwrite) a skill. Returns its slug, or null when it could
    /// not be written.
    public string? WriteSkill(string name, string body, string by)
    {
        var slug = SkillSlug(name);
        if (slug.Length == 0) return null;
        try
        {
            var text = new StringBuilder();
            var trimmed = (body ?? "").Replace("\r\n", "\n").Trim();
            // A body that already starts with the title keeps it; else the name heads it.
            if (!trimmed.StartsWith("# ", StringComparison.Ordinal)) text.Append("# ").Append(name.Trim()).Append("\n\n");
            text.Append('_').Append("Saved by ").Append(by).Append(", ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd"))
                .Append(". Any bot may follow it; whoever finds it wrong fixes the file.").Append("_\n\n");
            text.Append(trimmed).Append('\n');
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SkillPathFor(slug))!);
            AtomicFile.WriteAllText(SkillPathFor(slug), text.ToString());
            return slug;
        }
        catch (Exception ex) { Log.Error("TeamStore.WriteSkill", ex); return null; }
    }

    internal static string SkillSlug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in (name ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length > 60 ? s[..60].TrimEnd('-') : s;
    }

    // ---- rendering --------------------------------------------------------

    /// Rewrite every bot's system.md from its position's current brief (and
    /// the lead role, for the lead). Called after a brief changes, a bot
    /// joins, or the lead changes; a running bot picks the new file up at
    /// its next launch.
    public void RenderSystemFiles(string projectName)
    {
        foreach (var bot in Doc.Bots)
        {
            var pos = Doc.Position(bot.PositionSlug);
            if (pos == null) continue;
            try
            {
                var text = TeamRender.SystemPrompt(bot, pos, ReadBrief(pos.Slug), projectName, MemoryPathFor(bot.Slug), Doc.IsLead(bot));
                AtomicFile.WriteAllText(SystemPathFor(bot.Slug), text);
            }
            catch (Exception ex) { Log.Error("TeamStore.RenderSystem", ex); }
        }
    }

    /// Rewrite roster.md and every bot's context.md (the roster, the task
    /// board as it concerns that bot, and its memory — what the hook inlines
    /// into each of its prompts). Cheap, so callers do it on every
    /// membership, presence or task change rather than tracking dirtiness.
    public void RenderRoster(string projectName, IReadOnlyDictionary<string, string>? presence = null,
        string? modelLimits = null, IReadOnlyDictionary<string, string>? addresses = null)
    {
        var roster = TeamRender.Roster(Doc, projectName, presence, modelLimits, addresses);
        try { AtomicFile.WriteAllText(RosterPath, roster); }
        catch (Exception ex) { Log.Error("TeamStore.RenderRoster", ex); }
        foreach (var bot in Doc.Bots)
        {
            try
            {
                AtomicFile.WriteAllText(ContextPathFor(bot.Slug),
                    TeamRender.Context(roster, bot, ReadMemory(bot.Slug), MemoryPathFor(bot.Slug),
                        TeamRender.TaskBlock(Tasks, Doc, bot),
                        TeamRender.KnowledgeBlock(ReadKnowledge(), KnowledgePath, KnowledgeOverCap() && Doc.IsLead(bot)),
                        TeamRender.SkillsBlock(ListSkills(), SkillsDir)));
            }
            catch (Exception ex) { Log.Error("TeamStore.RenderContext", ex); }
        }
    }

    // ---- helpers ----------------------------------------------------------

    /// Slugify and de-duplicate with "-2", "-3" … the way BoardStore.Create
    /// and Worktree.PathFor do, so two positions called "Frontend dev" coexist.
    internal static string UniqueSlug(string name, string fallback, Func<string, bool> taken)
    {
        var slug = GitProc.Slugify(name);
        if (slug.Length == 0) slug = fallback;
        var candidate = slug;
        for (var i = 2; taken(candidate) && i < 100; i++) candidate = $"{slug}-{i}";
        return candidate;
    }
}
