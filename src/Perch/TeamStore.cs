using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public string BriefPathFor(string positionSlug) => Path.Combine(PositionsDir, positionSlug, "brief.md");
    public string MemoryPathFor(string botSlug) => Path.Combine(BotsDir, botSlug, "memory.md");
    public string SystemPathFor(string botSlug) => Path.Combine(LocalDir, "bots", botSlug, "system.md");
    public string ContextPathFor(string botSlug) => Path.Combine(LocalDir, "bots", botSlug, "context.md");

    /// Largest memory a bot gets back per prompt. The roster is ~1.5 KB for a
    /// five-bot team and the hook inlines at most 6 KB, so this leaves room.
    public const int MemoryMaxBytes = 2048;

    private RoomLedger? _ledger;
    public RoomLedger Ledger => _ledger ??= new RoomLedger(LedgerPath);

    /// The task board (tasks.json beside team.json; shared). Read on first
    /// use and on Reload; a missing or unreadable file is an empty board —
    /// never fatal, the team file is the one that matters.
    private TaskDoc? _tasks;
    public TaskDoc Tasks => _tasks ??= LoadTasks();

    private TaskDoc LoadTasks()
    {
        try
        {
            if (!File.Exists(TasksPath)) return new TaskDoc();
            var doc = JsonSerializer.Deserialize(File.ReadAllText(TasksPath), TaskJsonContext.Default.TaskDoc);
            if (doc == null) return new TaskDoc();
            doc.Done ??= new();
            if (doc.Current != null) doc.Current.Items ??= new();
            return doc;
        }
        catch (Exception ex)
        {
            Log.Error("TeamStore.ReadTasks", ex);
            return new TaskDoc();
        }
    }

    public void SaveTasks()
    {
        if (!Readable) return;
        try
        {
            var doc = Tasks;
            while (doc.Done.Count > TaskDoc.DoneKept) doc.Done.RemoveAt(0);
            AtomicFile.WriteAllText(TasksPath, JsonSerializer.Serialize(doc, TaskJsonContext.Default.TaskDoc));
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
            Tasks.Current?.Items.RemoveAll(i => string.Equals(i.Bot, slug, StringComparison.OrdinalIgnoreCase));
            foreach (var dir in new[] { Path.Combine(BotsDir, slug), Path.Combine(LocalDir, "bots", slug) })
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch (Exception ex) { Log.Error("TeamStore.RemoveBot", ex); }
            }
        }
        return removed;
    }

    /// The bot's memory as it stands (it edits the file itself), capped for
    /// the prompt. "" when there is none yet.
    public string ReadMemory(string botSlug)
    {
        var path = MemoryPathFor(botSlug);
        if (!File.Exists(path)) return "";
        try
        {
            var text = File.ReadAllText(path).Trim();
            if (System.Text.Encoding.UTF8.GetByteCount(text) <= MemoryMaxBytes) return text;
            var cut = Math.Min(text.Length, MemoryMaxBytes);
            while (cut > 0 && System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, cut)) > MemoryMaxBytes) cut--;
            return text[..cut].TrimEnd() + "\n[memory truncated — keep it under 2 KB]";
        }
        catch (Exception ex) { Log.Error("TeamStore.ReadMemory", ex); return ""; }
    }

    public void WriteMemory(string botSlug, string text)
    {
        try { AtomicFile.WriteAllText(MemoryPathFor(botSlug), (text ?? "").Trim() + "\n"); }
        catch (Exception ex) { Log.Error("TeamStore.WriteMemory", ex); }
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
    public void RenderRoster(string projectName, IReadOnlyDictionary<string, string>? presence = null)
    {
        var roster = TeamRender.Roster(Doc, projectName, presence);
        try { AtomicFile.WriteAllText(RosterPath, roster); }
        catch (Exception ex) { Log.Error("TeamStore.RenderRoster", ex); }
        foreach (var bot in Doc.Bots)
        {
            try
            {
                AtomicFile.WriteAllText(ContextPathFor(bot.Slug),
                    TeamRender.Context(roster, bot, ReadMemory(bot.Slug), MemoryPathFor(bot.Slug),
                        TeamRender.TaskBlock(Tasks, Doc, bot)));
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
