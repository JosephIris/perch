using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Perch;

/// One process that belonged to a pane at the moment it was torn down.
///
/// `StartTicks` is what makes acting on this LATER safe. Several seconds pass
/// between the snapshot and the kill, and a pid freed in that window can be
/// handed to something else — so the stamp is re-checked before anything dies:
/// a recycled pid has a different start time and is left alone. It is read
/// from the process itself rather than from the probe, because the probe fills
/// a start time only for rows that hold a listener.
///
/// 0 means "couldn't read it" (the process was already gone, or above our
/// token). Such a member is skipped rather than killed on faith.
internal sealed record JobMember(int Pid, long StartTicks, string Name);

/// What a pane's process tree looked like just before teardown.
internal sealed record JobSnapshot(Guid PaneId, IReadOnlyList<JobMember> Members)
{
    public static readonly JobSnapshot Empty = new(Guid.Empty, Array.Empty<JobMember>());
}

/// Reclaims the REST of a torn-down pane's process tree — the part the
/// pseudo-console doesn't take with it.
///
/// Measured, not assumed: one leaked agent session held four processes —
/// the pane shell (~72 MB), claude itself (~330–460 MB), and TWO bun
/// processes for a single MCP plugin server (~280–390 MB together). The MCP
/// server is as expensive as Claude. A teardown that kills claude and stops
/// there recovers barely half the memory, which is why this exists at all.
///
/// The line it must not cross is <see cref="PaneJob"/>'s deliberate design:
/// closing a pane has to leave a dev server running, because a server
/// outliving its pane is exactly what the Local panel exists to show you. So
/// the rule here is not "kill the job" — it is:
///
///     kill what the pane left behind, EXCEPT anything that is serving.
///
/// "Serving" means holding a loopback listener, or being the parent of
/// something that does (`npm` wrapping `node`) — killing that parent would
/// orphan the listener rather than tidy it. That is the same definition the
/// Local panel uses, so the two features can never disagree about what a
/// server is.
internal sealed class JobSweep
{
    private readonly ISystemProbe? _probe;
    private bool _warned;

    public JobSweep(ISystemProbe? probe) => _probe = probe;

    /// Snapshot the pane's tree. MUST be called while the pane's PTY (and so
    /// its job handle) is still alive — membership is unanswerable once the
    /// handle is closed. Runs off the UI thread: the Windows probe talks to
    /// WMI and can take a few hundred ms.
    public Task<JobSnapshot> CaptureAsync(Guid paneId, IProcScope? scope)
    {
        if (_probe == null || scope == null) return Task.FromResult(JobSnapshot.Empty);
        return Task.Run(() =>
        {
            try
            {
                var (_, procs) = _probe.Probe();
                var members = new List<JobMember>();
                foreach (var p in procs)
                {
                    if (p.Pid <= 4) continue;
                    if (!scope.ContainsPid(p.Pid)) continue;
                    var started = StartTicks(p.Pid);
                    if (started == 0) continue;   // unreadable → never a kill candidate
                    members.Add(new JobMember(p.Pid, started, p.Name));
                }
                return new JobSnapshot(paneId, members);
            }
            catch (Exception ex) { Log.Error("JobSweep.capture", ex); return JobSnapshot.Empty; }
        });
    }

    /// Kill what survived the teardown. `grace` is how long the tree gets to
    /// wind itself down first — an MCP stdio server usually exits on its own
    /// when the agent closes its pipe, and one that does costs us nothing.
    public async Task<int> ReapAsync(JobSnapshot snap, TimeSpan grace, string why)
    {
        if (snap.Members.Count == 0) return 0;
        if (_probe == null)
        {
            if (!_warned) { _warned = true; Log.Info("JobSweep", "no system probe on this host — leftover pane processes are not reclaimed"); }
            return 0;
        }
        if (grace > TimeSpan.Zero) await Task.Delay(grace).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            try
            {
                var (listeners, procs) = _probe.Probe();
                var alive = new HashSet<int>(procs.Select(p => p.Pid));
                var serving = ServingPids(listeners, procs);
                var killed = 0;
                foreach (var m in snap.Members)
                {
                    // Gone already — the common, cheap case.
                    if (!alive.Contains(m.Pid)) continue;
                    // Pid reuse guard: same number, different process. A stamp
                    // we can no longer read means the same thing — don't kill.
                    if (StartTicks(m.Pid) != m.StartTicks) continue;
                    if (serving.Contains(m.Pid))
                    {
                        Log.Info("Reap.job", $"pane={snap.PaneId:N} keeping pid={m.Pid} ({m.Name}) — it is serving a port");
                        continue;
                    }
                    if (Kill(m, snap.PaneId, why)) killed++;
                }
                if (killed > 0)
                    Log.Info("Reap.job", $"pane={snap.PaneId:N} reclaimed {killed} leftover process(es) — {why}");
                return killed;
            }
            catch (Exception ex) { Log.Error("JobSweep.reap", ex); return 0; }
        }).ConfigureAwait(false);
    }

    /// Pids that hold a loopback listener, plus every ancestor of one. An
    /// ancestor is spared because killing it would orphan the listener, which
    /// is strictly worse than leaving both.
    internal static HashSet<int> ServingPids(
        IReadOnlyList<RawListener> listeners, IReadOnlyList<RawProc> procs)
    {
        var parent = procs.ToDictionary(p => p.Pid, p => p.Ppid);
        var serving = new HashSet<int>();
        foreach (var l in listeners)
        {
            if (l.Pid <= 4) continue;
            var cur = l.Pid;
            // Depth cap: a corrupt/cyclic ppid chain must not spin forever.
            for (var hop = 0; hop < 32 && cur > 4 && serving.Add(cur); hop++)
            {
                if (!parent.TryGetValue(cur, out var next) || next == cur) break;
                cur = next;
            }
        }
        return serving;
    }

    /// The process's start time in ticks, or 0 when it can't be read (already
    /// gone, or owned by a token we can't open). Both hosts answer this from
    /// the same managed API, so the reuse guard needs no per-OS interop.
    private static long StartTicks(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.StartTime.Ticks;
        }
        catch { return 0; }
    }

    private static bool Kill(JobMember m, Guid paneId, string why)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(m.Pid);
            var rss = 0L;
            try { rss = proc.WorkingSet64 / (1024 * 1024); } catch { }
            proc.Kill(entireProcessTree: true);
            Log.Info("Reap.job", $"pane={paneId:N} killed pid={m.Pid} ({m.Name}) rss={rss}MB — {why}");
            return true;
        }
        catch (ArgumentException) { return false; }          // exited between probe and kill
        catch (InvalidOperationException) { return false; }   // ditto
        catch (Exception ex) { Log.Error($"JobSweep.kill pid={m.Pid}", ex); return false; }
    }
}
