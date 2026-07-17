using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Perch;
using Xunit;

namespace Perch.Tests;

/// Attributing a dev server that OUTLIVED the process which started it.
///
/// This is the shape that defeated the old parent-pid walk, and it is the norm
/// rather than an edge case: an agent backgrounds a server in a detached
/// subshell, the wrapping shell exits a moment later, and from then on the
/// server's parent pid names a dead process. The walk dead-ends at that corpse,
/// never reaches the pane's shell, and a server Perch itself started gets filed
/// as a stranger's ("other") — then hidden outright by the "Perch only" filter.
///
/// Real processes and a real port, because the entire question is what the
/// KERNEL believes; a mocked process tree would only replay our own assumptions.
/// The pane's shell is spawned with redirected stdin rather than through ConPty
/// — ConPTY can't attach a shell under the test host — so what's covered here is
/// the mechanism (job inheritance, membership after orphaning, and the poller's
/// use of it). ConPty's own suspend/assign/resume wiring is exercised by running
/// the app.
public class PaneJobAttributionTests
{
    /// Stand-in for a pane's shell: alive, and — crucially — assigned to the job
    /// BEFORE it has spawned anything, exactly as ConPty does with a suspended
    /// process. Everything it later spawns must inherit membership.
    private static Process StartShellInJob(PaneJob job)
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        Assert.True(job.Assign(p.Handle), "could not put the pane's shell in its job");
        return p;
    }

    /// The pane's shell is alive, but the inner cmd that launched the server
    /// exits at once, orphaning it:
    ///     pane shell (alive) → cmd /c start /b (exits) → listener (orphan)
    [Fact]
    public async Task OrphanedBackgroundServerIsStillAttributedToItsPane()
    {
        var port = FreePort();
        var tmp = Path.Combine(Path.GetTempPath(), $"perch-jobtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var pidFile = Path.Combine(tmp, "server.pid");
        var script = Path.Combine(tmp, "server.ps1");
        // Holds the port and reports its own pid. Long enough to outlast the two
        // scans below; short enough to reap itself if the test dies.
        File.WriteAllText(script, $"""
            $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, {port})
            $l.Start()
            $PID | Out-File -Encoding ascii '{pidFile}'
            Start-Sleep -Seconds 180
            """);

        using var job = PaneJob.Create()!;
        Assert.NotNull(job);
        Process? shell = null;
        var serverPid = 0;
        try
        {
            shell = StartShellInJob(job);
            await shell.StandardInput.WriteLineAsync(
                $"cmd /c start /b \"\" powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\"");
            await shell.StandardInput.FlushAsync();

            serverPid = await WaitForPid(pidFile, TimeSpan.FromSeconds(60));
            Assert.True(serverPid > 0, "the background server never started");
            await WaitUntilOrphaned(serverPid, shell.Id, TimeSpan.FromSeconds(30));

            var poller = new LocalPoller();
            var withJob = new[] { new PaneProc(shell.Id, "pane1", "test-pane", "idle", job) };
            var noJob = new[] { new PaneProc(shell.Id, "pane1", "test-pane", "idle", null) };

            // The same live server, the same scan — two attribution strategies.
            var viaJob = (await poller.ScanAsync(withJob)).FirstOrDefault(l => l.Port == port);
            var viaAncestry = (await poller.ScanAsync(noJob)).FirstOrDefault(l => l.Port == port);

            // Guard: if the ancestry walk can still find this, the server isn't
            // really orphaned and the test would prove nothing.
            Assert.True(viaAncestry == null || viaAncestry.OwnerName == null,
                "expected an orphaned server to defeat the ancestry walk — the setup no longer reproduces the bug");

            // The fix: the kernel still holds it in the pane's job.
            Assert.True(viaJob != null, "the server was dropped entirely — job attribution failed");
            Assert.Equal("test-pane", viaJob!.OwnerName);
            Assert.Equal("pane1", viaJob.OwnerPaneId);
            Assert.Equal(serverPid, viaJob.Pid);
        }
        finally
        {
            if (serverPid > 0) Kill(serverPid);
            KillShell(shell);
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// Closing a pane must NOT kill its servers — a server outliving its pane is
    /// the exact thing the Local panel exists to report. Guards the deliberate
    /// absence of KILL_ON_JOB_CLOSE on the per-pane job.
    [Fact]
    public async Task DisposingThePaneJobLeavesItsServerRunning()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"perch-jobtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var pidFile = Path.Combine(tmp, "server.pid");
        var script = Path.Combine(tmp, "server.ps1");
        File.WriteAllText(script, $"""
            $PID | Out-File -Encoding ascii '{pidFile}'
            Start-Sleep -Seconds 180
            """);

        var job = PaneJob.Create()!;
        Process? shell = null;
        var serverPid = 0;
        try
        {
            shell = StartShellInJob(job);
            await shell.StandardInput.WriteLineAsync(
                $"cmd /c start /b \"\" powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\"");
            await shell.StandardInput.FlushAsync();

            serverPid = await WaitForPid(pidFile, TimeSpan.FromSeconds(60));
            Assert.True(serverPid > 0, "the background server never started");

            // The pane closes: shell dies, our handle to the job goes away.
            KillShell(shell);
            shell = null;
            job.Dispose();
            await Task.Delay(1500);

            Assert.True(Alive(serverPid),
                "closing the pane killed its background server — the per-pane job must not set KILL_ON_JOB_CLOSE");
        }
        finally
        {
            if (serverPid > 0) Kill(serverPid);
            KillShell(shell);
            job.Dispose();
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// Block until the server's parent is gone, so the ancestry walk provably
    /// has nothing left to climb.
    private static async Task WaitUntilOrphaned(int serverPid, int shellPid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var ppid = ParentPid(serverPid);
            if (ppid > 0 && ppid != shellPid && !Alive(ppid)) return;
            await Task.Delay(250);
        }
        Assert.Fail("the server's parent never exited — nothing was orphaned, so the test can't distinguish the two strategies");
    }

    private static int ParentPid(int pid)
    {
        var (code, so, _) = LocalPoller.RunAsync(
            $"(Get-CimInstance Win32_Process -Filter \"ProcessId={pid}\").ParentProcessId", 15_000, default)
            .GetAwaiter().GetResult();
        return code == 0 && int.TryParse(so.Trim(), out var ppid) ? ppid : 0;
    }

    private static async Task<int> WaitForPid(string pidFile, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidFile))
            {
                try
                {
                    if (int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) && pid > 0) return pid;
                }
                catch (IOException) { /* still being written */ }
            }
            await Task.Delay(250);
        }
        return 0;
    }

    /// A port nothing holds right now. The listener under test binds it moments
    /// later; the gap is racy in principle and irrelevant on a dev box.
    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void Kill(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
        catch { }
    }

    private static void KillShell(Process? p)
    {
        if (p == null) return;
        try { p.Kill(entireProcessTree: false); } catch { }
        try { p.Dispose(); } catch { }
    }
}
