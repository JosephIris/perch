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
/// the poller so attribution never has to read window state off-thread. Pid is
/// the ConPty child; a dev server started in that pane is one of its descendants,
/// so a server is "owned" by the pane whose Pid appears in the server's ancestry.
internal sealed record PaneProc(int Pid, string PaneId, string Name, string? State);

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
/// bill — a port scan is cheap and always available on Windows — so this feature
/// is always on, but still invisible until something is actually listening.
///
/// One PowerShell subprocess does the I/O: Get-NetTCPConnection for the listener
/// set, Get-CimInstance Win32_Process for the pid → (parent, name, command line,
/// start time) map. Both are native to Windows; the whole thing degrades to "no
/// servers" if either is unavailable rather than ever throwing into the UI.
internal sealed class LocalPoller
{
    // Loopback + wildcard addresses. A server on 0.0.0.0 is still reachable at
    // localhost, so it counts; a server bound to a specific LAN NIC is not a
    // "localhost dev server" and is deliberately excluded.
    private const string Script = """
        $ErrorActionPreference='SilentlyContinue'
        $loop=@('127.0.0.1','::1','0.0.0.0','::')
        $L = Get-NetTCPConnection -State Listen |
             Where-Object { $loop -contains $_.LocalAddress } |
             ForEach-Object { [pscustomobject]@{ port=[int]$_.LocalPort; pid=[int]$_.OwningProcess; addr=[string]$_.LocalAddress } }
        $P = Get-CimInstance Win32_Process |
             ForEach-Object {
               $ms=0
               if ($_.CreationDate) { try { $ms=[int64]([datetimeoffset]$_.CreationDate).ToUnixTimeMilliseconds() } catch {} }
               [pscustomobject]@{ pid=[int]$_.ProcessId; ppid=[int]$_.ParentProcessId; name=[string]$_.Name; cmd=[string]$_.CommandLine; startMs=$ms }
             }
        [pscustomobject]@{ listeners=@($L); procs=@($P) } | ConvertTo-Json -Depth 3 -Compress
        """;

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

    public async Task<IReadOnlyList<LocalListener>> ScanAsync(
        IReadOnlyList<PaneProc> panes, CancellationToken ct = default)
    {
        var (code, stdout, stderr) = await RunAsync(Script, 15_000, ct);
        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            if (code != 0) Log.Info($"LocalPoller: scan failed ({code}): {stderr.Trim()}");
            return Array.Empty<LocalListener>();
        }

        try { return Parse(stdout, panes); }
        catch (Exception ex) { Log.Error("LocalPoller.Parse", ex); return Array.Empty<LocalListener>(); }
    }

    private sealed record ProcRow(int Pid, int Ppid, string Name, string Cmd, long StartMs);

    internal IReadOnlyList<LocalListener> Parse(string json, IReadOnlyList<PaneProc> panes)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var procs = new Dictionary<int, ProcRow>();
        if (root.TryGetProperty("procs", out var pe) && pe.ValueKind == JsonValueKind.Array)
            foreach (var el in pe.EnumerateArray())
            {
                var pid = Int(el, "pid");
                if (pid <= 0) continue;
                procs[pid] = new ProcRow(pid, Int(el, "ppid"), Str(el, "name") ?? "", Str(el, "cmd") ?? "", Long(el, "startMs"));
            }

        // pid → owning pane. Built with the indexer (not ToDictionary) so a
        // duplicated pid — impossible in practice, but cheap to be safe about —
        // can't throw the whole scan away.
        var paneByPid = new Dictionary<int, PaneProc>();
        foreach (var p in panes) paneByPid[p.Pid] = p;

        var result = new List<LocalListener>();
        var seen = new HashSet<(int, int)>();

        if (root.TryGetProperty("listeners", out var le) && le.ValueKind == JsonValueKind.Array)
            foreach (var el in le.EnumerateArray())
            {
                var port = Int(el, "port");
                var pid = Int(el, "pid");
                if (port <= 0 || pid <= 4) continue;          // 0/4 = System / Idle
                // A dev server binds both 127.0.0.1 and ::1 → two rows, one pid.
                // Collapse to a single row keyed by (port, pid).
                if (!seen.Add((port, pid))) continue;

                procs.TryGetValue(pid, out var proc);
                var name = BaseName(proc?.Name ?? "");

                var owner = FindOwner(pid, procs, paneByPid);
                // System loopback noise (svchost, etc.) that no pane owns and no
                // dev runtime explains is dropped — the panel is about dev
                // servers, not every socket on the box.
                if (owner == null && !IsDevRuntime(name)) continue;

                var (framework, command) = Describe(proc, name);
                result.Add(new LocalListener(
                    Port: port,
                    Pid: pid,
                    Addr: Str(el, "addr") ?? "127.0.0.1",
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

    /// Walk a listener's process ancestry until it hits a live pane's root pid.
    /// Bounded and cycle-guarded — a corrupt ppid chain must not spin.
    private static PaneProc? FindOwner(int pid, Dictionary<int, ProcRow> procs, Dictionary<int, PaneProc> paneByPid)
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
    private static (string Framework, string Command) Describe(ProcRow? proc, string baseName)
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

    private static int Int(JsonElement el, string name)
        => el.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetInt32(out var i) ? i : 0,
                JsonValueKind.String => int.TryParse(v.GetString(), out var i) ? i : 0,
                _ => 0,
            }
            : 0;

    private static long Long(JsonElement el, string name)
        => el.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetInt64(out var i) ? i : 0,
                JsonValueKind.String => long.TryParse(v.GetString(), out var i) ? i : 0,
                _ => 0,
            }
            : 0;

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// Run the scan off the UI thread via Windows PowerShell (5.1, always present
    /// — pwsh may not be). The script goes in as -EncodedCommand so no quoting
    /// survives to be mangled.
    internal static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        string script, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {b64}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            if (!p.Start()) return (-1, "", "failed to start powershell");

            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-1, "", "scan timed out");
            }
            return (p.ExitCode, await stdout, await stderr);
        }
        catch (Exception ex)
        {
            Log.Error("LocalPoller.Run", ex);
            return (-1, "", ex.Message);
        }
    }
}
