using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Perch;

/// The macOS localhost-panel probe: lsof for loopback listeners, ps for the
/// process tree + command lines. Subprocesses rather than syscalls — the
/// panel polls at 30s (3s while open), so two cheap spawns per tick is fine
/// on mac (the Windows in-process rewrite was motivated by AV-scan spawn
/// costs that don't exist here).
internal sealed class MacSystemProbe : ISystemProbe
{
    public (IReadOnlyList<RawListener> Listeners, IReadOnlyList<RawProc> Procs) Probe()
    {
        var listeners = new List<RawListener>();
        var procs = new List<RawProc>();
        try
        {
            // -F pn → machine-readable: "p<pid>" then "n<addr>:<port>" lines.
            var lsof = Run("/usr/sbin/lsof", "-nP", "-iTCP", "-sTCP:LISTEN", "-Fpn");
            var pid = 0;
            foreach (var line in lsof.Split('\n'))
            {
                if (line.Length < 2) continue;
                if (line[0] == 'p') { int.TryParse(line.AsSpan(1), out pid); continue; }
                if (line[0] != 'n') continue;
                var addr = line.Substring(1);
                var colon = addr.LastIndexOf(':');
                if (colon < 0 || !int.TryParse(addr.AsSpan(colon + 1), out var port)) continue;
                var host = addr.Substring(0, colon);
                // Loopback + wildcard binds only — same scope as the Windows
                // listener table walk.
                if (host is not ("127.0.0.1" or "[::1]" or "*" or "localhost")) continue;
                listeners.Add(new RawListener(port, pid, host == "[::1]" ? "::1" : "127.0.0.1"));
            }

            var ps = Run("/bin/ps", "-axo", "pid=,ppid=,comm=,args=");
            foreach (var line in ps.Split('\n'))
            {
                var s = line.AsSpan().TrimStart();
                if (s.Length == 0) continue;
                if (!SplitInt(ref s, out var p)) continue;
                if (!SplitInt(ref s, out var pp)) continue;
                var rest = s.ToString();
                var sp = rest.IndexOf(' ');
                var comm = sp < 0 ? rest : rest.Substring(0, sp);
                var args = sp < 0 ? "" : rest.Substring(sp + 1).Trim();
                procs.Add(new RawProc(p, pp, Path.GetFileName(comm), args, 0));
            }
        }
        catch (Exception ex) { Log.Error("MacSystemProbe", ex); }
        return (listeners, procs);
    }

    private static bool SplitInt(ref ReadOnlySpan<char> s, out int value)
    {
        s = s.TrimStart();
        var end = 0;
        while (end < s.Length && char.IsDigit(s[end])) end++;
        var ok = int.TryParse(s[..end], out value);
        s = s[end..];
        return ok && end > 0;
    }

    private static string Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(5000);
        return stdout;
    }
}
