using System;
using System.Diagnostics;
using System.IO;

namespace Perch;

/// Crash cleanup for pane children. Windows gets this from the kernel (the
/// kill-on-close job object reaps the whole tree even on a Task Manager
/// kill); macOS has no equivalent, so we do it by bookkeeping: every pty
/// spawn records its session-leader pid + the process's `ps lstart` stamp,
/// a clean dispose forgets it, and the NEXT launch kills any leftover
/// session whose pid still exists with the SAME start stamp (the stamp
/// comparison is what makes pid reuse safe — a recycled pid has a
/// different start time and is left alone).
internal static class PtyOrphans
{
    private static string Dir => Path.Combine(AppPaths.DataRoot, "perch", "ptys");

    public static void Record(int pid)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var stamp = StartStamp(pid);
            if (stamp != null) File.WriteAllText(Path.Combine(Dir, pid.ToString()), stamp);
        }
        catch (Exception ex) { Log.Error("PtyOrphans.record", ex); }
    }

    public static void Forget(int pid)
    {
        try { File.Delete(Path.Combine(Dir, pid.ToString())); } catch { }
    }

    /// Kill sessions a crashed previous run left behind. Call once at startup,
    /// before any new pane spawns.
    public static void ReapLeftovers()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var file in Directory.GetFiles(Dir))
            {
                if (!int.TryParse(Path.GetFileName(file), out var pid)) { File.Delete(file); continue; }
                try
                {
                    var recorded = File.ReadAllText(file).Trim();
                    var current = StartStamp(pid);
                    // Alive, same start time, still a session leader → ours.
                    if (current != null && current == recorded && Libc.getsid(pid) == pid)
                    {
                        Log.Info("PtyOrphans.reap", $"killing orphaned pane session pid={pid}");
                        Libc.kill(-pid, Libc.SIGKILL);
                    }
                }
                catch (Exception ex) { Log.Error("PtyOrphans.reap", ex); }
                finally { try { File.Delete(file); } catch { } }
            }
        }
        catch (Exception ex) { Log.Error("PtyOrphans.sweep", ex); }
    }

    /// The process's launch timestamp as ps prints it, or null when the pid
    /// is gone. Compared as an opaque string — no parsing needed.
    private static string? StartStamp(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/ps")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "-p", pid.ToString(), "-o", "lstart=" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var line = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(3000);
            return line.Length == 0 ? null : line;
        }
        catch { return null; }
    }
}
