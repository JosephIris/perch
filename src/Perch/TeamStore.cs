using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Perch;

/// Reads and writes a project's team folder. File mechanics only — deciding
/// WHEN to render, deliver, or relaunch lives in TeamController.
///
/// ## Layout
///
///   &lt;repo&gt;\.perch\team\
///     team.json                 the TeamDoc (positions + bots)
///     roster.md                 rendered; the prompt hook injects it every turn
///     room.jsonl                the room ledger (RoomLedger)
///     positions\&lt;slug&gt;\brief.md the position's standing brief, hand-editable
///     bots\&lt;slug&gt;\system.md     rendered; appended to the bot's system prompt at launch
///
/// Rooted at the MAIN checkout, never a worktree: every bot of a project, in
/// whichever worktree it runs, is told absolute paths into this one folder.
/// `.perch` is gitignored by the same file boards use; deleting that
/// .gitignore is the opt-in to sharing a team with collaborators.
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
    public bool Readable { get; private set; } = true;
    public string Problem { get; private set; } = "";
    public TeamDoc Doc { get; private set; } = new();

    public string JsonPath => Path.Combine(Dir, "team.json");
    public string RosterPath => Path.Combine(Dir, "roster.md");
    public string LedgerPath => Path.Combine(Dir, "room.jsonl");
    public string PositionsDir => Path.Combine(Dir, "positions");
    public string BotsDir => Path.Combine(Dir, "bots");
    public string BriefPathFor(string positionSlug) => Path.Combine(PositionsDir, positionSlug, "brief.md");
    public string SystemPathFor(string botSlug) => Path.Combine(BotsDir, botSlug, "system.md");

    private RoomLedger? _ledger;
    public RoomLedger Ledger => _ledger ??= new RoomLedger(LedgerPath);

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
        return store;
    }

    /// Create the team folder (idempotent) and return the store.
    public static TeamStore Create(string repoRoot)
    {
        var existing = Open(repoRoot);
        if (existing != null) return existing;
        var store = new TeamStore(repoRoot);
        Directory.CreateDirectory(store.Dir);
        BoardStore.EnsureGitIgnored(repoRoot);
        store.Save();
        return store;
    }

    private void Load()
    {
        if (!File.Exists(JsonPath))
        {
            Doc = new TeamDoc();     // a folder with no index yet is empty, not broken
            return;
        }
        string text;
        try { text = File.ReadAllText(JsonPath); }
        catch (Exception ex)
        {
            Log.Error("TeamStore.Read", ex);
            Fail("The team file could not be read.");
            return;
        }
        try
        {
            var doc = JsonSerializer.Deserialize(text, TeamJsonContext.Default.TeamDoc);
            if (doc == null) { Fail("The team file could not be parsed."); return; }
            if (doc.V > 1) { Fail($"This team was written by a newer version of Perch (v{doc.V})."); return; }
            doc.Positions ??= new();
            doc.Bots ??= new();
            Doc = doc;
        }
        catch (Exception ex)
        {
            Log.Error("TeamStore.Parse", ex);
            Fail("The team file could not be parsed.");
        }
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
        try { AtomicFile.WriteAllText(JsonPath, JsonSerializer.Serialize(Doc, TeamJsonContext.Default.TeamDoc)); }
        catch (Exception ex) { Log.Error("TeamStore.Save", ex); }
    }

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
    /// already minted; the slug is unique within this team.
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
        };
        Doc.Bots.Add(bot);
        return bot;
    }

    public bool RemoveBot(string slug)
    {
        var removed = Doc.Bots.RemoveAll(b => string.Equals(b.Slug, slug, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            try
            {
                var dir = Path.Combine(BotsDir, slug);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) { Log.Error("TeamStore.RemoveBot", ex); }
        }
        return removed;
    }

    // ---- rendering --------------------------------------------------------

    /// Rewrite every bot's system.md from its position's current brief.
    /// Called after a brief changes or a bot joins; a running bot picks the
    /// new file up at its next launch.
    public void RenderSystemFiles(string projectName)
    {
        foreach (var bot in Doc.Bots)
        {
            var pos = Doc.Position(bot.PositionSlug);
            if (pos == null) continue;
            try
            {
                var text = TeamRender.SystemPrompt(bot, pos, ReadBrief(pos.Slug), projectName);
                AtomicFile.WriteAllText(SystemPathFor(bot.Slug), text);
            }
            catch (Exception ex) { Log.Error("TeamStore.RenderSystem", ex); }
        }
    }

    /// Rewrite roster.md. Cheap, so callers do it on every membership or
    /// presence change rather than tracking dirtiness.
    public void RenderRoster(string projectName, IReadOnlyDictionary<string, string>? presence = null)
    {
        try { AtomicFile.WriteAllText(RosterPath, TeamRender.Roster(Doc, projectName, presence)); }
        catch (Exception ex) { Log.Error("TeamStore.RenderRoster", ex); }
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
