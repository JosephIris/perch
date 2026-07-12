using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Perch;

/// Creating and seeding the git worktree behind a project tab.
///
/// WHY a worktree at all: two agents in one directory don't race over git, they
/// race over write(). Agent A reads a file, thinks for twenty seconds, writes it
/// back; agent B edited it meanwhile; A's write silently reverts B. No conflict,
/// no error — git never sees it. A worktree gives each tab its own directory, so
/// that can't happen, and it's also what makes the per-tab loc/commit chips TRUE
/// (a shared checkout would have every tab wearing every other tab's lines).
///
/// WHY we create it and not `claude --worktree`: that flag exists, but it puts
/// the tree inside the repo (.claude/worktrees/<name>, which the main checkout
/// then counts as untracked) and only moves the AGENT into it — the pane's shell
/// stays put, and every git signal we compute is measured against the PANE's cwd.
/// The chips would sit at ~0 while the agent worked somewhere we couldn't see.
internal static class Worktree
{
    /// Where worktrees live. Deliberately OUTSIDE the repo: a tree inside it
    /// shows up as untracked in the main checkout (which is exactly what cc's
    /// own --worktree does, and it pollutes the loc chip).
    ///
    /// Local, not roaming, AppData — these are full checkouts, and a roaming
    /// profile would try to sync them across machines.
    public static string Root(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.WorktreeRoot)) return settings.WorktreeRoot;
        var over = Environment.GetEnvironmentVariable("PERCH_DATA_DIR");
        var b = string.IsNullOrWhiteSpace(over)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : over;
        return Path.Combine(b, "Perch", "worktrees");
    }

    /// Free path for a tab named <paramref name="slug"/> under a project. If the
    /// folder is taken (a previous tab of the same name whose tree wasn't cleaned
    /// up), suffix it — never silently reuse a directory that already has someone
    /// else's work in it.
    public static string PathFor(Settings settings, Project project, string slug)
    {
        var baseDir = Path.Combine(Root(settings), ProjectStore.BaseName(project.Path));
        var path = Path.Combine(baseDir, slug);
        for (var i = 2; Directory.Exists(path) && i < 100; i++)
            path = Path.Combine(baseDir, $"{slug}-{i}");
        return path;
    }

    /// Creates the worktree and seeds it. Returns (path, branch) on success, or
    /// an error string the caller shows — a failed worktree must NOT silently
    /// degrade into "spawn in the main checkout", which is precisely the
    /// collision the feature exists to prevent.
    public static async Task<(string? path, string? branch, string? error)> CreateAsync(
        Settings settings, Project project, string name)
    {
        var slug = GitProc.Slugify(name);
        if (slug.Length == 0) return (null, null, "That name has no letters or digits to build a branch from.");
        if (!Directory.Exists(project.Path)) return (null, null, $"{project.Name} is no longer at {project.Path}.");

        var path = PathFor(settings, project, slug);
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
        catch (Exception ex) { return (null, null, ex.Message); }

        var err = await GitProc.WorktreeAddAsync(project.Path, path, slug);
        if (err != null) return (null, null, err);

        await SeedAsync(project.Path, path, project.EffectiveSeeds(settings));
        return (path, slug, null);
    }

    /// A fresh worktree is a CLEAN checkout: no .env, no node_modules, no .venv.
    /// Without these the agent's first test run fails and it starts "fixing" a
    /// broken environment — the single biggest way this feature turns into a trap.
    ///
    /// Small ignored config files are COPIED (fast, and each tab should be free to
    /// edit its own). Heavy dependency directories are JUNCTIONED rather than
    /// copied: a node_modules copy is minutes and gigabytes per tab, which would
    /// make opening a tab feel broken. A junction is instant and takes no disk.
    ///
    /// Trade-off, stated plainly: junctioned deps are SHARED with the main
    /// checkout, so an `npm install` inside one tab is visible to the others.
    /// That's the same thing that happens when you switch branches in one
    /// checkout, which is what everyone does today — and it beats waiting minutes
    /// per tab. Best-effort throughout: seeding is a convenience, and a failure
    /// here must never take the worktree (which is real work) down with it.
    public static async Task SeedAsync(string repo, string worktree, IEnumerable<string> seeds)
    {
        foreach (var raw in seeds)
        {
            var rel = (raw ?? "").Trim().Replace('/', Path.DirectorySeparatorChar);
            if (rel.Length == 0) continue;
            // Never let a seed path climb out of the repo (or the worktree).
            if (rel.Contains("..") || Path.IsPathRooted(rel)) continue;

            try
            {
                if (rel.Contains('*'))
                {
                    // Glob (".env*", "src/*.local"): copy matching FILES. Small,
                    // and each tab should be free to edit its own.
                    var dir = Path.GetDirectoryName(rel) ?? "";
                    var pattern = Path.GetFileName(rel);
                    var srcDir = Path.Combine(repo, dir);
                    if (!Directory.Exists(srcDir)) continue;
                    var dstDir = Path.Combine(worktree, dir);
                    Directory.CreateDirectory(dstDir);
                    foreach (var src in Directory.EnumerateFiles(srcDir, pattern))
                    {
                        var dst = Path.Combine(dstDir, Path.GetFileName(src));
                        if (!File.Exists(dst)) File.Copy(src, dst);
                    }
                    continue;
                }

                var srcPath = Path.Combine(repo, rel);
                var dstPath = Path.Combine(worktree, rel);
                if (File.Exists(dstPath) || Directory.Exists(dstPath)) continue;   // tracked already

                if (Directory.Exists(srcPath))
                {
                    // Heavy dependency dir → junction. Copying node_modules is
                    // minutes and gigabytes PER TAB, which would make opening a
                    // tab feel broken. The parent must exist first: a nested seed
                    // like src/web/node_modules lands inside a tracked directory
                    // that git already created, but don't assume it.
                    Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                    await JunctionAsync(dstPath, srcPath);
                }
                else if (File.Exists(srcPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                    File.Copy(srcPath, dstPath);
                }
            }
            catch (Exception ex) { Log.Info("Worktree.seed", $"{rel} skipped: {ex.Message}"); }
        }
    }

    /// Deletes every junction in the tree WITHOUT descending into one.
    ///
    /// The obvious `EnumerateDirectories(.., AllDirectories)` is a trap here: it
    /// follows reparse points, so it would walk the whole of the real
    /// node_modules (slow, and cyclic if anything links back). We recurse by hand
    /// and stop at any link — a link is deleted, never entered. Depth-capped as a
    /// second belt against a pathological tree.
    private static void UnlinkJunctions(string dir, int depth)
    {
        if (depth > 8) return;
        string[] children;
        try { children = Directory.GetDirectories(dir); }
        catch { return; }

        foreach (var child in children)
        {
            try
            {
                var info = new DirectoryInfo(child);
                if (!info.Exists) continue;
                if (info.LinkTarget != null) { info.Delete(); continue; }   // unlink; do NOT recurse
                UnlinkJunctions(child, depth + 1);
            }
            catch (Exception ex) { Log.Info("Worktree.remove", $"unlink {child}: {ex.Message}"); }
        }
    }

    /// Directory junction (mklink /J). NOT a symlink: a real symlink needs
    /// SeCreateSymbolicLinkPrivilege (admin, or Developer Mode) on Windows, so
    /// Directory.CreateSymbolicLink would fail for most users. A junction needs
    /// no privilege and behaves the same for reads.
    private static async Task JunctionAsync(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (p == null) return;
        await p.WaitForExitAsync();
    }

    /// Tears down a tab's worktree. Keeps the BRANCH — the commits are the work,
    /// and closing a tab must never destroy them. Junctioned dependency dirs are
    /// removed as LINKS first: deleting the worktree folder without unlinking
    /// would follow the junction and delete the REAL node_modules out of the main
    /// checkout, which would be a spectacular way to ruin someone's afternoon.
    public static async Task RemoveAsync(string repo, string worktree)
    {
        // Unlink EVERY junction in the tree, at any depth, before git touches it.
        // Deleting the folder without unlinking would follow the junction and
        // delete the REAL node_modules out of the main checkout — a spectacular
        // way to ruin someone's afternoon. Found by walking rather than by
        // re-reading the export file, because the seeds may have changed since this
        // tree was made.
        UnlinkJunctions(worktree, depth: 0);
        await GitProc.WorktreeRemoveAsync(repo, worktree);
    }
}
