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
