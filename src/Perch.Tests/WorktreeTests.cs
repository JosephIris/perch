using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Perch.Tests;

// The worktree behind a project tab. Two things are worth pinning:
//
//  - Slugify feeds a git BRANCH name and a Windows FOLDER name. Git rejects a
//    lot (spaces, "..", trailing ".lock", leading/trailing dashes) and Windows
//    rejects more (CON, PRN, …). A tab called "fix: the / bug" must not blow up
//    the worktree create, so we normalize rather than validate.
//  - The worktree itself is driven against real git — it's the whole mechanism
//    that makes per-tab loc counts true, so a mock would pin nothing.
public class WorktreeTests
{
    private static async Task<(bool ok, string stdout)> Git(string args, string cwd)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git", Arguments = args, WorkingDirectory = cwd,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                },
            };
            if (!p.Start()) return (false, "");
            var so = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode == 0, so);
        }
        catch { return (false, ""); }
    }

    private static void DeleteDir(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
        catch { }
    }

    /// A repo with one commit, plus a .env that must be seeded into the worktree.
    private static async Task<string?> SetupRepoAsync(string root)
    {
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        if (!(await Git("init -q -b main", repo)).ok) return null;   // no git on PATH
        await Git("config user.email t@t", repo);
        await Git("config user.name t", repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "one\n");
        await File.WriteAllTextAsync(Path.Combine(repo, ".env"), "SECRET=1\n");
        await Git("add a.txt", repo);   // .env stays untracked, like a real one
        await Git("commit -q -m init", repo);
        return repo;
    }

    [Theory]
    [InlineData("fix the loc diff", "fix-the-loc-diff")]
    [InlineData("fix: the / bug", "fix-the-bug")]          // git would reject the raw form
    [InlineData("  Trim Me  ", "trim-me")]
    [InlineData("CamelCase", "camelcase")]
    [InlineData("a--b__c", "a-b-c")]                        // runs of junk collapse to one dash
    [InlineData("...dots...", "dots")]                      // git rejects leading/trailing dots
    [InlineData("emoji 🎉 tab", "emoji-tab")]
    [InlineData("", "")]                                    // caller falls back
    [InlineData("!!!", "")]                                 // nothing to build a branch from
    public void Slugify_ProducesSomethingGitAndWindowsAccept(string input, string expected)
    {
        Assert.Equal(expected, GitProc.Slugify(input));
    }

    [Fact]
    public void Slugify_DodgesWindowsReservedDeviceNames()
    {
        // A folder literally named CON/PRN/NUL cannot be created on Windows, so a
        // tab called "con" would fail to get a worktree at all.
        Assert.Equal("con-tab", GitProc.Slugify("con"));
        Assert.Equal("nul-tab", GitProc.Slugify("NUL"));
        Assert.Equal("com1-tab", GitProc.Slugify("com1"));
        Assert.Equal("console", GitProc.Slugify("console"));   // only the EXACT names
    }

    [Fact]
    public async Task Create_CutsAWorktreeOnItsOwnBranch_OutsideTheRepo_AndSeedsEnv()
    {
        var root = Path.Combine(Path.GetTempPath(), "perch-wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repo = await SetupRepoAsync(root);
            if (repo == null) return;   // no git on PATH

            var settings = new Settings { WorktreeRoot = Path.Combine(root, "worktrees") };
            var project = new Project { Name = "repo", Path = repo };

            var (path, branch, error) = await Worktree.CreateAsync(settings, project, "fix the loc diff");

            Assert.Null(error);
            Assert.NotNull(path);
            Assert.Equal("fix-the-loc-diff", branch);
            Assert.True(Directory.Exists(path));
            Assert.True(File.Exists(Path.Combine(path!, "a.txt")));

            // OUTSIDE the repo. A worktree inside it (which is what cc's own
            // --worktree does) shows up as untracked in the main checkout and
            // inflates its file count.
            Assert.False(path!.StartsWith(repo, StringComparison.OrdinalIgnoreCase));

            // Its own branch, checked out in the worktree; main still on main.
            Assert.Equal("fix-the-loc-diff", (await Git("rev-parse --abbrev-ref HEAD", path!)).stdout.Trim());
            Assert.Equal("main", (await Git("rev-parse --abbrev-ref HEAD", repo)).stdout.Trim());

            // Seeded: a clean checkout has no .env, so the agent's first run would
            // fail and it would start "fixing" a broken environment.
            Assert.True(File.Exists(Path.Combine(path!, ".env")));
            Assert.Equal("SECRET=1\n", await File.ReadAllTextAsync(Path.Combine(path!, ".env")));
        }
        finally { DeleteDir(root); }
    }

    [Fact]
    public async Task Create_TwoTabsSameName_GetSeparateTrees()
    {
        var root = Path.Combine(Path.GetTempPath(), "perch-wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repo = await SetupRepoAsync(root);
            if (repo == null) return;

            var settings = new Settings { WorktreeRoot = Path.Combine(root, "worktrees") };
            var project = new Project { Name = "repo", Path = repo };

            var a = await Worktree.CreateAsync(settings, project, "same name");
            Assert.Null(a.error);

            // Second tab, same name. It must NOT land in the first one's directory
            // and inherit its work — the suffix keeps them apart. (The branch is
            // reused, which is the point: "same name" means "same line of work".)
            var b = await Worktree.CreateAsync(settings, project, "same name");
            if (b.error != null)
            {
                // git refuses to check one branch out in two worktrees. That's a
                // legitimate outcome — what must NOT happen is silently reusing
                // tree A's folder.
                Assert.NotEqual(a.path, b.path);
                return;
            }
            Assert.NotEqual(a.path, b.path);
        }
        finally { DeleteDir(root); }
    }

    [Fact]
    public async Task Create_FailsLoudly_WhenTheRepoIsGone()
    {
        var settings = new Settings { WorktreeRoot = Path.Combine(Path.GetTempPath(), "perch-wt-none") };
        var project = new Project
        {
            Name = "ghost",
            Path = Path.Combine(Path.GetTempPath(), "perch-ghost-" + Guid.NewGuid()),
        };

        var (path, _, error) = await Worktree.CreateAsync(settings, project, "x");

        // Must NOT quietly degrade to "open in the main checkout" — that's the
        // very collision the worktree exists to prevent.
        Assert.Null(path);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Create_RejectsANameWithNothingToSlugify()
    {
        var root = Path.Combine(Path.GetTempPath(), "perch-wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repo = await SetupRepoAsync(root);
            if (repo == null) return;
            var settings = new Settings { WorktreeRoot = Path.Combine(root, "worktrees") };
            var project = new Project { Name = "repo", Path = repo };

            var (path, _, error) = await Worktree.CreateAsync(settings, project, "!!!");
            Assert.Null(path);
            Assert.NotNull(error);
        }
        finally { DeleteDir(root); }
    }

    // Seeds may be NESTED — this very repo keeps node_modules in src/web, not at
    // the root, so a top-level-only seeder would hand the agent a worktree whose
    // `npm test` fails, and it would start "fixing" a broken environment.
    [Fact]
    public async Task Seed_LinksNestedDependencyDirs_AndCopiesGlobbedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "perch-wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repo = await SetupRepoAsync(root);
            if (repo == null) return;

            // deps live one level down, like src/web here
            var deps = Path.Combine(repo, "src", "web", "node_modules", "pkg");
            Directory.CreateDirectory(deps);
            await File.WriteAllTextAsync(Path.Combine(deps, "index.js"), "module.exports=1\n");
            await Git("add -A", repo);
            await Git("commit -q -m src --allow-empty", repo);

            var settings = new Settings
            {
                WorktreeRoot = Path.Combine(root, "worktrees"),
                WorktreeSeedPaths = new() { ".env*", "src/web/node_modules" },
            };
            var project = new Project { Name = "repo", Path = repo };

            var (path, _, error) = await Worktree.CreateAsync(settings, project, "nested");
            Assert.Null(error);

            Assert.True(File.Exists(Path.Combine(path!, ".env")));            // glob copy
            var linked = Path.Combine(path!, "src", "web", "node_modules");
            Assert.True(Directory.Exists(linked));                            // nested link
            Assert.True(File.Exists(Path.Combine(linked, "pkg", "index.js"))); // reads through
        }
        finally { DeleteDir(root); }
    }

    // THE dangerous one. The junction points at the main checkout's real
    // node_modules; deleting the worktree folder without unlinking first would
    // follow it and delete the user's actual dependencies.
    [Fact]
    public async Task Remove_UnlinksJunctions_AndDoesNotEatTheRealNodeModules()
    {
        var root = Path.Combine(Path.GetTempPath(), "perch-wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repo = await SetupRepoAsync(root);
            if (repo == null) return;

            var realDeps = Path.Combine(repo, "node_modules");
            Directory.CreateDirectory(realDeps);
            await File.WriteAllTextAsync(Path.Combine(realDeps, "keep-me.txt"), "precious\n");

            var settings = new Settings
            {
                WorktreeRoot = Path.Combine(root, "worktrees"),
                WorktreeSeedPaths = new() { "node_modules" },
            };
            var project = new Project { Name = "repo", Path = repo };

            var (path, _, error) = await Worktree.CreateAsync(settings, project, "linky");
            Assert.Null(error);
            // Junctions need no privilege, but if the sandbox somehow refused,
            // there's nothing to prove — skip rather than assert a false pass.
            var link = new DirectoryInfo(Path.Combine(path!, "node_modules"));
            if (!link.Exists || link.LinkTarget == null) return;

            await Worktree.RemoveAsync(repo, path!);

            Assert.False(Directory.Exists(path!));                                  // tree gone
            Assert.True(Directory.Exists(realDeps));                                // deps SURVIVE
            Assert.True(File.Exists(Path.Combine(realDeps, "keep-me.txt")));        // …intact
        }
        finally { DeleteDir(root); }
    }

    [Fact]
    public async Task Remove_ReclaimsTheTree_ButKeepsTheBranchAndItsCommits()
    {
        var root = Path.Combine(Path.GetTempPath(), "perch-wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repo = await SetupRepoAsync(root);
            if (repo == null) return;

            var settings = new Settings { WorktreeRoot = Path.Combine(root, "worktrees") };
            var project = new Project { Name = "repo", Path = repo };
            var (path, branch, _) = await Worktree.CreateAsync(settings, project, "keep my work");
            Assert.NotNull(path);

            // The tab does real work and commits it.
            await File.WriteAllTextAsync(Path.Combine(path!, "b.txt"), "work\n");
            await Git("add -A", path!);
            await Git("commit -q -m \"the agent's work\"", path!);

            await Worktree.RemoveAsync(repo, path!);

            Assert.False(Directory.Exists(path!));
            // The BRANCH survives. Closing a tab must never be able to destroy the
            // commits — they're the entire point of the work.
            var (ok, log) = await Git($"log --oneline {branch} -1", repo);
            Assert.True(ok);
            Assert.Contains("the agent's work", log);
        }
        finally { DeleteDir(root); }
    }
}
