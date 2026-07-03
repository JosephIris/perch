using System.Diagnostics;
using System.IO;
using Xunit;

namespace Perch.Tests;

// DiffStatsAsync's untracked accounting is baseline-relative: files already
// untracked when the session baseline landed are ambient clutter, not session
// work — counting them wore "+90k" on an agent that had touched nothing.
// These exercise the real git plumbing against a throwaway repo (no mocks —
// the parsing and the exclude-set plumbing are exactly what can drift), and
// no-op quietly when git isn't on PATH so the suite doesn't hard-require it.
public class GitProcTests
{
    private static async Task<(bool ok, string stdout)> Git(string args, string cwd)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = args,
                    WorkingDirectory = cwd,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            if (!p.Start()) return (false, "");
            var stdout = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode == 0, stdout);
        }
        catch { return (false, ""); }
    }

    /// Windows Directory.Delete refuses git's read-only object files; strip
    /// the attribute first. Best-effort — a leaked temp dir is harmless.
    private static void DeleteRepo(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
        catch { }
    }

    /// Throwaway repo with one committed file and one PRE-EXISTING untracked
    /// file, plus the baseline sha + untracked snapshot captured the way
    /// OnGitBaseline does at session-start. Null when git isn't available.
    private static async Task<(string dir, string baseline, IReadOnlySet<string> snapshot)?> SetupRepoAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "perch-gitproc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (!(await Git("init -q", dir)).ok) { DeleteRepo(dir); return null; }
        await Git("config user.email perch-tests@localhost", dir);
        await Git("config user.name perch-tests", dir);

        await File.WriteAllTextAsync(Path.Combine(dir, "tracked.txt"), "one\ntwo\n");
        await Git("add tracked.txt", dir);
        await Git("commit -q -m baseline", dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "preexisting.txt"), "a\nb\nc\nd\ne\n");

        var (ok, sha) = await Git("rev-parse HEAD", dir);
        if (!ok) { DeleteRepo(dir); return null; }
        var snapshot = await GitProc.UntrackedFilesAsync(dir);
        Assert.NotNull(snapshot);
        Assert.Contains("preexisting.txt", snapshot!);
        return (dir, sha.Trim(), new HashSet<string>(snapshot!, StringComparer.Ordinal));
    }

    [Fact]
    public async Task DiffStats_CountsOnlyUntrackedFilesNewSinceBaseline()
    {
        var repo = await SetupRepoAsync();
        if (repo is not (var dir, var baseline, var snapshot)) return; // no git on PATH
        try
        {
            // Session work: one tracked edit (+1 line) and one NEW untracked
            // file (3 lines). preexisting.txt (5 lines) predates the baseline
            // and must not count.
            await File.AppendAllTextAsync(Path.Combine(dir, "tracked.txt"), "three\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "newfile.txt"), "x\ny\nz\n");

            var stats = await GitProc.DiffStatsAsync(baseline, dir, snapshot);
            Assert.NotNull(stats);
            var (files, added, deleted) = stats!.Value;
            Assert.Equal(2, files);   // tracked.txt + newfile.txt, NOT preexisting.txt
            Assert.Equal(4, added);   // 1 tracked + 3 new-untracked
            Assert.Equal(0, deleted);
        }
        finally { DeleteRepo(dir); }
    }

    [Fact]
    public async Task DiffStats_NullSnapshotSkipsUntrackedEntirely()
    {
        var repo = await SetupRepoAsync();
        if (repo is not (var dir, var baseline, _)) return; // no git on PATH
        try
        {
            // Snapshot not landed yet (or its capture failed): the fold-in is
            // skipped so a mid-capture refresh can only undercount — it must
            // never re-inflate with the ambient untracked footprint.
            await File.WriteAllTextAsync(Path.Combine(dir, "newfile.txt"), "x\ny\nz\n");

            var stats = await GitProc.DiffStatsAsync(baseline, dir, null);
            Assert.NotNull(stats);
            var (files, added, deleted) = stats!.Value;
            Assert.Equal(0, files);
            Assert.Equal(0, added);
            Assert.Equal(0, deleted);
        }
        finally { DeleteRepo(dir); }
    }
}
