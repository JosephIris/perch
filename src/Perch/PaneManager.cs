using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace Perch;

/// Owns the per-pane process plumbing: one ConPty + one PerchIpcServer per
/// live leaf pane, plus the byte counters the harness probes and the
/// last-output timestamps the idle watchdog reads. MainWindow decides WHAT to
/// spawn (shell, cwd, resume command) and reacts to the events; this class
/// owns the lifecycles so they can't leak or double-spawn.
internal sealed class PaneManager : IDisposable
{
    // One ConPty per leaf pane. Lifetime: created on first activation of a
    // session (lazy spawn) so closed-but-persisted sessions don't fork a
    // shell at startup; disposed when the pane is removed or the session
    // closes.
    private readonly Dictionary<Guid, ConPty> _ptys = new();

    // Per-pane named-pipe server listening on \\.\pipe\perch\<paneId>.
    // Agents inside the pane talk to us via the perch CLI ("perch status
    // working") which dials this pipe. Lifecycle mirrors _ptys exactly.
    private readonly Dictionary<Guid, PerchIpcServer> _paneIpc = new();

    // Bytes-received-since-launch per pane id. Surface for `pty.snapshot`
    // and the test harness; the page doesn't see this.
    private readonly Dictionary<Guid, long> _bytesReceived = new();

    // Last PTY-output timestamp (Stopwatch ticks) per pane id, written from
    // the ConPty read thread and read by the idle watchdog on the UI thread —
    // hence ConcurrentDictionary. Drives the Working→Done demotion.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> _lastOutputTicks = new();

    private readonly Dispatcher _dispatcher;

    public PaneManager(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// PTY emitted bytes (fired on the ConPty read thread — subscriber
    /// marshals). Counters are already updated when this fires.
    public event Action<Guid, ReadOnlyMemory<byte>>? Output;

    /// PTY process exited. The dead PTY + IPC are already scheduled for
    /// teardown on the UI thread (so a later pane.resize respawns cleanly).
    public event Action<Guid, int>? Exited;

    // Agent IPC (perch status / notify / meta / …) with the owning session
    // and pane bound at spawn time. Fired on the UI thread (PerchIpcServer
    // dispatches through the Dispatcher it's given).
    public event Action<Session, Guid, StatusMessage>? AgentStatus;
    public event Action<Session, Guid, NotifyMessage>? AgentNotify;
    public event Action<Session, Guid, MetaMessage>? AgentMeta;
    public event Action<Session, Guid, GitBaselineMessage>? GitBaseline;
    public event Action<Session, Guid, TitleMessage>? AgentTitle;
    public event Action<Session, Guid, NameResetMessage>? NameReset;
    public event Action<Session, Guid, AgentMessage>? AgentType;
    public event Action<Session, Guid, SessionMessage>? AgentSession;

    public bool Has(Guid paneId) => _ptys.ContainsKey(paneId);

    public bool TryGet(Guid paneId, out ConPty pty) => _ptys.TryGetValue(paneId, out pty!);

    public void Write(Guid paneId, byte[] bytes)
    {
        if (_ptys.TryGetValue(paneId, out var pty)) pty.Write(bytes);
    }

    public void Ack(Guid paneId, long bytes)
    {
        if (_ptys.TryGetValue(paneId, out var pty)) pty.Ack(bytes);
    }

    /// Resize the pane's PTY. False when the pane has no PTY yet — the caller
    /// treats that as the lazy-spawn trigger.
    public bool TryResize(Guid paneId, int cols, int rows)
    {
        if (!_ptys.TryGetValue(paneId, out var pty)) return false;
        pty.Resize(cols, rows);
        return true;
    }

    public long BytesReceived(Guid paneId)
    {
        lock (_bytesReceived) return _bytesReceived.TryGetValue(paneId, out var n) ? n : 0;
    }

    public bool TryGetLastOutputTicks(Guid paneId, out long ticks) =>
        _lastOutputTicks.TryGetValue(paneId, out ticks);

    /// Start the pane's shell + its agent IPC pipe. `startCmd` is the fully
    /// built command line (Shell.BuildStartupCommandLine — PERCH_PIPE /
    /// PERCH_PANE_ID env are injected there). Throws on spawn failure; the
    /// caller surfaces the error to the page. A duplicate spawn is refused —
    /// leaking a second ConPty would give the page two byte streams for one
    /// xterm (output interleaved beyond recognition).
    public void Spawn(Session sess, PaneNode pane, string startCmd, string cwd, int cols, int rows, string baseShell)
    {
        if (_ptys.ContainsKey(pane.Id))
        {
            Log.Info($"Pane.spawn.dup pane={pane.Id:N} -- already has a PTY, skipping");
            return;
        }
        var pty = ConPty.Start(startCmd, cols: cols, rows: rows, cwd: cwd);
        var paneId = pane.Id;
        pty.OutputReceived += (_, bytes) =>
        {
            lock (_bytesReceived)
                _bytesReceived[paneId] = (_bytesReceived.TryGetValue(paneId, out var n) ? n : 0) + bytes.Length;
            _lastOutputTicks[paneId] = System.Diagnostics.Stopwatch.GetTimestamp();
            Output?.Invoke(paneId, bytes);
        };
        pty.Exited += (_, code) =>
        {
            // Drop the dead PTY + IPC so a subsequent pane.resize naturally
            // respawns into the same paneId. Without this the resize handler
            // sees a stale entry and just calls Resize on a dead pty.
            _dispatcher.BeginInvoke(() => Destroy(paneId));
            Exited?.Invoke(paneId, code);
        };
        _ptys[paneId] = pty;

        // Agent IPC: per-pane named pipe \\.\pipe\perch\<paneId>. The
        // pane's shell inherits PERCH_PIPE pointing here, so `perch
        // status working` from inside the shell lands at AgentStatus.
        var ipc = new PerchIpcServer(paneId, _dispatcher);
        ipc.OnStatus += msg => AgentStatus?.Invoke(sess, paneId, msg);
        ipc.OnNotify += msg => AgentNotify?.Invoke(sess, paneId, msg);
        ipc.OnMeta   += msg => AgentMeta?.Invoke(sess, paneId, msg);
        ipc.OnGitBaseline += msg => GitBaseline?.Invoke(sess, paneId, msg);
        ipc.OnTitle  += msg => AgentTitle?.Invoke(sess, paneId, msg);
        ipc.OnNameReset += msg => NameReset?.Invoke(sess, paneId, msg);
        ipc.OnAgent  += msg => AgentType?.Invoke(sess, paneId, msg);
        ipc.OnSession += msg => AgentSession?.Invoke(sess, paneId, msg);
        ipc.Start();
        _paneIpc[paneId] = ipc;

        Log.Info("Pane.spawn", $"pane={paneId:N} pid={pty.ProcessId} shell={baseShell} cmd={startCmd}");
    }

    public void Destroy(Guid paneId)
    {
        if (_paneIpc.Remove(paneId, out var ipc)) { try { ipc.Dispose(); } catch { } }
        if (!_ptys.TryGetValue(paneId, out var pty)) return;
        _ptys.Remove(paneId);
        try { pty.Dispose(); } catch { }
        lock (_bytesReceived) _bytesReceived.Remove(paneId);
        _lastOutputTicks.TryRemove(paneId, out _);
    }

    public void Dispose()
    {
        foreach (var ipc in _paneIpc.Values) { try { ipc.Dispose(); } catch { } }
        _paneIpc.Clear();
        foreach (var pty in _ptys.Values) { try { pty.Dispose(); } catch { } }
        _ptys.Clear();
    }
}
