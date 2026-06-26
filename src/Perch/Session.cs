using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Perch;

internal sealed class Session : INotifyPropertyChanged
{
    public Guid Id { get; init; } = Guid.NewGuid();

    private string _title = "session";
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnPropertyChanged(); } }
    }

    /// True while Title is still an auto-derived name (the default "main"/
    /// "session N", or a repo basename pulled from cwd via OSC 7). The
    /// auto-rename pipeline is gated on this — a user-typed rename sets it
    /// false, after which OSC 7 events never overwrite the title.
    /// Persisted so the flag survives restarts. Defaults true on new
    /// sessions; SessionStore.Load runs a one-shot migration that flips
    /// it false for any loaded session whose title doesn't look auto.
    public bool IsAutoTitle { get; set; } = true;

    public string Shell { get; set; } = "";
    public string Cwd { get; set; } = "";

    public PaneNode Root { get; set; } = new();

    // Notification text emitted by an agent or shell script via OSC 9
    // (`printf '\e]9;text\a'`). The session-level row in the sidebar shows
    // this with a colored dot keyed off NotificationLevel. Stays until the
    // next notify call or the pane closes.
    private string _notificationText = "";
    [JsonIgnore]
    public string NotificationText
    {
        get => _notificationText;
        set { if (_notificationText != value) { _notificationText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasNotification)); } }
    }

    private NotificationLevel _notificationLevel = NotificationLevel.Info;
    [JsonIgnore]
    public NotificationLevel NotificationLevel
    {
        get => _notificationLevel;
        set { if (_notificationLevel != value) { _notificationLevel = value; OnPropertyChanged(); OnPropertyChanged(nameof(NotificationDotBrush)); } }
    }

    [JsonIgnore] public bool HasNotification => !string.IsNullOrEmpty(_notificationText);

    // ----- Workspace state pushed by `perch status` / `perch meta` -----
    //
    // All transient: each field reflects what the agent has reported during
    // the current session. They reset to defaults across restarts — the
    // agent's shell hooks re-push them when work resumes. Same lifetime
    // contract as NotificationText. Persisting would invite stale values
    // (a port that's no longer listening, a branch that's been checked out
    // elsewhere) for no real win.

    private AgentState _agentState = AgentState.Idle;
    [JsonIgnore]
    public AgentState AgentState
    {
        get => _agentState;
        set { if (_agentState != value) { _agentState = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasAgentState)); OnPropertyChanged(nameof(AgentStateText)); OnPropertyChanged(nameof(AgentStateBrushKey)); OnPropertyChanged(nameof(HasAgentMeta)); } }
    }

    [JsonIgnore] public bool HasAgentState => _agentState != AgentState.Idle;
    [JsonIgnore] public string AgentStateText => _agentState switch
    {
        Perch.AgentState.Working    => "working",
        Perch.AgentState.Done       => "done",
        Perch.AgentState.Waiting    => "waiting",
        Perch.AgentState.Permission => "permission",
        _                             => "",
    };

    /// Theme-brush resource key for the state pill background. Working uses
    /// the theme accent (it's the "in progress" affordance), Done uses success
    /// (turn finished, your move — calm green), Waiting uses caution (your
    /// feedback wanted), Permission uses critical (agent is blocked on you —
    /// the loudest signal).
    [JsonIgnore] public string AgentStateBrushKey => _agentState switch
    {
        Perch.AgentState.Working    => "AccentFillColorDefaultBrush",
        Perch.AgentState.Done       => "SystemFillColorSuccessBrush",
        Perch.AgentState.Waiting    => "SystemFillColorCautionBrush",
        Perch.AgentState.Permission => "SystemFillColorCriticalBrush",
        _                             => "SubtleFillColorTertiaryBrush",
    };

    private string _activityDetail = "";
    [JsonIgnore]
    public string ActivityDetail
    {
        get => _activityDetail;
        set { if (_activityDetail != value) { _activityDetail = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasActivityDetail)); } }
    }
    [JsonIgnore] public bool HasActivityDetail => !string.IsNullOrEmpty(_activityDetail);

    private string _branch = "";
    [JsonIgnore]
    public string Branch
    {
        get => _branch;
        set { if (_branch != value) { _branch = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasBranch)); OnPropertyChanged(nameof(HasAgentMeta)); } }
    }
    [JsonIgnore] public bool HasBranch => !string.IsNullOrEmpty(_branch);

    private int[] _ports = Array.Empty<int>();
    [JsonIgnore]
    public int[] Ports
    {
        get => _ports;
        set
        {
            var v = value ?? Array.Empty<int>();
            // Identity-compare by sequence so the ItemsControl doesn't churn
            // on every meta push when nothing actually changed.
            if (_ports.Length == v.Length)
            {
                bool same = true;
                for (int i = 0; i < v.Length; i++) if (_ports[i] != v[i]) { same = false; break; }
                if (same) return;
            }
            _ports = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPorts));
            OnPropertyChanged(nameof(HasAgentMeta));
        }
    }
    [JsonIgnore] public bool HasPorts => _ports.Length > 0;

    /// Any of the agent-pushed state fields populated? Controls the
    /// visibility of the chip row in the sidebar so it collapses cleanly
    /// when nothing's been pushed.
    [JsonIgnore] public bool HasAgentMeta => HasAgentState || HasBranch || HasPorts;

    /// Resource key for the colored dot brush — resolved by the sidebar
    /// template against the theme dictionary. Keeps the mapping in one place
    /// and lets the dot pick up theme accent variations.
    [JsonIgnore]
    public string NotificationDotBrush => _notificationLevel switch
    {
        Perch.NotificationLevel.Success => "SystemFillColorSuccessBrush",
        Perch.NotificationLevel.Warn    => "SystemFillColorCautionBrush",
        Perch.NotificationLevel.Error   => "SystemFillColorCriticalBrush",
        _                                  => "AccentFillColorDefaultBrush",
    };

    // "Last activity" timestamp, bumped whenever the session's primary pane
    // emits PTY output. Sidebar uses this to render a relative-time subtitle
    // ("now" / "5m ago" / "idle"), matching perch for macOS's pattern. Updated
    // off the TermPTY.TerminalOutput event — no polling, no buffer copy.
    private DateTime? _lastActivity;
    [JsonIgnore]
    public DateTime? LastActivity
    {
        get => _lastActivity;
        set { if (_lastActivity != value) { _lastActivity = value; OnPropertyChanged(); OnPropertyChanged(nameof(LastActivityRelative)); } }
    }

    [JsonIgnore]
    public string LastActivityRelative
    {
        get
        {
            if (_lastActivity is not DateTime ts) return "";
            var elapsed = DateTime.UtcNow - ts;
            if (elapsed.TotalSeconds < 5)    return "now";
            if (elapsed.TotalSeconds < 60)   return $"{(int)elapsed.TotalSeconds}s ago";
            if (elapsed.TotalMinutes < 60)   return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed.TotalHours < 24)     return $"{(int)elapsed.TotalHours}h ago";
            return $"{(int)elapsed.TotalDays}d ago";
        }
    }

    private bool _isEditing;
    /// True while the sidebar card is showing a TextBox for renaming.
    [JsonIgnore]
    public bool IsEditing
    {
        get => _isEditing;
        set { if (_isEditing != value) { _isEditing = value; OnPropertyChanged(); } }
    }

    [JsonIgnore]
    public string DisplayShell
    {
        get
        {
            var s = string.IsNullOrEmpty(Shell) ? Perch.Shell.DefaultCommandLine() : Shell;
            try { return System.IO.Path.GetFileNameWithoutExtension(s).ToLowerInvariant(); }
            catch { return s; }
        }
    }

    /// Called by the host's relative-time tick — refreshes the sidebar string
    /// without needing the underlying timestamp to change.
    public void RaiseLastActivityRelativeChanged()
        => OnPropertyChanged(nameof(LastActivityRelative));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class PaneNode
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public SplitOrientation? Split { get; set; }
    public List<PaneNode> Children { get; set; } = new();
    /// When set on a leaf, the pane renders a WebView2 loaded with this URL
    /// instead of a terminal. Sessions persisted to disk preserve their
    /// webview panes across restarts.
    public string? Url { get; set; }
    /// User- or auto-assigned name used by `perch focus/send/open` to address
    /// the pane from inside an agent. Persisted so addressing stays stable
    /// across restarts. Only leaves carry names; the field is ignored on
    /// split nodes.
    public string? Name { get; set; }

    /// True while Name is the auto-assigned "pane-N" placeholder OR an
    /// auto-derived title (e.g. a URL pane's website <title>). User-typed
    /// rename via pane.rename flips this false; subsequent URL-pane title
    /// changes won't overwrite. Persisted alongside Name.
    public bool IsAutoName { get; set; } = true;

    /// True once the user manually renames the pane (pane.rename). A
    /// user-named pane is NEVER re-titled by the agent — distinct from
    /// IsAutoName, which a URL pane keeps true so its <title> stays live.
    public bool IsUserNamed { get; set; }

    /// Whether the next agent first-prompt title may (re)name this pane.
    /// Set false once a prompt names it, so only the FIRST prompt of a
    /// Claude session defines the label. Re-enabled on session-start
    /// (new Claude / `/clear`) for any pane the user hasn't named — that's
    /// what lets "ctrl+c twice → relaunch" or `/clear` rename from the new
    /// first message. Persisted so the per-session lock survives a reattach.
    public bool AllowAutoName { get; set; } = true;

    /// The full first-prompt text the auto-name was derived from. Name is a
    /// 40-char label cut from this; the pane header shows the whole thing in
    /// a hover tooltip. Persisted so the tooltip survives restart.
    public string? NamePrompt { get; set; }

    /// Color tag index into the pane palette (0..5). Auto-assigned at
    /// split time, user can edit via the pane header context menu. Persisted
    /// so feature-marking ("the simulator-fix one is yellow") survives
    /// restarts and reattaches. See docs/DESIGN-BIBLE.md once we have the
    /// "Pane tagging" section.
    public int ColorIndex { get; set; }

    /// Relative size weight within the parent split. The web renders each
    /// child with flex-grow == Weight and flex-basis 0, so a child's pixel
    /// share equals Weight / (sum of siblings' Weights). Default 1.0 → even
    /// sizing (identical to the pre-resize behavior). Dragging a split gutter
    /// sends pane.resizeSplit, which rewrites the two adjacent children's
    /// Weights. Persisted so a custom layout survives restart; applied by the
    /// web on every DOM rebuild (treeSignature deliberately ignores it so a
    /// pure weight change never forces a rebuild). Carried on both leaves and
    /// split nodes — a split node's Weight is its share inside ITS parent.
    public double Weight { get; set; } = 1.0;

    // ----- Per-pane transient state (set by perch notify/status/meta) ------
    // All JsonIgnore: these are pushed by the running agent each time it
    // takes a turn. Persisting would invite stale values across restarts.
    // Multiple panes per session each carry their own state (previously
    // these lived on Session — last-writer-wins thrashed when two agents
    // ran simultaneously). Sidebar SessionView aggregates per-pane state
    // for the row indicator.

    [JsonIgnore] public AgentState AgentState { get; set; } = AgentState.Idle;
    /// True when the CURRENT AgentState was set by the idle watchdog (output
    /// silence) rather than an authoritative agent hook. Lets the watchdog
    /// re-promote its own Working→Done guess back to Working when output
    /// resumes, WITHOUT ever overriding a real Stop-hook "done". Any hook that
    /// sets state clears this back to false.
    [JsonIgnore] public bool StateInferred { get; set; }
    /// Which agent is running in this pane: "claude" (Claude Code), "codex",
    /// or "" (plain shell / unknown). Set from the agent's session-start hook
    /// (the claude.cmd wrapper makes "claude" definitive); cleared on session
    /// end. Transient — re-pushed by the agent each session. Surfaces as a
    /// small badge in the pane header so you can tell CC panes from the rest.
    [JsonIgnore] public string AgentType { get; set; } = "";
    [JsonIgnore] public string ActivityDetail { get; set; } = "";
    [JsonIgnore] public string NotificationText { get; set; } = "";
    [JsonIgnore] public NotificationLevel NotificationLevel { get; set; } = NotificationLevel.Info;
    [JsonIgnore] public string Branch { get; set; } = "";
    [JsonIgnore] public int[] Ports { get; set; } = Array.Empty<int>();

    /// HEAD sha captured at the start of an agent session (Claude Code's
    /// session-start hook). Empty when no session is active. CommitCount is
    /// recomputed each time the agent reports a state change.
    [JsonIgnore] public string CommitBaseline { get; set; } = "";
    [JsonIgnore] public int CommitCount { get; set; }

    /// Diff size since the agent's baseline (committed + uncommitted), and
    /// how many commits are ahead of upstream. Recomputed on the same git
    /// path as CommitCount; surface the "what it changed / what's unpushed"
    /// signal in the sidebar idle line and dashboard cards.
    [JsonIgnore] public int LinesAdded { get; set; }
    [JsonIgnore] public int LinesDeleted { get; set; }
    [JsonIgnore] public int FilesChanged { get; set; }
    [JsonIgnore] public int Ahead { get; set; }

    /// Unix-ms timestamp the pane entered its current Working spell, so the
    /// UI can tick "working · 2m". 0 whenever the pane isn't working. Wall
    /// clock (not Stopwatch) because the page compares against Date.now() on
    /// the same machine.
    [JsonIgnore] public long TurnStartUnixMs { get; set; }

    [JsonIgnore] public bool IsLeaf => Split == null;
    [JsonIgnore] public bool IsWebView => IsLeaf && !string.IsNullOrEmpty(Url);
    [JsonIgnore] public bool HasNotification => !string.IsNullOrEmpty(NotificationText);
}

internal enum SplitOrientation { Horizontal, Vertical }

// Idle      → no agent activity (hollow dot).
// Working   → agent is running / thinking (blue).
// Done      → agent finished its turn; the ball is in your court but it isn't
//             BLOCKED — "give me the next task" (green, calm, no pulse). Maps
//             from Claude's Stop hook, and is what the idle watchdog demotes a
//             silent Working pane to when a Stop is missed.
// Waiting   → agent is actively waiting on YOUR feedback — the 60s idle nudge
//             after you ignore a finished turn (yellow, gentle pulse). Maps
//             from Claude's idle Notification. Louder than Done, quieter than
//             Permission.
// Permission→ agent is BLOCKED on a permission prompt and can't proceed
//             without you (red, louder). Maps from a permission Notification.
internal enum AgentState { Idle, Working, Done, Waiting, Permission }

/// Severity for sidebar notification pills. Previously lived in OscParser.cs
/// alongside the OSC 9 wire parser; the webview rewrite parses OSC sequences
/// in JS (xterm.js exposes hooks) and pushes them back via WebMessageReceived,
/// but the host-side Session model still needs this enum to render colored
/// dots in the sidebar.
internal enum NotificationLevel { Info, Success, Warn, Error }
