using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Perch;

/// One file touched by a commit, with its add/delete line counts. Binary
/// files report 0/0 (git emits "-" for both, which we fold to 0).
internal sealed record GitCommitFile(string Path, int Added, int Deleted);

/// A single unpushed commit, plus whether it was made during the current
/// agent session (reachable from the session baseline). Drives the
/// "ready to push" recap surfaces.
internal sealed record GitCommit(
    string Sha,
    string ShortSha,
    string Subject,
    string CommittedIso,
    string Author,
    int Added,
    int Deleted,
    bool InSession,
    IReadOnlyList<GitCommitFile> Files);

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
        int files = 0, added = 0, deleted = 0;
        var (ok, stdout) = await RunAsync("git", $"diff --shortstat {baselineSha}", cwd);
        if (ok)
        {
            var mF = System.Text.RegularExpressions.Regex.Match(stdout, @"(\d+) files? changed");
            if (mF.Success) int.TryParse(mF.Groups[1].Value, out files);
            var mA = System.Text.RegularExpressions.Regex.Match(stdout, @"(\d+) insertions?\(\+\)");
            if (mA.Success) int.TryParse(mA.Groups[1].Value, out added);
            var mD = System.Text.RegularExpressions.Regex.Match(stdout, @"(\d+) deletions?\(-\)");
            if (mD.Success) int.TryParse(mD.Groups[1].Value, out deleted);
        }

        // `git diff` omits untracked files entirely, but for a "what changed"
        // signal they're the bulk of some work (new scripts, context notes).
        // Fold them in as added lines. Provenance is unknowable — an untracked
        // file that predates the anchor still counts — which we accept as the
        // price of a cheap, side-effect-free signal (no `git add -N`, which
        // would mutate the user's index).
        var (uOk, uFiles, uAdded) = await UntrackedStatsAsync(cwd);
        if (uOk) { files += uFiles; added += uAdded; }

        // Neither the tracked diff nor the untracked enumeration ran → not a
        // repo (or no git). Report null so the caller leaves the chip alone,
        // rather than a misleading (0,0,0). Note an unborn HEAD makes the diff
        // fail while enumeration still works, so a fresh repo's untracked files
        // (uOk) still surface.
        if (!ok && !uOk) return null;
        return (files, added, deleted);
    }

    /// Count of untracked (not-ignored) files and their total added-line count,
    /// folded into DiffStatsAsync since `git diff` never sees untracked files.
    /// Binary files (a NUL byte in the head) count as a file but 0 lines,
    /// mirroring git's own numstat. Reading files (rather than N `git diff
    /// --no-index` subprocesses or an index-mutating `add -N`) keeps it one
    /// process + local IO; a total-bytes budget bounds a stray huge/dense
    /// untracked tree so the refresh — which runs on every state change — can't
    /// stall. Returns (ok, files, added); ok=false only when the enumeration
    /// itself fails (no repo / no git).
    private static async Task<(bool ok, int files, int added)> UntrackedStatsAsync(string cwd)
    {
        const int MaxFiles = 1000;                 // cap the file count we tally
        const int MaxBytesPerFile = 2 << 20;       // sample ≤2 MiB of any one file
        long budget = 48L << 20;                   // …and ≤48 MiB of IO in total

        var (ok, stdout) = await RunAsync(
            "git", "ls-files --others --exclude-standard -z", cwd);
        if (!ok) return (false, 0, 0);

        var rels = stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        int files = 0, added = 0;
        var buf = new byte[8192];
        foreach (var rel in rels)
        {
            if (files >= MaxFiles) break;
            files++;
            if (budget <= 0) continue;             // out of IO budget: count file, skip lines
            try
            {
                var full = System.IO.Path.Combine(cwd, rel);
                using var fs = System.IO.File.OpenRead(full);
                int read, taken = 0, lines = 0;
                bool binary = false, sawAny = false, lastWasNl = false;
                while (taken < MaxBytesPerFile && budget > 0 &&
                       (read = await fs.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    taken += read; budget -= read;
                    for (int i = 0; i < read; i++)
                    {
                        var b = buf[i];
                        if (b == 0) { binary = true; break; }
                        sawAny = true;
                        lastWasNl = b == (byte)'\n';
                        if (lastWasNl) lines++;
                    }
                    if (binary) break;
                }
                if (binary) continue;              // counted as a file, 0 lines
                // A final line with no trailing newline still counts as one,
                // matching git numstat's no-newline-at-EOF accounting.
                if (sawAny && !lastWasNl) lines++;
                added += lines;
            }
            catch { /* unreadable/vanished file: still counts as a changed file */ }
        }
        return (true, files, added);
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

    /// The unpushed commits (`@{upstream}..HEAD`, newest first) with per-commit
    /// diff stats and file lists — the data behind the "↑N ready to push" recap.
    /// Each commit is tagged InSession when it's reachable from
    /// <paramref name="baselineSha"/> (i.e. made during the current agent
    /// session) so the UI can divide "this session" from "earlier unpushed".
    /// Returns null when there's no upstream / not a repo (same fold as
    /// AheadAsync — a missing upstream means nothing to push, not an error).
    /// Capped at <paramref name="max"/> commits so a long-lived branch can't
    /// produce an unbounded payload.
    public static async Task<IReadOnlyList<GitCommit>?> UnpushedCommitsAsync(
        string cwd, string baselineSha, int max = 50)
    {
        // Set of commits made this session (baseline..HEAD). Empty when no
        // baseline — then every unpushed commit lands in "earlier unpushed".
        var sessionShas = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(baselineSha))
        {
            var (okS, outS) = await RunAsync("git", $"rev-list {baselineSha}..HEAD", cwd);
            if (okS)
                foreach (var line in outS.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    sessionShas.Add(line.Trim());
        }

        // SOH marks each commit header; US separates its fields. Both are
        // control chars git never emits inside a one-line subject/author, so
        // parsing stays unambiguous without -z gymnastics. --numstat appends
        // "<added>\t<deleted>\t<path>" rows under each header. quotepath=false
        // keeps non-ASCII paths literal.
        const char SOH = (char)0x01;
        const char US = (char)0x1F;
        var fmt = $"{SOH}%H{US}%h{US}%s{US}%cI{US}%an";
        var (ok, stdout) = await RunAsync(
            "git",
            $"-c core.quotepath=false log @{{upstream}}..HEAD --numstat --format={fmt} -n {max}",
            cwd);
        if (!ok) return null;

        var commits = new List<GitCommit>();
        string? sha = null, shortSha = null, subject = null, iso = null, author = null;
        var files = new List<GitCommitFile>();
        int added = 0, deleted = 0;

        void Flush()
        {
            if (sha == null) return;
            commits.Add(new GitCommit(
                sha, shortSha ?? "", subject ?? "", iso ?? "", author ?? "",
                added, deleted, sessionShas.Contains(sha), files));
        }

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0 && line[0] == SOH)
            {
                Flush();
                files = new List<GitCommitFile>();
                added = deleted = 0;
                var parts = line.Substring(1).Split(US);
                sha      = parts.Length > 0 ? parts[0] : "";
                shortSha = parts.Length > 1 ? parts[1] : "";
                subject  = parts.Length > 2 ? parts[2] : "";
                iso      = parts.Length > 3 ? parts[3] : "";
                author   = parts.Length > 4 ? parts[4] : "";
                continue;
            }
            if (sha == null || line.Length == 0) continue;
            // numstat row: "<added>\t<deleted>\t<path>" ("-" for binary).
            int t1 = line.IndexOf('\t');
            int t2 = t1 >= 0 ? line.IndexOf('\t', t1 + 1) : -1;
            if (t1 <= 0 || t2 <= t1) continue;
            var aStr = line.Substring(0, t1);
            var dStr = line.Substring(t1 + 1, t2 - t1 - 1);
            var path = line.Substring(t2 + 1);
            int a = aStr == "-" ? 0 : (int.TryParse(aStr, out var av) ? av : 0);
            int d = dStr == "-" ? 0 : (int.TryParse(dStr, out var dv) ? dv : 0);
            added += a;
            deleted += d;
            files.Add(new GitCommitFile(path, a, d));
        }
        Flush();
        return commits;
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
                    // Git emits UTF-8 by default (i18n.logOutputEncoding); decode
                    // it as such so non-ASCII commit subjects/paths in the recap
                    // don't garble through the console's OEM codepage.
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
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
