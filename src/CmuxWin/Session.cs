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

    private string _preview = "";
    [JsonIgnore]
    public string Preview
    {
        get => _preview;
        set { if (_preview != value) { _preview = value; OnPropertyChanged(); } }
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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class PaneNode
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public SplitOrientation? Split { get; set; }
    public List<PaneNode> Children { get; set; } = new();
    [JsonIgnore] public bool IsLeaf => Split == null;
}

internal enum SplitOrientation { Horizontal, Vertical }
