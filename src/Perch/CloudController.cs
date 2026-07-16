using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Perch;

/// Owns the cloud feature end to end: the ledger, the poller, the refresh timer,
/// and the delete path. Kept out of MainWindow because none of it touches window
/// state — it's a background reconciler that happens to push to the webview.
///
/// The whole thing is inert unless gcloud is installed AND authenticated, so a
/// user who doesn't drive GCP from an agent never sees a chip, a panel, or a
/// subprocess.
internal sealed class CloudController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly CloudPoller _poller = new();
    private readonly CloudLedger _ledger = new();
    private readonly Action<object> _push;
    private readonly Func<string?, string?> _lookupPaneState;

    private DispatcherTimer? _timer;
    private CancellationTokenSource? _inflight;
    private IReadOnlyList<CloudResource> _last = Array.Empty<CloudResource>();
    private bool _panelOpen;

    /// Background cadence. Slow on purpose: this exists to catch a machine you
    /// forgot hours ago, not to render a live dashboard, and each tick is a
    /// gcloud subprocess. Drops to FastMs while the panel is actually open.
    private const int IdleMs = 5 * 60 * 1000;
    private const int FastMs = 60 * 1000;

    public CloudController(Dispatcher dispatcher, Action<object> push, Func<string?, string?> lookupPaneState)
    {
        _dispatcher = dispatcher;
        _push = push;
        _lookupPaneState = lookupPaneState;
        _poller.LookupPaneState = _lookupPaneState;
        _poller.LookupLedger = s => _ledger.Get(s);
    }

    /// Probe gcloud ONCE, and only arm the timer if it's actually there. A user
    /// with no gcloud (or no active login) gets a single cheap check at startup
    /// and then nothing at all — no timer, no subprocesses, no chip, no panel.
    /// The feature costs them exactly one process spawn, once, ever.
    public async void Start()
    {
        if (!await _poller.IsAvailableAsync())
        {
            Log.Info("CloudController: gcloud absent or not logged in — cloud feature off for this session");
            return;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(IdleMs),
        };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
        // One poll at launch: the most valuable moment to be told about a machine
        // that survived the night is the moment you sit back down.
        await RefreshAsync();
    }

    /// The panel opened or closed. Open → poll now and tick faster, so deleting
    /// something makes the row disappear promptly rather than up to 5 min later.
    public void SetPanelOpen(bool open)
    {
        _panelOpen = open;
        if (_timer != null) _timer.Interval = TimeSpan.FromMilliseconds(open ? FastMs : IdleMs);
        if (open) _ = RefreshAsync();
    }

    /// The hook just stamped labels onto a `gcloud create` in this pane. The
    /// labels can only carry ids, so snapshot the human-readable half NOW, while
    /// the pane still exists to be read. Once it's closed this is the only record
    /// of what the machine was for.
    public void OnStamped(Session sess, PaneNode pane, CloudStampedMessage msg)
    {
        var session = msg.Session;
        if (string.IsNullOrWhiteSpace(session)) session = pane.ClaudeSessionId;
        if (string.IsNullOrWhiteSpace(session)) return;

        _ledger.Remember(
            session!,
            agentName: pane.Name,
            // NamePrompt is the full first prompt of the pane's cc session — the
            // sentence that actually explains why this machine exists.
            task: pane.NamePrompt,
            cwd: pane.Cwd ?? sess.Cwd,
            paneId: pane.Id.ToString("N"));

        // A machine was just created; don't make the user wait out the idle tick
        // to see it. gcloud is eventually consistent about a brand-new instance,
        // so give it a moment before asking.
        _ = Task.Delay(6000).ContinueWith(_ => _dispatcher.BeginInvoke(() => _ = RefreshAsync()));
    }

    public async Task RefreshAsync()
    {
        // Coalesce: a burst of stamps must not queue a burst of gcloud calls.
        _inflight?.Cancel();
        var cts = new CancellationTokenSource();
        _inflight = cts;
        try
        {
            var list = await _poller.PollAsync(cts.Token);
            if (cts.IsCancellationRequested) return;
            _last = list;
            Push();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("CloudController.Refresh", ex); }
    }

    /// Delete one resource. A VM and a Dataproc cluster take DIFFERENT commands,
    /// and deleting a cluster's master as if it were a VM leaves the workers
    /// orphaned and still billing — the exact failure this feature exists to
    /// prevent. Confirmation happens in the UI; by the time we're here, the user
    /// has said yes.
    public async Task DeleteAsync(string id)
    {
        var r = _last.FirstOrDefault(x => x.Id == id);
        if (r == null) return;
        // Radar rows are view-only. We surface them so a stray GPU can't hide, but
        // Perch didn't create it and won't delete it — that's the owner's call,
        // where they made it. Belt-and-braces: the UI also omits the kill button.
        if (!r.StartedByPerch) { Log.Info($"CloudController: refusing to delete radar row {r.Name}"); return; }

        var args = r.Kind == "cluster"
            ? $"dataproc clusters delete {r.Name} --region={r.Region} --quiet"
            : $"compute instances delete {r.Name} --zone={r.Zone} --quiet";

        Log.Info($"CloudController: deleting {r.Kind} {r.Name}");
        var (code, _, stderr) = await CloudPoller.RunAsync(args, 120_000, CancellationToken.None);
        if (code != 0)
            Log.Info($"CloudController: delete failed ({code}): {stderr.Trim()}");

        await RefreshAsync();
    }

    /// Delete every orphan in one go. Sequential, not parallel: a burst of
    /// concurrent gcloud deletes is a good way to get rate-limited halfway
    /// through and leave the user unsure what actually died.
    public async Task DeleteOrphansAsync()
    {
        // Only OUR orphans. A radar row (a GPU we didn't create) is never swept —
        // deleting someone else's Terraform-managed box would be a real incident.
        foreach (var r in _last.Where(x => x.IsOrphan && x.StartedByPerch).ToList())
            await DeleteAsync(r.Id);
    }

    private void Push()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _push(new
        {
            type = "cloud.data",
            resources = _last.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                kind = r.Kind,
                machineType = r.MachineType,
                zone = r.Zone,
                vmCount = r.VmCount,
                isGpu = r.IsGpu,
                createdMs = r.CreatedUnixMs,
                usdPerHour = r.UsdPerHour,
                priceKnown = r.PriceKnown,
                agentName = r.AgentName,
                task = r.Task,
                paneId = r.PaneId,
                isOrphan = r.IsOrphan,
                agentState = r.AgentState,
                startedByPerch = r.StartedByPerch,
            }).ToArray(),
            nowMs = now,
        });
    }

    public void Dispose()
    {
        try { _timer?.Stop(); } catch { }
        try { _inflight?.Cancel(); } catch { }
    }
}
