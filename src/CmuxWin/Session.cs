using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace CmuxWin;

internal sealed class Session : INotifyPropertyChanged
{
    public Guid Id { get; init; } = Guid.NewGuid();

    private string _title = "session";
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnPropertyChanged(); } }
    }

    public string Shell { get; set; } = "";
    public string Cwd { get; set; } = "";

    public PaneNode Root { get; set; } = new();

    // "Last activity" timestamp, bumped whenever the session's primary pane
    // emits PTY output. Sidebar uses this to render a relative-time subtitle
    // ("now" / "5m ago" / "idle"), matching cmux for macOS's pattern. Updated
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
            var s = string.IsNullOrEmpty(Shell) ? CmuxWin.Shell.DefaultCommandLine() : Shell;
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
    [JsonIgnore] public bool IsLeaf => Split == null;
    [JsonIgnore] public bool IsWebView => IsLeaf && !string.IsNullOrEmpty(Url);
}

internal enum SplitOrientation { Horizontal, Vertical }
