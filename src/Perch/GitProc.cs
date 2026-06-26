using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Perch;

/// Static helpers for the small set of `git` commands we shell out to:
/// branch detection, repo root, and commit-count-since-baseline. All async,
/// all swallow errors → null (no git, no repo, runaway process). Centralized
/// here so MainWindow stays focused on UI/IPC concerns; this file is also
/// the single place to tweak timeouts or escape rules if needed.
internal static class GitProc
{
    /// Returns the current branch name (e.g. "main"), or "(<short-sha>)"
    /// when HEAD is detached, or "" if the cwd isn't a git repo, or null
    /// on any failure (git missing, process crash, etc.).
    public static async Task<string?> BranchAsync(string cwd)
    {
        var (ok, stdout) = await RunAsync("git", "rev-parse --abbrev-ref HEAD", cwd);
        if (!ok) return null;
        var b = stdout.Trim();
        if (b != "HEAD") return b;
        // Detached HEAD — show short sha instead so the chip stays useful.
        var (_, sha) = await RunAsync("git", "rev-parse --short HEAD", cwd);
        return $"({sha.Trim()})";
    }

    /// Returns the absolute path to the repo root (`git rev-parse
    /// --show-toplevel`), or null if cwd isn't in a repo.
    public static async Task<string?> TopLevelAsync(string cwd)
    {
        var (ok, stdout) = await RunAsync("git", "rev-parse --show-toplevel", cwd);
        return ok ? stdout.Trim() : null;
    }

    /// Counts commits in HEAD that aren't reachable from <paramref name="baselineSha"/>.
    /// Used by the cc-session commit counter. Returns null if the count
    /// can't be determined.
    public static async Task<int?> CommitsSinceAsync(string baselineSha, string cwd)
    {
        if (string.IsNullOrEmpty(baselineSha)) return null;
        var (ok, stdout) = await RunAsync("git", $"rev-list {baselineSha}..HEAD --count", cwd);
        if (!ok) return null;
        return int.TryParse(stdout.Trim(), out var n) ? n : (int?)null;
    }

    /// Diff size from <paramref name="baselineSha"/> to the working tree —
    /// i.e. everything the agent has touched since session-start, committed
    /// AND uncommitted. Parses `git diff --shortstat`; any clause may be
    /// absent ("1 file changed, 5 insertions(+)" with no deletions). Returns
    /// (files, added, deleted), or null on failure.
    public static async Task<(int files, int added, int deleted)?> DiffStatsAsync(string baselineSha, string cwd)
    {
        if (string.IsNullOrEmpty(baselineSha)) return null;
        var (ok, stdout) = await RunAsync("git", $"diff --shortstat {baselineSha}", cwd);
        if (!ok) return null;
        int files = 0, added = 0, deleted = 0;
        var mF = System.Text.RegularExpressions.Regex.Match(stdout, @"(\d+) files? changed");
        if (mF.Success) int.TryParse(mF.Groups[1].Value, out files);
        var mA = System.Text.RegularExpressions.Regex.Match(stdout, @"(\d+) insertions?\(\+\)");
        if (mA.Success) int.TryParse(mA.Groups[1].Value, out added);
        var mD = System.Text.RegularExpressions.Regex.Match(stdout, @"(\d+) deletions?\(-\)");
        if (mD.Success) int.TryParse(mD.Groups[1].Value, out deleted);
        return (files, added, deleted);
    }

    /// Commits HEAD is ahead of its upstream (`@{upstream}..HEAD`) — the
    /// "↑N unpushed" signal. Returns 0 when there's no upstream tracking
    /// branch configured or nothing to push; null only on an unexpected
    /// failure. (No upstream is a normal state, not an error, so it folds
    /// to 0 rather than null.)
    public static async Task<int?> AheadAsync(string cwd)
    {
        var (ok, stdout) = await RunAsync("git", "rev-list --count @{upstream}..HEAD", cwd);
        if (!ok) return 0;
        return int.TryParse(stdout.Trim(), out var n) ? n : 0;
    }

    /// Shared process runner — captures stdout, ignores stderr, returns
    /// (success, stdout). Success means exit code 0. No timeout; git
    /// commands here are fast and we're already off the UI thread.
    private static async Task<(bool ok, string stdout)> RunAsync(string exe, string args, string cwd)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
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
}
