using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perch;

/// A registered repo. The sidebar's project mode groups sessions (tabs) under
/// these; a tab's worktree is cut from Path.
///
/// Path is the repo ROOT (`git rev-parse --show-toplevel`), stored normalized
/// (see ProjectStore.Normalize) so the same repo reached via a trailing slash,
/// a different case, or a subdirectory can't register twice.
internal sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long AddedAtUnixMs { get; set; }

    /// Hidden from the sidebar's project list (folded into its "Hidden" drawer).
    /// Everything else about the registration survives — name, seeds, which
    /// tabs file under it — so unhiding restores the group exactly as it was.
    /// Distinct from unregistering, which severs the tabs' filing.
    public bool Hidden { get; set; }

    /// Per-project override of what gets seeded into a new worktree (see
    /// Settings.WorktreeSeedPaths). Null or empty = inherit the global list.
    ///
    /// It has to be per-project because repos disagree about where their deps
    /// live: THIS repo keeps node_modules in src/web, a Python one has .venv at
    /// the root, a Go one needs nothing at all. A single global list is wrong for
    /// somebody the moment you have two projects.
    public List<string>? SeedPaths { get; set; }

    /// The export file actually used for this project.
    public IReadOnlyList<string> EffectiveSeeds(Settings settings) =>
        SeedPaths is { Count: > 0 } ? SeedPaths : settings.WorktreeSeedPaths;
}

/// Where a project candidate came from — drives the grouping in the
/// registration UI ("repos you're already working in" reads very differently
/// from "found in C:\src").
internal enum ProjectSource { InUse, Scanned }

internal sealed record ProjectCandidate(string Path, string Name, ProjectSource Source);

/// The registered-project list, persisted next to sessions.json. Separate file
/// (not folded into Settings) because it's list-shaped and mutates on its own
/// cadence — same reasoning that keeps SessionStore separate.
internal sealed class ProjectStore
{
    public List<Project> Projects { get; } = new();

    private static string StorePath => System.IO.Path.Combine(
        AppPaths.DataRoot, "perch", "projects.json");

    /// Normalized key for a repo path: full path, no trailing separator,
    /// lower-cased. Windows paths are case-insensitive, so `C:\Dev\Repo` and
    /// `c:\dev\repo\` are the SAME project and must not both register.
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { path = System.IO.Path.GetFullPath(path); } catch { /* keep as-is */ }
        path = path.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                            System.IO.Path.AltDirectorySeparatorChar);
        return path.ToLowerInvariant();
    }

    public Project? ByPath(string path)
    {
        var key = Normalize(path);
        return Projects.FirstOrDefault(p => Normalize(p.Path) == key);
    }

    public Project? ById(Guid id) => Projects.FirstOrDefault(p => p.Id == id);

    /// Registers a repo. Idempotent: re-adding a known path returns the
    /// existing project rather than a duplicate (the page can fire project.add
    /// from the scan dialog and the folder picker both, for the same repo).
    /// Returns null when the path is empty or missing on disk.
    public Project? Add(string path, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var existing = ByPath(path);
        if (existing != null) return existing;
        if (!Directory.Exists(path)) return null;

        string full;
        try { full = System.IO.Path.GetFullPath(path); } catch { full = path; }
        full = full.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                            System.IO.Path.AltDirectorySeparatorChar);

        var p = new Project
        {
            Name = string.IsNullOrWhiteSpace(name) ? BaseName(full) : name!,
            Path = full,
            AddedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        Projects.Add(p);
        return p;
    }

    public bool Remove(Guid id)
    {
        var p = ById(id);
        if (p == null) return false;
        Projects.Remove(p);
        return true;
    }

    /// Folder name of a repo path — the project's default display name.
    /// GetFileName returns "" for a path with a trailing separator, hence the
    /// trim in Add(); a drive root ("C:\") has no basename, so fall back to
    /// the raw path rather than showing an empty row.
    public static string BaseName(string path)
    {
        var n = System.IO.Path.GetFileName(path);
        return string.IsNullOrEmpty(n) ? path : n;
    }

    public static ProjectStore Load()
    {
        var store = new ProjectStore();
        try
        {
            if (!File.Exists(StorePath)) return store;
            var dto = JsonSerializer.Deserialize(
                File.ReadAllText(StorePath), ProjectStoreJsonContext.Default.ProjectStoreDto);
            if (dto?.Projects is { Count: > 0 })
                foreach (var p in dto.Projects)
                    if (!string.IsNullOrWhiteSpace(p.Path))
                        store.Projects.Add(p);
        }
        catch { /* unreadable/corrupt → start empty rather than crash on launch */ }
        return store;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StorePath)!);
            var dto = new ProjectStoreDto { Version = 1, Projects = Projects.ToList() };
            File.WriteAllText(
                StorePath, JsonSerializer.Serialize(dto, ProjectStoreJsonContext.Default.ProjectStoreDto));
        }
        catch { /* best-effort, same as SessionStore/Settings */ }
    }
}

internal sealed class ProjectStoreDto
{
    public int Version { get; set; }
    public List<Project>? Projects { get; set; }
}

[JsonSerializable(typeof(ProjectStoreDto))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ProjectStoreJsonContext : JsonSerializerContext { }

/// Finds repos worth offering as projects. Two sources, deliberately:
///
///  - InUse: repo roots of the panes you already have open. Zero configuration,
///    and it's the set you almost certainly want — you're literally working in
///    them right now.
///  - Scanned: each configured scan root, walked ONE level deep. One level
///    because a recursive walk of a dev folder is slow, and because nobody
///    nests repos more than one deep under their code directory. Matching git's
///    own rule, a child counts as a repo when it has a `.git` (dir OR file — a
///    linked worktree's .git is a FILE, and excluding those would hide exactly
///    the worktrees this feature creates).
///
/// Already-registered paths are filtered out, so the dialog only ever shows
/// things you can actually add.
internal static class ProjectScan
{
    public static IReadOnlyList<ProjectCandidate> Candidates(
        IEnumerable<string> scanRoots,
        IEnumerable<string> inUseRepoRoots,
        ProjectStore store)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outp = new List<ProjectCandidate>();

        void Offer(string path, ProjectSource source)
        {
            var key = ProjectStore.Normalize(path);
            if (key.Length == 0) return;
            if (!seen.Add(key)) return;              // already offered (in-use wins over scanned)
            if (store.ByPath(path) != null) return;  // already registered
            outp.Add(new ProjectCandidate(path, ProjectStore.BaseName(path), source));
        }

        foreach (var root in inUseRepoRoots)
            if (!string.IsNullOrWhiteSpace(root)) Offer(root, ProjectSource.InUse);

        foreach (var root in scanRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(root); }
            catch { continue; }                      // unreadable root: skip, don't fail the scan
            foreach (var child in children)
            {
                if (!IsRepo(child)) continue;
                Offer(child, ProjectSource.Scanned);
            }
        }
        return outp;
    }

    /// A `.git` directory (normal clone) OR file (linked worktree / submodule).
    public static bool IsRepo(string dir)
    {
        var git = System.IO.Path.Combine(dir, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }
}
