using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Perch;

/// The pane's session footprint: how much the agent changed since its baseline,
/// committed AND uncommitted, plus how many commits it authored.
internal readonly record struct GitSessionStats(int Files, int Added, int Deleted, int Commits);

/// One file in the pane's session footprint. Same shape as GitCommitFile, but
/// spanning the WHOLE session (authored commits + working tree + new untracked)
/// rather than a single commit.
internal sealed record GitSessionFile(string Path, int Added, int Deleted);

/// The same footprint, itemized. SessionStatsAsync always built this set and
/// then threw it away, keeping only `paths.Count` — the Inspector's Changes
/// list is that discarded detail, at zero extra git calls.
internal sealed record GitSessionDetail(
    IReadOnlyList<GitSessionFile> Files, int Added, int Deleted, int Commits);

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

    /// Reflog actions that mean "this commit was AUTHORED here" — as opposed to
    /// fetched from a remote. This is the whole trick behind not billing a
    /// `git pull` to the agent: a pulled commit enters the repo under a
    /// `pull:`/`(start)` action, never an authoring one.
    ///
    /// Two traps are encoded here, both found the hard way (see the tests):
    ///
    /// - The action is prefixed with the INVOKING COMMAND, not the operation,
    ///   so a rebasing pull logs "pull --rebase (pick): <subject>" — never
    ///   "rebase (pick)". Key on the operation in parens; never on the command.
    /// - `(start)` is excluded on purpose: it records a checkout of the
    ///   UPSTREAM tip, so counting it would fold every upstream line into the
    ///   session — the very bug this exists to prevent.
    ///
    /// Deliberately NOT here: a bare `merge` (a fast-forward `merge feature`
    /// records the branch tip, whose lines were authored elsewhere). A merge
    /// COMMIT is fine to include via `commit (merge)` — git emits no numstat
    /// rows for a merge, so it contributes 0 lines and only ticks the count.
    private static readonly Regex AuthoredHereAction = new(
        @"^commit(:| \((amend|initial|merge)\))|^(cherry-pick|revert|am)\b|\((pick|squash|fixup|reword|continue)\)",
        RegexOptions.Compiled);

    /// Shas of commits authored in this repo, per HEAD's reflog.
    ///
    /// We ask the reflog — rather than the obvious "is it on a remote?" — because
    /// remote-reachability flips the moment you `git push`: the agent's own
    /// commits land on the remote and would suddenly read as somebody else's
    /// work, zeroing the chip on push. The reflog is durable evidence of where a
    /// commit was born, and no later push, fetch, or rebase rewrites it.
    ///
    /// Null when the reflog can't be read (not a repo, or reflogs disabled) —
    /// callers then skip committed work entirely, which undercounts rather than
    /// re-inflating.
    public static async Task<IReadOnlySet<string>?> AuthoredHereAsync(string cwd, int max = 500)
    {
        var (ok, stdout) = await RunAsync("git", $"reflog show HEAD --format=%H%x09%gs -n {max}", cwd);
        if (!ok) return null;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            if (AuthoredHereAction.IsMatch(line.Substring(tab + 1)))
                set.Add(line.Substring(0, tab));
        }
        return set;
    }

    /// Everything the agent has touched since session-start — committed,
    /// uncommitted, and untracked — plus the count of commits it authored.
    ///
    /// Built additively (our commits' numstat + the working tree) rather than as
    /// one `git diff <baseline>` tree diff, because a tree diff bills the agent
    /// for whatever a `git pull` dragged in: HEAD fast-forwards past the
    /// baseline and every upstream line lands inside the range. An idle pane
    /// that only ran `git pull` wore "+100, 1 commit"; a rebasing pull read
    /// "+203" against a true +3. So:
    ///
    ///   session = Σ numstat(commits AUTHORED here, since the baseline)
    ///           + numstat(working tree vs HEAD)      // uncommitted, tracked
    ///           + untracked files new since baseline
    ///
    /// Nothing here consults a remote ref, so pushing can't move the answer, and
    /// pulled commits are excluded no matter how they arrived (fast-forward,
    /// merge, or rebase-replay). Ancestry does the rest: intersecting with
    /// `baseline..HEAD` drops both pre-session commits and shas a rebase
    /// orphaned, so a rewritten history heals itself on the next refresh.
    ///
    /// The one deviation from a tree diff: a line edited in two commits counts
    /// twice (a tree diff would net it out). That's the honest reading of "work
    /// done" for an activity chip, and it's the price of being pull-proof.
    ///
    /// <paramref name="baselineUntracked"/> is the untracked set captured when
    /// the baseline sha landed (UntrackedFilesAsync); files in it are excluded
    /// from the untracked fold-in so only files NEW since session-start count.
    /// Null means the snapshot hasn't landed (or its capture failed) — then the
    /// fold-in is skipped entirely, because counting ambient pre-existing files
    /// inflated the chip by the repo's whole untracked footprint (+90k on a repo
    /// full of old scrape output). A momentary undercount is the better failure.
    ///
    /// <paramref name="pathFilter"/> restricts every term to the given rel
    /// paths (git-style forward slashes, as numstat/ls-files report them).
    /// Used when SEVERAL agents share one working tree (projects-mode tabs
    /// without worktrees): the tree diff is the union of everyone's work, so
    /// each pane counts only the files ITS agent reported touching. Under a
    /// filter a commit ticks the count only when it touched a matching file —
    /// a shared tree's `baseline..HEAD` contains the other agents' commits
    /// too, and an unconditional count would bill them to everyone. Null (the
    /// solo-pane case) keeps the whole-tree measurement, which also catches
    /// files created by Bash commands that attribution can't see.
    ///
    /// <paramref name="workingTreeFilter"/> restricts ONLY the uncommitted
    /// tracked term (`diff HEAD`) to the given paths, independent of
    /// <paramref name="pathFilter"/>. The working tree is the one place a
    /// human's own hand-edits sit right next to the agent's, indistinguishable
    /// to git — so we scope that term to the files the agent's edit tools
    /// reported touching (git.touched) even for a SOLO pane, while committed and
    /// new-untracked work stay whole (so the agent's commits and the files its
    /// Bash commands created still count). Null falls back to pathFilter.
    public static async Task<GitSessionStats?> SessionStatsAsync(
        string baselineSha, string cwd, IReadOnlySet<string>? baselineUntracked,
        IReadOnlySet<string>? pathFilter = null, IReadOnlySet<string>? workingTreeFilter = null)
    {
        var d = await SessionDetailAsync(baselineSha, cwd, baselineUntracked, pathFilter, workingTreeFilter);
        return d == null ? null : new GitSessionStats(d.Files.Count, d.Added, d.Deleted, d.Commits);
    }

    /// The itemized form, and the actual worker — SessionStatsAsync is just this
    /// with the rows dropped. Same git commands, same accounting; the only
    /// difference is that the per-path add/delete tallies survive the return.
    public static async Task<GitSessionDetail?> SessionDetailAsync(
        string baselineSha, string cwd, IReadOnlySet<string>? baselineUntracked,
        IReadOnlySet<string>? pathFilter = null, IReadOnlySet<string>? workingTreeFilter = null)
    {
        if (string.IsNullOrEmpty(baselineSha)) return null;

        var authored = await AuthoredHereAsync(cwd);
        int added = 0, deleted = 0, commits = 0;
        // Keyed by path, so a file touched by several commits (and again in the
        // working tree) is ONE row whose counts sum — `Files.Count` is "how many
        // files did this touch", not "how many file-edits happened".
        var paths = new Dictionary<string, (int Added, int Deleted)>(StringComparer.Ordinal);
        void Bump(string path, int a, int dl)
        {
            paths.TryGetValue(path, out var cur);
            paths[path] = (cur.Added + a, cur.Deleted + dl);
        }

        // One `log` over the whole range, filtered in-process: cheaper than a
        // `show` per authored sha, and it can't blow the command-line limit.
        // quotepath=false keeps non-ASCII paths literal, matching UnpushedCommits.
        const char SOH = (char)0x01;
        var (okLog, log) = await RunAsync(
            "git", $"-c core.quotepath=false log --numstat --format={SOH}%H {baselineSha}..HEAD", cwd);
        if (okLog && authored != null)
        {
            var mine = false;
            var counted = false;   // this commit already ticked `commits` (filtered mode)
            foreach (var raw in log.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length > 0 && line[0] == SOH)
                {
                    // Ancestry has already bounded this to baseline..HEAD; the
                    // reflog set decides whether it's ours or something a pull
                    // brought in. Merge commits emit no numstat rows, so an
                    // authored merge ticks the count and adds no lines.
                    // Under a path filter the tick moves to the first MATCHING
                    // row instead — in a shared tree "authored here" includes
                    // the other agents' commits, and only the files tell whose.
                    mine = authored.Contains(line.Substring(1).Trim());
                    if (mine && pathFilter == null) commits++;
                    counted = false;
                    continue;
                }
                if (!mine || line.Length == 0) continue;
                if (TryNumstat(line, out var a, out var d, out var path))
                {
                    if (pathFilter != null && !pathFilter.Contains(path)) continue;
                    if (pathFilter != null && !counted) { commits++; counted = true; }
                    added += a; deleted += d; Bump(path, a, d);
                }
            }
        }

        // Uncommitted tracked work. `diff HEAD` (not `diff`) so staged-but-
        // uncommitted edits count too. This is the one term a human's own
        // hand-edits leak into — git can't tell them from the agent's — so it's
        // scoped to the agent's git.touched set (workingTreeFilter) when given,
        // even for a solo pane. Falls back to pathFilter (the shared-tree split)
        // when no dedicated working-tree filter is set.
        var wtFilter = workingTreeFilter ?? pathFilter;
        var (okDiff, wt) = await RunAsync("git", "-c core.quotepath=false diff --numstat HEAD", cwd);
        if (okDiff)
        {
            foreach (var raw in wt.Split('\n'))
            {
                if (TryNumstat(raw.TrimEnd('\r'), out var a, out var d, out var path))
                {
                    if (wtFilter != null && !wtFilter.Contains(path)) continue;
                    added += a; deleted += d; Bump(path, a, d);
                }
            }
        }

        // `git diff` omits untracked files entirely, but for a "what changed"
        // signal they're the bulk of some work (new scripts, context notes).
        // Fold in the ones created since the baseline snapshot as added lines
        // (no `git add -N`, which would mutate the user's index).
        var uOk = false;
        if (baselineUntracked != null)
        {
            var untracked = await UntrackedStatsAsync(cwd, baselineUntracked, pathFilter);
            uOk = untracked != null;
            foreach (var (path, lines) in untracked ?? new List<(string, int)>())
            {
                added += lines;
                Bump(path, lines, 0);
            }
        }

        // Nothing ran → not a repo (or no git). Report null so the caller leaves
        // the chip alone, rather than a misleading all-zeroes.
        if (!okLog && !okDiff && !uOk) return null;

        // Biggest churn first — the file you most need to look at is the one
        // the agent rewrote, not the one it added an import to.
        var rows = paths
            .Select(kv => new GitSessionFile(kv.Key, kv.Value.Added, kv.Value.Deleted))
            .OrderByDescending(f => f.Added + f.Deleted)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        return new GitSessionDetail(rows, added, deleted, commits);
    }

    /// A `--numstat` row: "&lt;added&gt;\t&lt;deleted&gt;\t&lt;path&gt;", with "-" for binary
    /// (folded to 0 lines, matching git's own accounting — the file still counts).
    private static bool TryNumstat(string line, out int added, out int deleted, out string path)
    {
        added = deleted = 0;
        path = "";
        int t1 = line.IndexOf('\t');
        int t2 = t1 >= 0 ? line.IndexOf('\t', t1 + 1) : -1;
        if (t1 <= 0 || t2 <= t1) return false;
        var aStr = line.Substring(0, t1);
        var dStr = line.Substring(t1 + 1, t2 - t1 - 1);
        path = line.Substring(t2 + 1);
        if (path.Length == 0) return false;
        added = aStr == "-" ? 0 : (int.TryParse(aStr, out var av) ? av : 0);
        deleted = dStr == "-" ? 0 : (int.TryParse(dStr, out var dv) ? dv : 0);
        return true;
    }

    /// The current untracked (not-ignored) file set, rel paths exactly as git
    /// reports them — captured at baseline time so DiffStatsAsync can later
    /// tell "was already lying around" from "created this session". Null when
    /// the enumeration fails (not a repo / no git).
    public static async Task<IReadOnlyList<string>?> UntrackedFilesAsync(string cwd)
    {
        var (ok, stdout) = await RunAsync(
            "git", "ls-files --others --exclude-standard -z", cwd);
        if (!ok) return null;
        return stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// Count of untracked (not-ignored) files and their total added-line count,
    /// folded into DiffStatsAsync since `git diff` never sees untracked files.
    /// Files present in <paramref name="knownAtBaseline"/> (the session-start
    /// snapshot) are skipped — they predate the session and aren't its work.
    /// Binary files (a NUL byte in the head) count as a file but 0 lines,
    /// mirroring git's own numstat. Reading files (rather than N `git diff
    /// --no-index` subprocesses or an index-mutating `add -N`) keeps it one
    /// process + local IO; a total-bytes budget bounds a stray huge/dense
    /// untracked tree so the refresh — which runs on every state change — can't
    /// stall. Returns (ok, files, added); ok=false only when the enumeration
    /// itself fails (no repo / no git).
    private static async Task<List<(string Path, int Added)>?> UntrackedStatsAsync(
        string cwd, IReadOnlySet<string> knownAtBaseline, IReadOnlySet<string>? pathFilter = null)
    {
        const int MaxFiles = 1000;                 // cap the file count we tally
        const int MaxBytesPerFile = 2 << 20;       // sample ≤2 MiB of any one file
        long budget = 48L << 20;                   // …and ≤48 MiB of IO in total

        var (ok, stdout) = await RunAsync(
            "git", "ls-files --others --exclude-standard -z", cwd);
        if (!ok) return null;

        var rels = stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var found = new List<(string, int)>();
        var buf = new byte[8192];
        foreach (var rel in rels)
        {
            if (knownAtBaseline.Contains(rel)) continue;   // predates the session
            if (pathFilter != null && !pathFilter.Contains(rel)) continue;   // another agent's file
            if (found.Count >= MaxFiles) break;
            if (budget <= 0) { found.Add((rel, 0)); continue; }   // out of IO budget: count file, skip lines
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
                if (binary) { found.Add((rel, 0)); continue; }   // counted as a file, 0 lines
                // A final line with no trailing newline still counts as one,
                // matching git numstat's no-newline-at-EOF accounting.
                if (sawAny && !lastWasNl) lines++;
                found.Add((rel, lines));
            }
            catch { found.Add((rel, 0)); }   // unreadable/vanished: still a changed file
        }
        return found;
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

    /// Full shas of the unpushed commits (`@{upstream}..HEAD`). Null when the
    /// command fails (no upstream / not a repo) — same fold as AheadAsync:
    /// nothing to push, so callers keep whatever attribution they had.
    public static async Task<IReadOnlySet<string>?> UnpushedShasAsync(string cwd)
    {
        var (ok, stdout) = await RunAsync("git", "rev-list @{upstream}..HEAD", cwd);
        if (!ok) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            set.Add(line.Trim());
        return set;
    }

    /// How many of the repo's unpushed commits this pane's recorded shas claim.
    /// The hook records the SHORT sha `git commit` printed, so match by prefix;
    /// 7+ hex chars can't collide within one repo's unpushed set in practice,
    /// and a stale claim (rebased away, already pushed) simply stops matching.
    public static int CountAttributed(IReadOnlySet<string> unpushedFull, IReadOnlyList<string> paneShas)
    {
        var n = 0;
        foreach (var full in unpushedFull)
            foreach (var mine in paneShas)
                if (mine.Length >= 7 && full.StartsWith(mine, StringComparison.OrdinalIgnoreCase))
                { n++; break; }
        return n;
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

    /// Turns a tab name into something git will accept as a branch and Windows
    /// will accept as a folder: lowercase, ASCII-ish, dashes for runs of
    /// anything else. Git refuses a lot here (spaces, "..", a trailing ".lock",
    /// leading/trailing dots or dashes), and a tab called "fix: the / bug" must
    /// not blow up the worktree create — so we normalize rather than validate.
    /// Empty (a name with no alphanumerics at all) → "" and the caller falls
    /// back; never return a slug git would reject.
    public static string Slugify(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) && ch < 128) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        // Windows reserved device names would make the worktree folder
        // uncreatable (CON, PRN, AUX, NUL, COM1..9, LPT1..9).
        if (Regex.IsMatch(slug, @"^(con|prn|aux|nul|com\d|lpt\d)$")) slug += "-tab";
        return slug.Length > 60 ? slug.Substring(0, 60).Trim('-') : slug;
    }

    /// Creates a worktree for <paramref name="branch"/> at <paramref name="path"/>,
    /// cut from the repo's current HEAD.
    ///
    /// We create the worktree OURSELVES rather than using `claude --worktree`,
    /// which exists but puts the tree at &lt;repo&gt;/.claude/worktrees/&lt;name&gt; and only
    /// moves the AGENT into it — the pane's shell (and therefore every git signal
    /// we compute, which is measured against the pane's cwd) would stay on the
    /// main checkout and read ~0 forever while the agent worked elsewhere. Owning
    /// it means the pane's cwd IS the worktree, so the loc/commit chips are true
    /// per tab with no changes to the signal code at all.
    ///
    /// Reuses an existing branch if it already exists (a tab re-created with the
    /// same name picks its work back up instead of failing). Returns null on
    /// success, else git's stderr — the caller surfaces it rather than silently
    /// dropping the user into the wrong directory.
    public static async Task<string?> WorktreeAddAsync(string repo, string path, string branch)
    {
        // Prune first: a worktree whose folder was deleted by hand still occupies
        // its name in git's registry and would make `add` fail with a stale
        // "already exists".
        await RunAsync("git", "worktree prune", repo);

        var exists = (await RunAsync("git", $"rev-parse --verify --quiet refs/heads/{branch}", repo)).ok;
        var args = exists
            ? $"worktree add \"{path}\" {branch}"          // reattach an existing branch
            : $"worktree add -b {branch} \"{path}\"";      // cut a new one from HEAD
        var (ok, _, err) = await RunWithErrAsync("git", args, repo);
        return ok ? null : (string.IsNullOrWhiteSpace(err) ? "git worktree add failed" : err.Trim());
    }

    /// Removes a worktree. The BRANCH is deliberately kept — the tab's commits
    /// are the whole point of the work and must survive closing its tab; you can
    /// still check the branch out anywhere. `--force` because an agent almost
    /// always leaves the tree dirty, and refusing to clean up a closed tab's
    /// folder over uncommitted scratch would just strand it forever.
    public static async Task<bool> WorktreeRemoveAsync(string repo, string path)
    {
        var (ok, _) = await RunAsync("git", $"worktree remove --force \"{path}\"", repo);
        if (!ok) await RunAsync("git", "worktree prune", repo);
        return ok;
    }

    /// Shared process runner — captures stdout, ignores stderr, returns
    /// (success, stdout). Success means exit code 0. No timeout; git
    /// commands here are fast and we're already off the UI thread.
    private static async Task<(bool ok, string stdout)> RunAsync(string exe, string args, string cwd)
    {
        var (ok, stdout, _) = await RunWithErrAsync(exe, args, cwd);
        return (ok, stdout);
    }

    /// Same, but keeps stderr. Creating a worktree is the one git call whose
    /// failure the USER has to see (bad path, branch already checked out
    /// elsewhere, disk full) — swallowing it would drop them into a pane sitting
    /// in the wrong directory with no explanation.
    private static async Task<(bool ok, string stdout, string stderr)> RunWithErrAsync(
        string exe, string args, string cwd)
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
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                },
            };
            if (!p.Start()) return (false, "", "");
            // Read BOTH pipes before waiting. Waiting first can deadlock: a git
            // command that writes more than the pipe buffer to stderr blocks
            // forever on the write while we block forever on the exit.
            var outT = p.StandardOutput.ReadToEndAsync();
            var errT = p.StandardError.ReadToEndAsync();
            var stdout = await outT;
            var stderr = await errT;
            await p.WaitForExitAsync();
            return (p.ExitCode == 0, stdout, stderr);
        }
        catch { return (false, "", ""); }
    }
}
