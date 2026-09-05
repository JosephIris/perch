using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Perch;

/// The single place this app launches a subprocess.
///
/// Every shell-out — git, gcloud, mklink — goes through RunAsync so that ONE
/// counter knows the aggregate. The aggregate is the entire point. The pollers
/// arrived one feature at a time (inspector rail, localhost panel, per-tab
/// commit counts), each defensible on its own, and nothing in the codebase
/// could see that together they had reached ~2.4 process launches per second
/// while the app sat idle. Process creation on Windows takes global kernel
/// locks and every image is inspected by the AV stack, so the cost lands on the
/// whole machine rather than on us — and it never appears as one fat process in
/// Task Manager, which is precisely why it went unnoticed.
///
/// SpawnBudgetTests pins the idle rate. Add a poller and that test tells you,
/// instead of someone's desktop telling them two weeks later.
internal static class ProcRunner
{
    private static long _spawns;
    private static readonly ConcurrentDictionary<string, long> Sites = new(StringComparer.Ordinal);

    /// Total subprocesses launched since the last Reset. Process-wide, which
    /// makes it right for a status-bar readout and WRONG for a test assertion —
    /// see BeginScope.
    public static long SpawnCount => Interlocked.Read(ref _spawns);

    /// Per-call-site counts. A budget failure names the feature that caused it
    /// rather than just the total, so the fix is obvious from the test output.
    public static IReadOnlyDictionary<string, long> BySite => Sites;

    public static void Reset()
    {
        Interlocked.Exchange(ref _spawns, 0);
        Sites.Clear();
    }

    /// Count only the spawns made inside this async flow.
    ///
    /// The process-wide counter above cannot be asserted on: xUnit runs test
    /// classes in parallel, and several suites legitimately shell out to git, so
    /// a budget test reading the global count measures whatever else happened to
    /// be running. That is not flakiness to retry past — the number genuinely
    /// isn't the one the test means. AsyncLocal flows through await, so a scope
    /// captures exactly the work under it and nothing beside it.
    public static SpawnScope BeginScope() => new();

    public sealed class SpawnScope : IDisposable
    {
        private static readonly AsyncLocal<SpawnScope?> Cur = new();
        internal static SpawnScope? Current => Cur.Value;

        private readonly SpawnScope? _prev;
        private long _count;
        private readonly ConcurrentDictionary<string, long> _sites = new(StringComparer.Ordinal);

        internal SpawnScope()
        {
            _prev = Cur.Value;
            Cur.Value = this;
        }

        public long SpawnCount => Interlocked.Read(ref _count);
        public IReadOnlyDictionary<string, long> BySite => _sites;

        internal void Record(string site)
        {
            Interlocked.Increment(ref _count);
            _sites.AddOrUpdate(site, 1, static (_, n) => n + 1);
            _prev?.Record(site);      // nested scopes roll up
        }

        public string Breakdown()
            => _sites.IsEmpty
                ? "  (no subprocesses recorded)"
                : string.Join("\n", _sites.OrderByDescending(kv => kv.Value)
                                          .Select(kv => $"  {kv.Value,4}x  {kv.Key}"));

        public void Dispose() => Cur.Value = _prev;
    }

    /// Launch `fileName args`, capture both pipes, return (exit code, stdout,
    /// stderr). Code -1 means the process could not be started, timed out, or
    /// threw — callers distinguish those by stderr, as they did before.
    ///
    /// `site` is a stable tag ("git.status", "local.scan", "cloud.list") used
    /// only for counting. `timeoutMs` of 0 means no timeout.
    ///
    /// `stdoutEncoding` is null by default so each caller keeps the decoding it
    /// had: git sets UTF-8 explicitly (i18n.logOutputEncoding) while the others
    /// inherit the console codepage, and silently changing that would garble
    /// output rather than fix anything.
    ///
    /// `stdinText`, when given, is written to the child's stdin (UTF-8) and the
    /// pipe closed before we wait — how a prompt reaches `claude -p` without
    /// passing through cmd.exe's quoting. `env` adds or, for a null value,
    /// REMOVES variables from the child's environment; the headless runner
    /// strips PERCH_PIPE so a child never mistakes itself for a pane.
    public static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        string fileName,
        string arguments,
        string site,
        string? workingDir = null,
        int timeoutMs = 0,
        Encoding? stdoutEncoding = null,
        CancellationToken ct = default,
        string? stdinText = null,
        IReadOnlyDictionary<string, string?>? env = null)
    {
        Interlocked.Increment(ref _spawns);
        Sites.AddOrUpdate(site, 1, static (_, n) => n + 1);
        SpawnScope.Current?.Record(site);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (workingDir != null) psi.WorkingDirectory = workingDir;
            if (stdoutEncoding != null)
            {
                psi.StandardOutputEncoding = stdoutEncoding;
                psi.StandardErrorEncoding = stdoutEncoding;
            }
            if (stdinText != null)
            {
                psi.RedirectStandardInput = true;
                psi.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }
            if (env != null)
            {
                foreach (var (key, value) in env)
                {
                    if (value == null) psi.Environment.Remove(key);
                    else psi.Environment[key] = value;
                }
            }

            using var p = new Process { StartInfo = psi };
            if (!p.Start()) return (-1, "", $"failed to start {fileName}");

            // Read BOTH pipes before waiting. Waiting first can deadlock: a
            // child that writes more than the pipe buffer to stderr blocks
            // forever on the write while we block forever on the exit.
            var outT = p.StandardOutput.ReadToEndAsync();
            var errT = p.StandardError.ReadToEndAsync();

            if (stdinText != null)
            {
                // Write after the readers are armed, for the same reason: a
                // child that echoes while we're still writing must not block us.
                try
                {
                    await p.StandardInput.WriteAsync(stdinText);
                    p.StandardInput.Close();
                }
                catch (Exception ex) { Log.Info($"ProcRunner.{site}", $"stdin write failed: {ex.Message}"); }
            }

            if (timeoutMs > 0)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(timeoutMs);
                try { await p.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return (-1, "", "timed out");
                }
            }
            else
            {
                await p.WaitForExitAsync(ct);
            }

            return (p.ExitCode, await outT, await errT);
        }
        catch (Exception ex)
        {
            Log.Error($"ProcRunner.{site}", ex);
            return (-1, "", ex.Message);
        }
    }
}
