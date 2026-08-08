using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Perch.Tests;

// The project registry behind the sidebar's project mode. The interesting
// behaviour is all about NOT creating duplicates and NOT losing sessions:
//
//  - The same repo reached two ways (trailing slash, different case, the folder
//    picker vs the scan) must register ONCE. Windows paths are case-insensitive,
//    so a naive string compare would happily register C:\Dev\Repo twice.
//  - Unregistering a project must not close its tabs — they fall back to
//    "Other". Destroying live sessions as a side effect of a settings tweak
//    would be wildly disproportionate.
public class ProjectTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "perch-projects-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string MakeRepo(string parent, string name)
    {
        var dir = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));   // a `.git` DIR — a normal clone
        return dir;
    }

    [Fact]
    public void Add_DerivesNameFromFolder_AndIsIdempotent()
    {
        var root = TempDir();
        try
        {
            var repo = MakeRepo(root, "cmux-win");
            var store = new ProjectStore();

            var p = store.Add(repo);
            Assert.NotNull(p);
            Assert.Equal("cmux-win", p!.Name);
            Assert.Single(store.Projects);

            // Same repo, three other spellings: trailing separator, different
            // case, and a re-add of the exact path. None may duplicate.
            Assert.Same(p, store.Add(repo + Path.DirectorySeparatorChar));
            Assert.Same(p, store.Add(repo.ToUpperInvariant()));
            Assert.Same(p, store.Add(repo));
            Assert.Single(store.Projects);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Add_RejectsMissingPath()
    {
        var store = new ProjectStore();
        Assert.Null(store.Add(""));
        Assert.Null(store.Add(Path.Combine(Path.GetTempPath(), "perch-does-not-exist-" + Guid.NewGuid())));
        Assert.Empty(store.Projects);
    }

    [Fact]
    public void Scan_FindsReposOneLevelDeep_SkipsNonReposAndAlreadyRegistered()
    {
        var root = TempDir();
        try
        {
            var alpha = MakeRepo(root, "alpha");
            var beta = MakeRepo(root, "beta");
            Directory.CreateDirectory(Path.Combine(root, "not-a-repo"));         // no .git
            MakeRepo(Path.Combine(root, "not-a-repo"), "too-deep");              // 2 levels down

            var store = new ProjectStore();
            store.Add(beta);   // already registered → must not be offered again

            var found = ProjectScan.Candidates(new[] { root }, Array.Empty<string>(), store);

            Assert.Single(found);
            Assert.Equal(alpha, found[0].Path);
            Assert.Equal("alpha", found[0].Name);
            Assert.Equal(ProjectSource.Scanned, found[0].Source);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Scan_TreatsWorktreeDotGitFileAsARepo()
    {
        // A linked worktree's `.git` is a FILE, not a directory. Excluding those
        // would hide exactly the worktrees this feature goes on to create.
        var root = TempDir();
        try
        {
            var wt = Path.Combine(root, "feature-x");
            Directory.CreateDirectory(wt);
            File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: ../.git/worktrees/feature-x");

            var found = ProjectScan.Candidates(new[] { root }, Array.Empty<string>(), new ProjectStore());

            Assert.Single(found);
            Assert.Equal(wt, found[0].Path);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Scan_InUseWinsOverScanned_AndNeverOffersTheSameRepoTwice()
    {
        var root = TempDir();
        try
        {
            var repo = MakeRepo(root, "alpha");

            // The same repo is both open in a pane AND under a scan root. It must
            // appear ONCE, labelled InUse — "you're working in this right now" is
            // the more useful thing to tell the user.
            var found = ProjectScan.Candidates(new[] { root }, new[] { repo }, new ProjectStore());

            Assert.Single(found);
            Assert.Equal(ProjectSource.InUse, found[0].Source);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Scan_SurvivesAMissingOrUnreadableScanRoot()
    {
        var root = TempDir();
        try
        {
            var repo = MakeRepo(root, "alpha");
            var gone = Path.Combine(Path.GetTempPath(), "perch-gone-" + Guid.NewGuid());

            // A stale scan root (folder deleted since it was configured) must not
            // take the whole scan down with it.
            var found = ProjectScan.Candidates(new[] { gone, root }, Array.Empty<string>(), new ProjectStore());

            Assert.Single(found);
            Assert.Equal(repo, found[0].Path);
        }
        finally { Directory.Delete(root, true); }
    }

    [WindowsFact]
    public void Normalize_CollapsesCaseAndTrailingSeparators()
    {
        var a = ProjectStore.Normalize(@"C:\Dev\Repo");
        Assert.Equal(a, ProjectStore.Normalize(@"C:\Dev\Repo\"));
        Assert.Equal(a, ProjectStore.Normalize(@"c:\dev\repo"));
        Assert.NotEqual(a, ProjectStore.Normalize(@"C:\Dev\Other"));
        Assert.Equal("", ProjectStore.Normalize(""));
    }

    [UnixFact]
    public void Normalize_CollapsesTrailingSeparators_Unix()
    {
        var a = ProjectStore.Normalize("/dev/repo");
        Assert.Equal(a, ProjectStore.Normalize("/dev/repo/"));
        Assert.NotEqual(a, ProjectStore.Normalize("/dev/other"));
        Assert.Equal("", ProjectStore.Normalize(""));
    }

    // Color is scoped to the PROJECT, not global. A hue exists to tell a tab from
    // its siblings, and you only ever compare tabs under the same repo — scoping
    // globally would burn all six across three projects and start repeating
    // inside one, which is the only place a repeat actually costs you.
    [Fact]
    public void PickUnusedColorForProject_IsScopedToTheProject()
    {
        var store = new SessionStore();
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();

        var a = new Session { ProjectId = p1 };
        a.Root.ColorIndex = 0;
        var b = new Session { ProjectId = p1 };
        b.Root.ColorIndex = 1;
        // Two tabs in a DIFFERENT project have burned 2 and 3. They must not
        // constrain p1 at all.
        var c = new Session { ProjectId = p2 };
        c.Root.ColorIndex = 2;
        var d = new Session { ProjectId = p2 };
        d.Root.ColorIndex = 3;
        foreach (var s in new[] { a, b, c, d }) store.Sessions.Add(s);

        Assert.Equal(2, store.PickUnusedColorForProject(p1));   // NOT 4
        Assert.Equal(0, store.PickUnusedColorForProject(p2));   // NOT 4 either
        // A project with no tabs starts at the top of the palette.
        Assert.Equal(0, store.PickUnusedColorForProject(Guid.NewGuid()));
    }

    // Mirrors the real creation ORDER in OnProjectTabNew: AddNew() first (which
    // stamps a globally-unused color on the new leaf), then pick, THEN file it
    // under the project. Get that order wrong — file it first — and the pick sees
    // the tab's own placeholder color as "taken" and skips a hue: three tabs came
    // out 0, 2, 3 instead of 0, 1, 2. Only visible on the third tab, which is
    // exactly the kind of thing that ships.
    [Fact]
    public void ConsecutiveTabs_GetConsecutiveColors()
    {
        var store = new SessionStore();
        var p = Guid.NewGuid();
        var got = new List<int>();

        for (var i = 0; i < 3; i++)
        {
            var s = store.AddNew();                             // stamps a global color
            var color = store.PickUnusedColorForProject(p);     // pick BEFORE filing
            s.ProjectId = p;
            s.Root.ColorIndex = color;
            got.Add(color);
        }

        Assert.Equal(new[] { 0, 1, 2 }, got);
    }

    [Fact]
    public void PickUnusedColorForProject_RoundRobinsOnceThePaletteIsExhausted()
    {
        var store = new SessionStore();
        var p = Guid.NewGuid();
        for (var i = 0; i < 6; i++)
        {
            var s = new Session { ProjectId = p };
            s.Root.ColorIndex = i;
            store.Sessions.Add(s);
        }
        // All six taken → wrap rather than crash or return -1.
        var next = store.PickUnusedColorForProject(p);
        Assert.InRange(next, 0, 5);
    }

    // Seeds are per-project because repos disagree about where deps live: THIS
    // repo keeps node_modules in src/web, a Python one has .venv at the root, a
    // Go one needs nothing. One global list is wrong for somebody the moment you
    // have two projects.
    [Fact]
    public void EffectiveSeeds_InheritsTheGlobalListUntilOverridden()
    {
        var settings = new Settings { WorktreeSeedPaths = new() { ".env*", "node_modules" } };
        var p = new Project { Name = "repo", Path = @"C:\repo" };

        // No override → global.
        Assert.Equal(settings.WorktreeSeedPaths, p.EffectiveSeeds(settings));

        // An EMPTY override still inherits: an empty box means "I cleared it",
        // not "seed nothing", and inheriting is the safer read of that.
        p.SeedPaths = new List<string>();
        Assert.Equal(settings.WorktreeSeedPaths, p.EffectiveSeeds(settings));

        // A real override wins.
        p.SeedPaths = new List<string> { "src/web/node_modules" };
        Assert.Equal(new[] { "src/web/node_modules" }, p.EffectiveSeeds(settings));
    }

    [Fact]
    public void Remove_DropsTheProjectButReportsUnknownIds()
    {
        var root = TempDir();
        try
        {
            var store = new ProjectStore();
            var p = store.Add(MakeRepo(root, "alpha"))!;

            Assert.False(store.Remove(Guid.NewGuid()));   // unknown id → no-op
            Assert.Single(store.Projects);
            Assert.True(store.Remove(p.Id));
            Assert.Empty(store.Projects);
        }
        finally { Directory.Delete(root, true); }
    }
}
