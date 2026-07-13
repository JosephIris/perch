using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace Perch.Tests;

// SessionStatsAsync answers "what did the AGENT change since session-start",
// and both halves of that have drawn blood:
//
//  - Untracked accounting is baseline-relative: files already untracked when the
//    baseline landed are ambient clutter, not session work — counting them wore
//    "+90k" on an agent that had touched nothing.
//  - It must not bill the agent for a `git pull`. The old tree diff
//    (`git diff <baseline>`) did exactly that: HEAD fast-forwards past the
//    baseline and every upstream line lands in the range. An idle pane that only
//    pulled read "+100, 1 commit"; a rebasing pull read "+203" against a true +3.
//
// These exercise the real git plumbing against throwaway repos (no mocks — the
// reflog action strings, the numstat parsing, and the exclude-set plumbing are
// exactly what can drift), and no-op quietly when git isn't on PATH so the suite
// doesn't hard-require it.
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

    /// A throwaway origin + working clone + a second clone standing in for a
    /// teammate, so the pull/push shapes can be driven for real. `work` starts
    /// with one commit pushed to origin/main; `baseline` is HEAD at that point,
    /// exactly as the session-start hook captures it. Null when git isn't there.
    private static async Task<(string root, string work, string other, string baseline)?> SetupClonesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "perch-gitremote-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var origin = Path.Combine(root, "origin.git");
        var work = Path.Combine(root, "work");
        var other = Path.Combine(root, "other");

        if (!(await Git($"init -q --bare -b main \"{origin}\"", root)).ok) { DeleteRepo(root); return null; }
        if (!(await Git($"clone -q \"{origin}\" \"{work}\"", root)).ok) { DeleteRepo(root); return null; }
        await Git("config user.email perch-tests@localhost", work);
        await Git("config user.name perch-tests", work);

        await File.WriteAllTextAsync(Path.Combine(work, "tracked.txt"), "one\ntwo\n");
        await Git("add -A", work);
        await Git("commit -q -m init", work);
        if (!(await Git("push -q -u origin main", work)).ok) { DeleteRepo(root); return null; }

        if (!(await Git($"clone -q \"{origin}\" \"{other}\"", root)).ok) { DeleteRepo(root); return null; }
        await Git("config user.email teammate@localhost", other);
        await Git("config user.name teammate", other);

        var (ok, sha) = await Git("rev-parse HEAD", work);
        if (!ok) { DeleteRepo(root); return null; }
        return (root, work, other, sha.Trim());
    }

    /// The teammate pushes a file of <paramref name="lines"/> lines to origin/main.
    private static async Task TeammatePushAsync(string other, string name, int lines)
    {
        await Git("pull -q", other);
        await File.WriteAllTextAsync(
            Path.Combine(other, name),
            string.Concat(Enumerable.Range(1, lines).Select(i => i + "\n")));
        await Git("add -A", other);
        await Git($"commit -q -m {name}", other);
        await Git("push -q origin main", other);
    }

    /// Untracked snapshot the way OnGitBaseline takes it when the baseline lands.
    private static async Task<IReadOnlySet<string>> SnapshotAsync(string dir) =>
        new HashSet<string>(await GitProc.UntrackedFilesAsync(dir) ?? Array.Empty<string>(),
                            StringComparer.Ordinal);

    [Fact]
    public async Task SessionStats_CountsOnlyUntrackedFilesNewSinceBaseline()
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

            var stats = await GitProc.SessionStatsAsync(baseline, dir, snapshot);
            Assert.NotNull(stats);
            Assert.Equal(2, stats!.Value.Files);   // tracked.txt + newfile.txt, NOT preexisting.txt
            Assert.Equal(4, stats.Value.Added);    // 1 tracked + 3 new-untracked
            Assert.Equal(0, stats.Value.Deleted);
        }
        finally { DeleteRepo(dir); }
    }

    [Fact]
    public async Task SessionStats_NullSnapshotSkipsUntrackedEntirely()
    {
        var repo = await SetupRepoAsync();
        if (repo is not (var dir, var baseline, _)) return; // no git on PATH
        try
        {
            // Snapshot not landed yet (or its capture failed): the fold-in is
            // skipped so a mid-capture refresh can only undercount — it must
            // never re-inflate with the ambient untracked footprint.
            await File.WriteAllTextAsync(Path.Combine(dir, "newfile.txt"), "x\ny\nz\n");

            var stats = await GitProc.SessionStatsAsync(baseline, dir, null);
            Assert.NotNull(stats);
            Assert.Equal(0, stats!.Value.Files);
            Assert.Equal(0, stats.Value.Added);
            Assert.Equal(0, stats.Value.Deleted);
        }
        finally { DeleteRepo(dir); }
    }

    /// THE bug: an agent that touched nothing and only ran `git pull` must not
    /// wear the 100 lines the pull brought in. The old tree diff read
    /// "+100, 1 commit" here.
    [Fact]
    public async Task SessionStats_FastForwardPullIsNotSessionWork()
    {
        var repo = await SetupClonesAsync();
        if (repo is not (var root, var work, var other, var baseline)) return; // no git on PATH
        try
        {
            var snapshot = await SnapshotAsync(work);
            await TeammatePushAsync(other, "theirs.txt", 100);
            Assert.True((await Git("pull -q --ff-only", work)).ok);

            var stats = await GitProc.SessionStatsAsync(baseline, work, snapshot);
            Assert.NotNull(stats);
            Assert.Equal(0, stats!.Value.Added);
            Assert.Equal(0, stats.Value.Deleted);
            Assert.Equal(0, stats.Value.Files);
            Assert.Equal(0, stats.Value.Commits);
        }
        finally { DeleteRepo(root); }
    }

    /// A pull that MERGES upstream work on top of the agent's own commit keeps
    /// only the agent's lines. (Old tree diff: +152.)
    [Fact]
    public async Task SessionStats_MergingPullKeepsOnlyOurLines()
    {
        var repo = await SetupClonesAsync();
        if (repo is not (var root, var work, var other, var baseline)) return; // no git on PATH
        try
        {
            var snapshot = await SnapshotAsync(work);
            await File.AppendAllTextAsync(Path.Combine(work, "tracked.txt"), "agent-line\n");
            await Git("add -A", work);
            await Git("commit -q -m \"agent work\"", work);

            await TeammatePushAsync(other, "theirs.txt", 100);
            Assert.True((await Git("-c pull.rebase=false pull -q --no-edit", work)).ok);

            var stats = await GitProc.SessionStatsAsync(baseline, work, snapshot);
            Assert.NotNull(stats);
            Assert.Equal(1, stats!.Value.Added);     // ours only — none of the 100
            Assert.Equal(1, stats.Value.Files);
            Assert.Equal(1, stats.Value.Commits);    // the merge commit adds no lines
        }
        finally { DeleteRepo(root); }
    }

    /// A rebasing pull replays our commits under NEW shas, with a reflog action
    /// named after the invoking command ("pull --rebase (pick):", never
    /// "rebase (pick):"). Miss that and the replayed work vanishes; count the
    /// "(start)" entry that precedes it and all 30 upstream lines come back.
    [Fact]
    public async Task SessionStats_RebasingPullKeepsReplayedWork()
    {
        var repo = await SetupClonesAsync();
        if (repo is not (var root, var work, var other, var baseline)) return; // no git on PATH
        try
        {
            var snapshot = await SnapshotAsync(work);
            await File.AppendAllTextAsync(Path.Combine(work, "tracked.txt"), "agent-line\n");
            await Git("add -A", work);
            await Git("commit -q -m \"agent work\"", work);

            await TeammatePushAsync(other, "theirs.txt", 30);
            Assert.True((await Git("-c pull.rebase=true pull -q", work)).ok);

            var stats = await GitProc.SessionStatsAsync(baseline, work, snapshot);
            Assert.NotNull(stats);
            Assert.Equal(1, stats!.Value.Added);     // the replayed commit, not the 30
            Assert.Equal(1, stats.Value.Commits);
        }
        finally { DeleteRepo(root); }
    }

    /// Two agents in ONE working tree (projects-mode tabs without worktrees):
    /// the whole-tree diff is the union of both agents' work, so each pane's
    /// stats are filtered to the files ITS agent reported touching. The filter
    /// must bound every term — uncommitted tracked edits, new untracked files,
    /// and commits (a shared tree's baseline..HEAD holds the OTHER agent's
    /// commits too, and they're all "authored here" by reflog).
    [Fact]
    public async Task SessionStats_PathFilterSplitsASharedTree()
    {
        var repo = await SetupRepoAsync();
        if (repo is not (var dir, var baseline, var snapshot)) return; // no git on PATH
        try
        {
            // Agent A edits tracked.txt (+1) and creates mine.txt (2 lines).
            // Agent B creates theirs.txt (4 lines) and COMMITS other.txt (+3).
            await File.AppendAllTextAsync(Path.Combine(dir, "tracked.txt"), "three\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "mine.txt"), "m1\nm2\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "theirs.txt"), "t1\nt2\nt3\nt4\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "other.txt"), "o1\no2\no3\n");
            await Git("add other.txt", dir);
            await Git("commit -q -m \"agent B work\"", dir);

            var mineOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "tracked.txt", "mine.txt" };
            var stats = await GitProc.SessionStatsAsync(baseline, dir, snapshot, mineOnly);
            Assert.NotNull(stats);
            Assert.Equal(2, stats!.Value.Files);    // tracked.txt + mine.txt only
            Assert.Equal(3, stats.Value.Added);     // 1 tracked + 2 untracked; none of B's 7
            Assert.Equal(0, stats.Value.Deleted);
            Assert.Equal(0, stats.Value.Commits);   // B's commit touched none of A's files

            // And the unfiltered reading still sees everything — the solo-pane
            // measurement is unchanged.
            var union = await GitProc.SessionStatsAsync(baseline, dir, snapshot);
            Assert.NotNull(union);
            Assert.Equal(10, union!.Value.Added);   // 1 + 2 + 4 + 3
            Assert.Equal(1, union.Value.Commits);
        }
        finally { DeleteRepo(dir); }
    }

    /// Under a path filter a commit counts only when it touched one of the
    /// pane's own files — that's what keeps agent B's commits out of A's
    /// commit chip in a shared tree.
    [Fact]
    public async Task SessionStats_PathFilterCountsOnlyCommitsTouchingOwnFiles()
    {
        var repo = await SetupRepoAsync();
        if (repo is not (var dir, var baseline, var snapshot)) return; // no git on PATH
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "mine.txt"), "m1\nm2\n");
            await Git("add mine.txt", dir);
            await Git("commit -q -m \"agent A commit\"", dir);

            var mineOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mine.txt" };
            var stats = await GitProc.SessionStatsAsync(baseline, dir, snapshot, mineOnly);
            Assert.NotNull(stats);
            Assert.Equal(1, stats!.Value.Commits);
            Assert.Equal(2, stats.Value.Added);
            Assert.Equal(1, stats.Value.Files);
        }
        finally { DeleteRepo(dir); }
    }

    /// Pushing must not erase the session: the agent's commits become reachable
    /// from the remote, so any "is it on a remote?" test would flip them to
    /// someone else's work and zero the chip. The reflog doesn't flip.
    [Fact]
    public async Task SessionStats_PushDoesNotZeroTheSession()
    {
        var repo = await SetupClonesAsync();
        if (repo is not (var root, var work, var other, var baseline)) return; // no git on PATH
        try
        {
            var snapshot = await SnapshotAsync(work);
            await File.AppendAllTextAsync(Path.Combine(work, "tracked.txt"), "agent-line\n");
            await Git("add -A", work);
            await Git("commit -q -m \"agent work\"", work);
            Assert.True((await Git("push -q origin main", work)).ok);

            var stats = await GitProc.SessionStatsAsync(baseline, work, snapshot);
            Assert.NotNull(stats);
            Assert.Equal(1, stats!.Value.Added);
            Assert.Equal(1, stats.Value.Commits);
        }
        finally { DeleteRepo(root); }
    }
}
