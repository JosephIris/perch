using System;
using System.Runtime.InteropServices;

namespace Perch;

/// A per-pane Windows job object — the durable answer to "which pane started
/// this process?".
///
/// Attribution used to walk the parent-pid chain from a listener up to the
/// pane's shell, but that chain is only as strong as its weakest link: every
/// ancestor has to still be alive. Agents routinely background a dev server
/// (`(python app.py --port 5106 &)`) and the wrapping `bash -c` exits moments
/// later, orphaning the server — its ppid now names a dead pid, the walk
/// dead-ends short of the pane, and the server lands in "other". It was ours;
/// we just couldn't prove it.
///
/// The kernel tracks job membership instead. A child inherits its parent's job
/// automatically, so everything a pane ever spawns — however deeply nested,
/// however orphaned — stays a member for life. Membership survives both the
/// death of intermediate processes and pid reuse, neither of which the pid walk
/// can withstand.
///
/// Crucially this job sets NO limit flags, and in particular NOT
/// KILL_ON_JOB_CLOSE: closing a pane has to leave its servers running, because
/// a server outliving its pane is the exact thing the Local panel exists to
/// show you. The app-wide job in <see cref="JobObjectGuard"/> does set
/// kill-on-close and this one nests inside it (Win8+ allows nesting), so Perch
/// exiting still reaps everything.
internal sealed class PaneJob : IDisposable, IProcScope
{
    // Guards _job against the scan racing a pane closing: Contains runs on the
    // poller's thread while Dispose runs on the UI thread, and using a handle
    // that CloseHandle just freed could query a recycled one. The lock is held
    // for a single syscall, so contention is nil.
    private readonly object _gate = new();
    private IntPtr _job;

    private PaneJob(IntPtr job) => _job = job;

    /// Null when the OS won't hand us a job. Attribution then degrades to the
    /// pid walk rather than the pane failing to spawn.
    public static PaneJob? Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job != IntPtr.Zero) return new PaneJob(job);
        Log.Info($"PaneJob: CreateJobObject failed ({Marshal.GetLastWin32Error()})");
        return null;
    }

    /// Put the pane's shell in the job. Everything it spawns from here on
    /// inherits membership — that inheritance is the whole point.
    public bool Assign(IntPtr hProcess)
    {
        lock (_gate)
        {
            if (_job == IntPtr.Zero) return false;
            if (AssignProcessToJobObject(_job, hProcess)) return true;
            Log.Info($"PaneJob: AssignProcessToJobObject failed ({Marshal.GetLastWin32Error()})");
            return false;
        }
    }

    /// Is this process one of the pane's, at any depth? Takes an already-open
    /// handle so a caller testing one pid against many panes pays for one
    /// OpenProcess, not one per pane.
    public bool Contains(IntPtr hProcess)
    {
        if (hProcess == IntPtr.Zero) return false;
        lock (_gate)
        {
            if (_job == IntPtr.Zero) return false;
            return IsProcessInJob(hProcess, _job, out var inJob) && inJob;
        }
    }

    /// A query-only handle for <see cref="Contains"/>. IntPtr.Zero when the
    /// process has already exited or sits above our token — both just mean
    /// "not attributable", never an error.
    public static IntPtr OpenForQuery(int pid)
    {
        if (pid <= 4) return IntPtr.Zero;
        return OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
    }

    /// IProcScope: open the pid, test membership, close. One handle per
    /// query — the listener count is small enough that this stays cheap.
    public bool ContainsPid(int pid)
    {
        var h = OpenForQuery(pid);
        if (h == IntPtr.Zero) return false;
        try { return Contains(h); }
        finally { CloseQuery(h); }
    }

    public static void CloseQuery(IntPtr hProcess)
    {
        if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
    }

    /// Releases our handle only. With no kill-on-close flag the job's processes
    /// keep running — deliberately, so they can be found lingering afterwards.
    public void Dispose()
    {
        lock (_gate)
        {
            if (_job == IntPtr.Zero) return;
            try { CloseHandle(_job); } catch { }
            _job = IntPtr.Zero;
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr hProcess, IntPtr hJob, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
}
