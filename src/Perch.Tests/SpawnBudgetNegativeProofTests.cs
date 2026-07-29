using System;
using System.IO;
using System.Threading.Tasks;
using Perch;
using Xunit;

namespace Perch.Tests;

/// A budget test that cannot fail is decoration. This one puts the old
/// behaviour back — an uncached, timer-style refresh — and asserts the budget
/// REJECTS it. If someone later removes the cache, SpawnBudgetTests goes red;
/// this proves that in the same run rather than asking anyone to take it on
/// faith.
public class SpawnBudgetNegativeProofTests : IDisposable
{
    private readonly string _repo;

    public SpawnBudgetNegativeProofTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "perch-neg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repo);
        Run("init -q");
        Run("config user.email t@t.t");
        Run("config user.name t");
        Run("config commit.gpgsign false");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "hello\n");
        Run("add -A");
        Run("commit -q -m first");
    }

    public void Dispose()
    {
        GitProc.NegativeProofDisableCache = false;
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            Directory.Delete(_repo, true);
        }
        catch { }
    }

    [Fact]
    public async Task WithoutTheCacheAnUnchangedRepoStillSpawns()
    {
        GitProc.ClearAllCaches();
        GitProc.NegativeProofDisableCache = true;
        try
        {
            await GitProc.StatusAsync(_repo);      // would prime a cache, if one applied

            using var scope = ProcRunner.BeginScope();
            await GitProc.StatusAsync(_repo);

            Assert.True(scope.SpawnCount > 0,
                "with the cache disabled a repeat refresh must still shell out — " +
                "if this is 0 the budget test is measuring nothing");
        }
        finally { GitProc.NegativeProofDisableCache = false; }
    }

    [Fact]
    public async Task WithTheCacheTheSameSequenceSpawnsNothing()
    {
        GitProc.ClearAllCaches();
        GitProc.NegativeProofDisableCache = false;

        await GitProc.StatusAsync(_repo);

        using var scope = ProcRunner.BeginScope();
        await GitProc.StatusAsync(_repo);

        Assert.Equal(0, scope.SpawnCount);
    }

    private void Run(string args)
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
}
