using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Perch;

/// The URL-pane registry + policy layer, extracted from the Windows
/// UrlPaneController so both hosts share it. The page emits urlpane.layout
/// (rect in page CSS px) for each URL leaf; we create a native browser view
/// through the host factory on first sight, and position / renavigate on
/// subsequent messages. See the original file's comments for the WHY of
/// _pending (create-storm guard), _rejected (one report per pane) and the
/// suppress/visible composition (native views paint above the page's HTML,
/// so a DOM modal can't cover them — they must hide instead).
internal sealed class UrlPanes : IUrlPanes
{
    private sealed class Entry
    {
        public required IUrlPaneHost Host;
        public required string Url;

        /// The page's intent for this pane: visible when its session's stage
        /// is the active one, hidden when switched away from. Independent of
        /// the modal-suppress override — a pane is only actually shown when
        /// BOTH say so (see Apply).
        public bool DesiredVisible = true;
    }

    private readonly Dictionary<Guid, Entry> _panes = new();

    /// Panes whose create is in flight (possibly waiting on the browser
    /// engine). Layout messages keep arriving the whole time (ResizeObserver
    /// fires on every reflow); without this set each one would start its own
    /// create and stack orphaned native views. Also records a dispose that
    /// lands mid-create, so the completion bails instead of resurrecting a
    /// closed pane.
    private sealed class Pending
    {
        public required UrlPaneLayoutMsg Layout;
        public bool Visible = true;
    }
    private readonly Dictionary<Guid, Pending> _pending = new();

    /// Panes we've already told the page about a policy rejection for.
    private readonly HashSet<Guid> _rejected = new();

    private bool _suppressed;
    private readonly IUiThread _ui;
    private readonly IUrlPaneHostFactory _factory;

    public event Action<Guid, string>? AutoTitleRequested;
    public event Action<Guid, string>? Rejected;
    public event Action<Guid, string>? Failed;

    public UrlPanes(IUiThread ui, IUrlPaneHostFactory factory)
    {
        _ui = ui;
        _factory = factory;
    }

    public void SetSuppressed(bool suppressed)
    {
        _suppressed = suppressed;
        foreach (var e in _panes.Values) Apply(e);
    }

    public void SetVisible(Guid paneId, bool visible)
    {
        if (_pending.TryGetValue(paneId, out var pending)) pending.Visible = visible;
        if (!_panes.TryGetValue(paneId, out var e)) return;
        e.DesiredVisible = visible;
        Apply(e);
    }

    private void Apply(Entry e) => e.Host.SetVisible(e.DesiredVisible && !_suppressed);

    public bool HasPanes => _panes.Count > 0;

    public string? UrlOf(Guid paneId) => _panes.TryGetValue(paneId, out var e) ? e.Url : null;

    public void OnLayout(UrlPaneLayoutMsg msg)
    {
        var id = msg.PaneId;
        var url = msg.Url;
        if (string.IsNullOrEmpty(url)) return;
        // Defense in depth: the page filters with the same policy
        // (web-url.ts), but the host must never create or re-navigate a
        // native browser pane to a scheme outside it (javascript:, data:, a
        // file:// to an .exe, …) even if a malformed message slips through.
        //
        // Rejection is REPORTED, not swallowed — once per pane, not once per
        // layout message (a rejected pane keeps reporting its rect).
        if (!WebUrlPolicy.IsAllowed(url))
        {
            if (_rejected.Add(id))
            {
                Log.Info("UrlPane.reject", $"pane={id:N} url rejected by policy");
                Rejected?.Invoke(id, url!);
            }
            return;
        }
        _rejected.Remove(id);   // a later message may carry a URL we do accept

        if (_panes.TryGetValue(id, out var entry))
        {
            entry.Host.SetBounds(msg.X, msg.Y, msg.W, msg.H);
            if (entry.Url != url) { entry.Host.NavigateIfChanged(url!); entry.Url = url!; }
            return;
        }

        if (_pending.TryGetValue(id, out var pending)) { pending.Layout = msg; return; }
        pending = new Pending { Layout = msg };
        _pending.Add(id, pending);
        Log.Info("UrlPane.create", $"pane={id:N} url={url} rect=({msg.X},{msg.Y},{msg.W},{msg.H})");
        _ = CreateAsync(id, pending);
    }

    private async Task CreateAsync(Guid id, Pending pending)
    {
        bool Current() => _pending.TryGetValue(id, out var current) && ReferenceEquals(current, pending);
        try
        {
            var initial = pending.Layout;
            var host = await _factory.CreateAsync(id, initial.Url!, initial.X, initial.Y, initial.W, initial.H);
            if (host == null)
            {
                Log.Info("UrlPane.create.timeout", $"pane={id:N} engine never became ready");
                return;
            }
            // The pane was closed while we waited — creating now would leave
            // a native view nobody has a handle to.
            await _ui.InvokeAsync(() =>
            {
                if (!Current()) { host.Close(); return; }
                var latest = pending.Layout;
                var entry = new Entry { Host = host, Url = latest.Url!, DesiredVisible = pending.Visible };
                bool Installed() => _panes.TryGetValue(id, out var value) && ReferenceEquals(value, entry);
                host.DocumentTitleChanged += title => _ui.Post(() => { if (Installed()) AutoTitleRequested?.Invoke(id, title); });
                host.NavigationFailed += status => _ui.Post(() => { if (Installed()) Failed?.Invoke(id, status); });
                host.SetVisible(false);
                host.SetBounds(latest.X, latest.Y, latest.W, latest.H);
                host.NavigateIfChanged(latest.Url!);
                _panes[id] = entry;
                Apply(entry);
            });
        }
        catch (Exception ex) { Log.Error("UrlPane.create", ex); }
        finally { await _ui.InvokeAsync(() => { if (Current()) _pending.Remove(id); }); }
    }

    public void OnDispose(PaneRef msg)
    {
        // Clearing _pending first cancels an in-flight create; without it, a
        // pane closed during startup came back as an orphan view.
        _pending.Remove(msg.PaneId);
        _rejected.Remove(msg.PaneId);
        if (!_panes.TryGetValue(msg.PaneId, out var entry)) return;
        try { entry.Host.Close(); } catch { }
        _panes.Remove(msg.PaneId);
    }

    /// Drop every native view without waiting for page messages. Used when
    /// the shared browser process died: the hosts are already dead, this
    /// tears their views down and forgets the (stale) engine.
    public void CloseAll()
    {
        foreach (var e in _panes.Values) { try { e.Host.Close(); } catch { } }
        _panes.Clear();
        _pending.Clear();
        _rejected.Clear();
        _factory.Reset();
    }
}
