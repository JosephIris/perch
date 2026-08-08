using System;
using System.Collections.Concurrent;
using System.IO;

namespace Perch;

/// Watches a working tree and says "something changed" — debounced.
///
/// This is the half of the refresh gate that .git cannot provide. The
/// fingerprint in GitProc sees commits, checkouts, fetches and branch switches,
/// because those all rewrite something under .git. It is blind to the working
/// tree, and the working tree is where two things that MUST be seen happen:
/// an agent's Bash command creating files (no git.touched IPC is emitted for
/// those, by design — attribution can't see them) and any edit that changes how
/// much a tracked file differs from HEAD.
///
/// Without this, gating the refresh would have traded a performance bug for a
/// correctness one: the footer's loc would sit stale until the next commit.
///
/// Deliberately coarse. It does not care WHAT changed, only that something did,
/// because the consumer's answer to that is "drop the cached signature" either
/// way. Coarse also means cheap: no path list to maintain, no ordering to get
/// wrong.
internal sealed class RepoWatcher : IDisposable
{
    /// Long enough that a build writing a thousand files fires once, short
    /// enough that a save feels instant. The old timer refreshed at 1Hz, so
    /// anything at or under a second is not a regression in responsiveness.
    private const int DebounceMs = 400;

    private readonly FileSystemWatcher _fsw;
    private readonly System.Timers.Timer _debounce;
    private readonly Action _onChanged;

    public RepoWatcher(string root, Action onChanged)
    {
        _onChanged = onChanged;

        _debounce = new System.Timers.Timer(DebounceMs) { AutoReset = false };
        _debounce.Elapsed += (_, _) => { try { _onChanged(); } catch { } };

        _fsw = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            // Size covers a file being written; LastWrite covers a save that
            // keeps the length. CreationTime/FileName cover add and rename.
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size,
            // The default 8KB buffer overflows on a busy tree (npm install, a
            // build) and an overflow drops events silently — exactly the case
            // where staleness would be most visible.
            InternalBufferSize = 64 * 1024,
        };

        _fsw.Changed += OnAny;
        _fsw.Created += OnAny;
        _fsw.Deleted += OnAny;
        _fsw.Renamed += OnAny;
        // An overflow means "you missed some" — the only safe reading of that
        // is that something changed, so treat it as a change rather than
        // ignoring it.
        _fsw.Error += (_, _) => Kick();

        _fsw.EnableRaisingEvents = true;
    }

    private void OnAny(object sender, FileSystemEventArgs e)
    {
        // .git churn is the fingerprint's job, and git rewrites index/lock files
        // constantly — forwarding those would make the debounce useless. The
        // check is on the segment, not a substring, so a file legitimately
        // named ".gitignore" or a directory "src/.github" isn't caught.
        if (IsUnderGitDir(e.FullPath)) return;
        Kick();
    }

    private static bool IsUnderGitDir(string path)
    {
        foreach (var seg in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (seg.Equals(".git", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void Kick()
    {
        try { _debounce.Stop(); _debounce.Start(); } catch { }
    }

    public void Dispose()
    {
        try { _fsw.EnableRaisingEvents = false; } catch { }
        try { _fsw.Dispose(); } catch { }
        try { _debounce.Dispose(); } catch { }
    }
}

/// One watcher per distinct working tree, shared by every pane sitting in it.
/// Panes come and go far more often than repos do, and a FileSystemWatcher per
/// pane on the same tree would multiply the event traffic for nothing.
internal sealed class RepoWatchers : IDisposable
{
    private readonly ConcurrentDictionary<string, RepoWatcher> _byRoot =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _onChanged;

    public RepoWatchers(Action<string> onChanged) => _onChanged = onChanged;

    public void Ensure(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        _byRoot.GetOrAdd(root, r =>
        {
            try { return new RepoWatcher(r, () => _onChanged(r)); }
            catch (Exception ex)
            {
                // A tree on a filesystem that can't watch (some network shares)
                // must not take the app down; those panes fall back to
                // refreshing on .git events alone.
                Log.Error("RepoWatchers.Ensure", ex);
                return null!;
            }
        });
    }

    public void Dispose()
    {
        foreach (var w in _byRoot.Values) w?.Dispose();
        _byRoot.Clear();
    }
}
