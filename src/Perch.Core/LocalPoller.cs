using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Perch;

/// A live pane's root shell process, snapshotted on the UI thread and handed to
/// the poller so attribution never has to read window state off-thread.
///
/// Two ways to own a server, tried in that order. Job is the pane's job object
/// and the authoritative one: the kernel tracks membership, so it holds even
/// after the process that spawned the server has exited. Pid is the ConPty
/// child, used by the legacy ancestry walk as a fallback for panes we never got
/// a job for.
internal sealed record PaneProc(int Pid, string PaneId, string Name, string? State, IProcScope? Scope = null);

/// One loopback listener the scan found, already attributed to a live pane (or
/// not). The controller layers the ledger on top to decide lingering-vs-other;
/// here OwnerName != null means "a still-open pane owns this".
internal sealed record LocalListener(
    int Port,
    int Pid,
    string Addr,
    string ProcName,
    string Command,
    string Framework,
    long StartedUnixMs,
    string? OwnerPaneId,
    string? OwnerName,
    string? OwnerState);

/// Enumerates loopback TCP listeners and attributes each to the pane that spawned
/// it. Unlike the cloud poller there's nothing to authenticate and nothing to
/// bill — reading the listener table is cheap and always available on Windows —
/// so this feature is always on, but still invisible until something is actually
/// listening.
///
/// The I/O is in-process (see WindowsSystemProbe): iphlpapi for the listener
/// table, Toolhelp32 for the process tree, and a pid-filtered WMI query for the
/// few command lines framework detection needs. It previously shelled out to
/// `powershell.exe -EncodedCommand` every 3 seconds while the panel was open,
/// which cost ~200-300ms of CPU in interpreter startup before doing any work and
/// made this the most expensive spawn in the app.
///
/// Every layer degrades to "no servers" rather than throwing into the UI.
internal sealed class LocalPoller
{
    private readonly ISystemProbe _probe;

    public LocalPoller(ISystemProbe probe) => _probe = probe;

    /// Runtimes a dev server actually runs on. Used ONLY to keep the "other"
    /// bucket (servers Perch didn't launch) about dev work instead of a wall of
    /// svchost loopback listeners. A server attributed to a pane is shown
    /// regardless — it's yours by definition.
    private static readonly HashSet<string> DevRuntimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "deno", "bun", "python", "pythonw", "ruby", "php", "dotnet",
        "java", "javaw", "caddy", "nginx", "hugo", "gunicorn", "uvicorn",
        "puma", "rackup", "ng", "next", "vite", "webpack", "npm", "pnpm",
        "yarn", "esbuild", "http-server", "live-server", "serve",
    };

    /// Null means "the scan itself failed" (subprocess error, timeout, bad
    /// JSON) — distinct from an empty list, which means "scanned fine, nothing
    /// is listening". The controller keeps its last good state on null instead
    /// of wrongly clearing every pane's ports.
    public async Task<IReadOnlyList<LocalListener>?> ScanAsync(
        IReadOnlyList<PaneProc> panes, CancellationToken ct = default)
    {
        try
        {
            // The probe is syscalls plus one small WMI query — fast, but not
            // free, and the UI thread is the one thing that must never wait on
            // it. Off-thread keeps the old subprocess's threading contract.
            var (listeners, procs) = await Task.Run(() => _probe.Probe(), ct);
            return Build(listeners, procs, panes);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log.Error("LocalPoller.Scan", ex); return null; }
    }

    /// Replace raw C0 control chars (minus the JSON-legal whitespace \t \r \n)
    /// with spaces. Inside JSON string values these are always invalid, and the
    /// structural characters JSON actually uses are all printable, so this is
    /// safe on well-formed output and healing on corrupt output.
    internal static string StripControlChars(string s)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c < 0x20 && c != '\t' && c != '\r' && c != '\n' || c == 0x7F)
            {
                sb ??= new StringBuilder(s);
                sb[i] = ' ';
            }
        }
        return sb?.ToString() ?? s;
    }

    /// Attribution proper: raw kernel facts in, panel rows out. Pure and
    /// synchronous, which is what makes it testable — the probe supplies the
    /// facts, so a fixture can stand in for a live machine.
    internal IReadOnlyList<LocalListener> Build(
        IReadOnlyList<RawListener> listeners,
        IReadOnlyList<RawProc> procs,
        IReadOnlyList<PaneProc> panes)
    {
        var byPid = new Dictionary<int, RawProc>();
        foreach (var p in procs) if (p.Pid > 0) byPid[p.Pid] = p;

        // pid → owning pane. Built with the indexer (not ToDictionary) so a
        // duplicated pid — impossible in practice, but cheap to be safe about —
        // can't throw the whole scan away.
        var paneByPid = new Dictionary<int, PaneProc>();
        foreach (var p in panes) paneByPid[p.Pid] = p;

        var result = new List<LocalListener>();
        var seen = new HashSet<(int, int)>();

        foreach (var l in listeners)
        {
            if (l.Port <= 0 || l.Pid <= 4) continue;          // 0/4 = System / Idle
            // A dev server binds both 127.0.0.1 and ::1 → two rows, one pid.
            // Collapse to a single row keyed by (port, pid).
            if (!seen.Add((l.Port, l.Pid))) continue;

            byPid.TryGetValue(l.Pid, out var proc);
            var name = BaseName(proc?.Name ?? "");

            var owner = FindOwner(l.Pid, byPid, paneByPid, panes);
            // System loopback noise (svchost, etc.) that no pane owns and no
            // dev runtime explains is dropped — the panel is about dev
            // servers, not every socket on the box.
            if (owner == null && !IsDevRuntime(name)) continue;

            var (framework, command) = Describe(proc, name);
            result.Add(new LocalListener(
                Port: l.Port,
                Pid: l.Pid,
                Addr: string.IsNullOrEmpty(l.Addr) ? "127.0.0.1" : l.Addr,
                ProcName: name,
                Command: command,
                Framework: framework,
                StartedUnixMs: proc?.StartMs ?? 0,
                OwnerPaneId: owner?.PaneId,
                OwnerName: owner?.Name,
                OwnerState: owner?.State));
        }

        return result;
    }

    /// Which live pane, if any, does this listener belong to?
    private static PaneProc? FindOwner(
        int pid, Dictionary<int, RawProc> procs, Dictionary<int, PaneProc> paneByPid,
        IReadOnlyList<PaneProc> panes)
        => FindOwnerByJob(pid, panes) ?? FindOwnerByAncestry(pid, procs, paneByPid);

    /// Ask the kernel. Job membership is inherited by every descendant and
    /// outlives the processes in between, so this still resolves a dev server
    /// the agent backgrounded and whose parent shell exited seconds later —
    /// precisely the case the ancestry walk below loses. One process handle,
    /// tested against each pane's job.
    private static PaneProc? FindOwnerByJob(int pid, IReadOnlyList<PaneProc> panes)
    {
        foreach (var p in panes)
        {
            try { if (p.Scope?.ContainsPid(pid) == true) return p; }
            catch { }
        }
        return null;
    }

    /// Fallback: walk the listener's process ancestry until it hits a live
    /// pane's root pid. Only reachable for a pane whose job we never got, since
    /// a job answers first. Bounded and cycle-guarded — a corrupt ppid chain
    /// must not spin — and it gives up the moment an ancestor is missing,
    /// because a dead pid ends the chain.
    private static PaneProc? FindOwnerByAncestry(int pid, Dictionary<int, RawProc> procs, Dictionary<int, PaneProc> paneByPid)
    {
        var visited = new HashSet<int>();
        var cur = pid;
        for (var hops = 0; hops < 32 && cur > 4 && visited.Add(cur); hops++)
        {
            if (paneByPid.TryGetValue(cur, out var pane)) return pane;
            if (!procs.TryGetValue(cur, out var row)) break;
            cur = row.Ppid;
        }
        return null;
    }

    private static bool IsDevRuntime(string baseName)
        => DevRuntimes.Contains(baseName)
           || baseName.StartsWith("python", StringComparison.OrdinalIgnoreCase)
           || baseName.StartsWith("node", StringComparison.OrdinalIgnoreCase);

    /// (framework tag, cleaned command). The framework is a best-effort read of
    /// the command line — the port and command carry the real weight, so a miss
    /// just falls back to the runtime name rather than lying.
    private static (string Framework, string Command) Describe(RawProc? proc, string baseName)
    {
        var cmd = proc?.Cmd ?? "";
        var lc = cmd.ToLowerInvariant();

        string fw =
            Has(lc, "vite") ? "Vite" :
            Has(lc, "next") ? "Next" :
            Has(lc, "nuxt") ? "Nuxt" :
            Has(lc, "react-scripts") ? "CRA" :
            Has(lc, "webpack") ? "Webpack" :
            Has(lc, "@angular") || Has(lc, "ng serve") ? "Angular" :
            Has(lc, "vue-cli-service") ? "Vue" :
            Has(lc, "astro") ? "Astro" :
            Has(lc, "svelte") ? "Svelte" :
            Has(lc, "remix") ? "Remix" :
            Has(lc, "gatsby") ? "Gatsby" :
            Has(lc, "storybook") ? "Storybook" :
            Has(lc, "http.server") ? "http.server" :
            Has(lc, "uvicorn") ? "Uvicorn" :
            Has(lc, "gunicorn") ? "Gunicorn" :
            Has(lc, "fastapi") ? "FastAPI" :
            Has(lc, "flask") ? "Flask" :
            Has(lc, "manage.py") || Has(lc, "django") ? "Django" :
            Has(lc, "rails") ? "Rails" :
            Has(lc, "puma") ? "Puma" :
            Has(lc, "sinatra") ? "Sinatra" :
            Has(lc, "jekyll") ? "Jekyll" :
            Has(lc, "hugo") ? "Hugo" :
            Has(lc, "aspnet") || (baseName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) ? ".NET" :
            Has(lc, "http-server") ? "http-server" :
            Has(lc, "live-server") ? "live-server" :
            RuntimeLabel(baseName);

        return (fw, CleanCommand(cmd, baseName, proc?.Name ?? baseName));
    }

    private static bool Has(string haystack, string needle) => haystack.Contains(needle, StringComparison.Ordinal);

    private static string RuntimeLabel(string baseName)
    {
        if (baseName.StartsWith("python", StringComparison.OrdinalIgnoreCase)) return "Python";
        return baseName.ToLowerInvariant() switch
        {
            "node" => "Node",
            "deno" => "Deno",
            "bun" => "Bun",
            "ruby" => "Ruby",
            "php" => "PHP",
            "java" or "javaw" => "Java",
            "dotnet" => ".NET",
            "" => "Server",
            _ => char.ToUpperInvariant(baseName[0]) + baseName.Substring(1),
        };
    }

    /// A short, honest command: the exe basename plus the first path-like arg,
    /// itself reduced to a basename. Full node/python invocations are absolute-
    /// path soup; this keeps the row readable without inventing "npm run dev".
    private static string CleanCommand(string cmd, string baseName, string procName)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return procName;
        var toks = Tokenize(cmd);
        if (toks.Count == 0) return procName;

        var exe = SafeBaseName(toks[0]);
        var arg = toks.Skip(1).FirstOrDefault(a => !a.StartsWith('-') && !a.StartsWith('/'));
        var argShort = arg == null ? "" : LooksLikePath(arg) ? SafeBaseName(arg) : arg;
        var s = (exe + " " + argShort).Trim();
        return s.Length > 64 ? s.Substring(0, 63) + "…" : s;
    }

    private static bool LooksLikePath(string s) => s.Contains('\\') || s.Contains('/');

    private static string SafeBaseName(string s)
    {
        s = s.Trim().Trim('"');
        try { var n = Path.GetFileName(s); return string.IsNullOrEmpty(n) ? s : n; }
        catch { return s; }
    }

    /// Space-split respecting double quotes. Good enough for pulling the exe and
    /// first arg off a Windows command line; not a full CommandLineToArgvW.
    private static List<string> Tokenize(string cmd)
    {
        var toks = new List<string>();
        var sb = new StringBuilder();
        var inQ = false;
        foreach (var c in cmd)
        {
            if (c == '"') { inQ = !inQ; continue; }
            if (c == ' ' && !inQ) { if (sb.Length > 0) { toks.Add(sb.ToString()); sb.Clear(); } }
            else sb.Append(c);
        }
        if (sb.Length > 0) toks.Add(sb.ToString());
        return toks;
    }

    private static string BaseName(string procName)
    {
        // Win32_Process.Name is the image name, e.g. "node.exe".
        if (procName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return procName.Substring(0, procName.Length - 4);
        return procName;
    }

}
