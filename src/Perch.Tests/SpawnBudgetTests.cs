using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Perch;
using Xunit;

namespace Perch.Tests;

/// The regression this file exists to prevent.
///
/// Over two weeks the app went from 4 git shell-outs to 12, and gained a
/// localhost panel that ran `powershell.exe` every 3 seconds. No single commit
/// was unreasonable. Together they reached ~2.4 process launches per second at
/// idle, which is ~8,500 an hour — and because process creation on Windows
/// takes global kernel locks and every image is inspected by the AV stack, that
/// cost landed on the whole machine while never appearing as one fat process in
/// Task Manager. It was found by profiling a user's slow desktop, not by any
/// test.
///
/// These assert the two properties that would have caught it:
///   1. a refresh cycle costs a bounded number of subprocesses, and
///   2. an idle repo costs none at all.
public class SpawnBudgetTests : IDisposable
{
    private readonly string _repo;

    public SpawnBudgetTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "perch-spawn-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repo);
        Git("init -q");
        Git("config user.email t@t.t");
        Git("config user.name t");
        Git("config commit.gpgsign false");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "hello\n");
        Git("add -A");
        Git("commit -q -m first");
    }

    public void Dispose()
    {
        try { DeleteTree(_repo); } catch { }
    }

    /// One full status refresh must stay within a small, fixed budget. The
    /// number is deliberately tight: it is not "how many calls do we make
    /// today" but "how many can we afford before the aggregate bites again".
    /// Raising it is a decision someone should have to justify in review.
    [Fact]
    public async Task AStatusRefreshStaysWithinItsSpawnBudget()
    {
        GitProc.ClearAllCaches();
        using var scope = ProcRunner.BeginScope();
        var snap = await GitProc.StatusAsync(_repo);
        Assert.NotNull(snap);

        Assert.True(scope.SpawnCount <= 2,
            $"a status refresh spent {scope.SpawnCount} subprocesses, budget is 2.\n{scope.Breakdown()}");
    }

    /// The one that actually protects the desktop: nothing changed in the repo,
    /// so the second refresh must cost nothing. A timer-driven implementation
    /// fails this — which is the point.
    [Fact]
    public async Task ARepeatRefreshOnAnUnchangedRepoSpawnsNothing()
    {
        await GitProc.StatusAsync(_repo);          // prime the cache

        using var scope = ProcRunner.BeginScope();
        var again = await GitProc.StatusAsync(_repo);
        Assert.NotNull(again);

        Assert.True(scope.SpawnCount == 0,
            $"an unchanged repo cost {scope.SpawnCount} subprocesses, expected 0.\n{scope.Breakdown()}");
    }

    /// ...but a real change must still be seen. A cache that never invalidates
    /// would pass the test above and break the feature, so the two are asserted
    /// together on purpose.
    [Fact]
    public async Task AChangedRepoIsStillObserved()
    {
        var before = await GitProc.StatusAsync(_repo);
        Assert.NotNull(before);
        Assert.False(before!.Dirty);

        File.WriteAllText(Path.Combine(_repo, "b.txt"), "new file\n");
        GitProc.InvalidateCache(_repo);

        var after = await GitProc.StatusAsync(_repo);
        Assert.NotNull(after);
        Assert.True(after!.Dirty, "a new untracked file must make the repo read dirty");
    }

    /// The localhost panel used to be the most expensive spawn in the app. It
    /// must now cost zero subprocesses, however many times it scans.
    [Fact]
    public async Task TheLocalhostScanSpawnsNothing()
    {
        using var scope = ProcRunner.BeginScope();
        var poller = new LocalPoller();
        for (var i = 0; i < 3; i++)
            await poller.ScanAsync(Array.Empty<PaneProc>());

        Assert.True(scope.SpawnCount == 0,
            $"three localhost scans cost {scope.SpawnCount} subprocesses, expected 0.\n{scope.Breakdown()}");
    }

    private void Git(string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = _repo,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.WaitForExit();
    }

    /// git marks objects read-only, which defeats Directory.Delete(recursive).
    private static void DeleteTree(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        Directory.Delete(dir, true);
    }
}
