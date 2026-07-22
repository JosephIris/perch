using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Perch;

/// Owns the localhost dev-servers feature end to end: the scan timer, the
/// pane-ownership ledger, and the kill path. Kept out of MainWindow because none
/// of it touches window state — it's a background reconciler that pushes to the
/// webview, exactly like CloudController, just watching your own machine instead
/// of GCP.
///
/// Three buckets fall out of scan + ledger:
///   live       — a still-open pane owns it (ancestry hits the pane's shell pid)
///   lingering  — no live owner, but the ledger remembers the pane that had it
///                (same pid) → it outlived its pane, the whole point of this
///   other      — a dev server Perch never launched (started by hand elsewhere)
internal sealed class LocalController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly LocalPoller _poller = new();
    private readonly LocalLedger _ledger = new();
    private readonly Action<object> _push;
    /// Snapshots live panes' root pids on the UI thread. Captured BEFORE the
    /// scan's await so attribution runs against an immutable copy — no off-thread
    /// read of session/pane state.
    private readonly Func<IReadOnlyList<PaneProc>> _snapshotPanes;
    /// Hands each scan's pane attribution (paneId "N" guid → listening ports)
    /// back to the host, which projects it onto PaneNode.Ports — the source of
    /// the tab/header ":port" chips. This is the ONLY feeder of pane ports:
    /// nothing ever sent the `meta --port` IPC message the chips were first
    /// built against, which is why they never lit.
    private readonly Action<IReadOnlyDictionary<string, int[]>> _applyPanePorts;

    private DispatcherTimer? _timer;
    private CancellationTokenSource? _inflight;
    private IReadOnlyList<ServerView> _last = Array.Empty<ServerView>();

    /// Background cadence. A scan is cheap but still a subprocess, and this exists
    /// to catch a server you forgot, not to render a live dashboard. Drops to
    /// FastMs while the panel is open so a kill makes the row vanish promptly.
    private const int IdleMs = 30 * 1000;
    private const int FastMs = 3 * 1000;

    public LocalController(
        Dispatcher dispatcher, Action<object> push,
        Func<IReadOnlyList<PaneProc>> snapshotPanes,
        Action<IReadOnlyDictionary<string, int[]>> applyPanePorts)
    {
        _dispatcher = dispatcher;
        _push = push;
        _snapshotPanes = snapshotPanes;
        _applyPanePorts = applyPanePorts;
    }

    public void Start()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(IdleMs),
        };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
        // One scan at launch: sitting back down is the moment to be told a server
        // survived from last session.
        _ = RefreshAsync();
    }

    public void SetPanelOpen(bool open)
    {
        if (_timer != null) _timer.Interval = TimeSpan.FromMilliseconds(open ? FastMs : IdleMs);
        if (open) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        // Snapshot panes NOW, on the UI thread, before any await.
        var panes = _snapshotPanes();

        _inflight?.Cancel();
        var cts = new CancellationTokenSource();
        _inflight = cts;
        try
        {
            var listeners = await _poller.ScanAsync(panes, cts.Token);
            if (cts.IsCancellationRequested) return;
            // Null = the SCAN failed (timeout, corrupt output), not "no servers".
            // Keep the last good picture rather than flickering every port chip
            // off for 30s; a legitimately empty scan still clears everything.
            if (listeners == null) return;
            _last = Classify(listeners);
            Push();

            // Project the live attributions onto pane state (tab/header chips).
            // Continuation of an awaited call started on the dispatcher timer,
            // so this runs on the UI thread — safe to touch pane state.
            var byPane = new Dictionary<string, int[]>();
            foreach (var g in _last.Where(v => v.Kind == "live" && v.PaneId != null)
                                   .GroupBy(v => v.PaneId!))
                byPane[g.Key] = g.Select(v => v.Port).Distinct().OrderBy(p => p).ToArray();
            _applyPanePorts(byPane);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("LocalController.Refresh", ex); }
    }

    /// Turn raw listeners into the three-bucket view, updating the ledger as we
    /// go: owned ports refresh their memory; an unowned port is lingering iff the
    /// ledger remembers it at the SAME pid, otherwise it's "other" (and a stale
    /// same-port-different-pid memory is dropped).
    private IReadOnlyList<ServerView> Classify(IReadOnlyList<LocalListener> listeners)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var views = new List<ServerView>(listeners.Count);

        foreach (var l in listeners)
        {
            string kind;
            string? paneName;
            string? paneId;
            string? agentState = null;
            long? closedMs = null;

            if (l.OwnerName != null)
            {
                kind = "live";
                paneName = l.OwnerName;
                paneId = l.OwnerPaneId;
                agentState = l.OwnerState;
                _ledger.Remember(l.Port, l.Pid, l.OwnerName, l.OwnerPaneId, now);
            }
            else
            {
                var e = _ledger.Get(l.Port);
                if (e != null && e.Pid == l.Pid)
                {
                    kind = "lingering";
                    paneName = e.PaneName;
                    paneId = e.PaneId;
                    closedMs = e.LastOwnedUnixMs;
                }
                else
                {
                    kind = "other";
                    paneName = null;
                    paneId = null;
                    if (e != null) _ledger.Forget(l.Port);   // port reused by a new process
                }
            }

            views.Add(new ServerView(
                Id: $"{l.Port}/{l.Pid}",
                Port: l.Port,
                Pid: l.Pid,
                Addr: l.Addr,
                Framework: l.Framework,
                Command: l.Command,
                StartedUnixMs: l.StartedUnixMs,
                Kind: kind,
                PaneName: paneName,
                PaneId: paneId,
                AgentState: agentState,
                ClosedUnixMs: closedMs));
        }

        _ledger.Flush();

        // Lingering first (needs you), then live, then other; newest within each
        // — a server you just started should sit at the top of its group.
        static int Rank(string k) => k == "lingering" ? 0 : k == "live" ? 1 : 2;
        return views
            .OrderBy(v => Rank(v.Kind))
            .ThenByDescending(v => v.StartedUnixMs)
            .ToList();
    }

    /// Kill one server by its EXACT pid, tree and all (npm → node → esbuild), so
    /// killing the listener actually frees the port. Never by name.
    public async Task KillAsync(int pid)
    {
        if (pid <= 4) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            Log.Info($"LocalController: killed pid {pid} (+tree)");
        }
        catch (ArgumentException) { /* already gone */ }
        catch (Exception ex) { Log.Info($"LocalController: kill {pid} failed: {ex.Message}"); }

        // The port it held is now free — drop any lingering memory so it doesn't
        // reappear against a reused port later.
        var view = _last.FirstOrDefault(v => v.Pid == pid);
        if (view != null) { _ledger.Forget(view.Port); _ledger.Flush(); }

        await RefreshAsync();
    }

    /// Kill every lingering server — the ones whose pane is already gone. These
    /// are the safe-to-reap set; live servers are left alone.
    public async Task KillLingeringAsync()
    {
        foreach (var v in _last.Where(v => v.Kind == "lingering").ToList())
            await KillAsync(v.Pid);
    }

    private void Push()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _push(new
        {
            type = "local.data",
            servers = _last.Select(v => new
            {
                id = v.Id,
                port = v.Port,
                pid = v.Pid,
                addr = v.Addr,
                framework = v.Framework,
                command = v.Command,
                startedMs = v.StartedUnixMs,
                kind = v.Kind,
                paneName = v.PaneName,
                paneId = v.PaneId,
                agentState = v.AgentState,
                closedMs = v.ClosedUnixMs,
            }).ToArray(),
            nowMs = now,
        });
    }

    public void Dispose()
    {
        try { _timer?.Stop(); } catch { }
        try { _inflight?.Cancel(); } catch { }
        try { _ledger.Flush(); } catch { }
    }

    private sealed record ServerView(
        string Id, int Port, int Pid, string Addr, string Framework, string Command,
        long StartedUnixMs, string Kind, string? PaneName, string? PaneId,
        string? AgentState, long? ClosedUnixMs);
}
