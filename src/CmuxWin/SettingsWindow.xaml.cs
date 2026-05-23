using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Wpf.Ui.Controls;

namespace CmuxWin;

public partial class SettingsWindow : FluentWindow
{
    private readonly Settings _settings;

    // Maps the dropdown items back to their command lines. "" = auto-detect (first entry).
    private List<Shell.ShellChoice> _shells = new();

    /// True if the user clicked Save. Lets the caller decide whether to apply.
    public bool Saved { get; private set; }

    public SettingsWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();

        PopulateFonts();
        PopulateShells();
        FontSizeBox.Value = settings.FontSize;
        UpdatePreview();
    }

    private void OnPreviewChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewText == null) return;
        if (FontFamilyBox?.SelectedItem is string ff)
            PreviewText.FontFamily = new System.Windows.Media.FontFamily($"{ff}, Consolas");
        if (FontSizeBox?.Value is double v && v >= 8 && v <= 32)
            PreviewText.FontSize = v;
    }

    private void PopulateFonts()
    {
        // Limit to mono families commonly installed on Windows so users don't pick proportional fonts.
        var candidates = new[]
        {
            "Cascadia Code", "Cascadia Mono", "Consolas",
            "JetBrains Mono", "Fira Code", "Source Code Pro",
            "Lucida Console", "Courier New",
        };
        var installed = new HashSet<string>(
            System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source),
            System.StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
            if (installed.Contains(c))
                FontFamilyBox.Items.Add(c);

        FontFamilyBox.SelectedItem = FontFamilyBox.Items.Cast<object>()
            .FirstOrDefault(o => string.Equals(o?.ToString(), _settings.FontFamily, System.StringComparison.OrdinalIgnoreCase))
            ?? (FontFamilyBox.Items.Count > 0 ? FontFamilyBox.Items[0] : null);
    }

    private void PopulateShells()
    {
        _shells = Shell.DetectedShells().ToList();
        ShellBox.Items.Add("Auto-detect");
        foreach (var s in _shells) ShellBox.Items.Add(s.Name);

        if (string.IsNullOrEmpty(_settings.DefaultShell))
        {
            ShellBox.SelectedIndex = 0;
        }
        else
        {
            var match = _shells.FindIndex(s => string.Equals(s.CommandLine, _settings.DefaultShell, System.StringComparison.OrdinalIgnoreCase));
            ShellBox.SelectedIndex = match >= 0 ? match + 1 : 0;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (FontFamilyBox.SelectedItem is string ff) _settings.FontFamily = ff;

        // NumberBox.Value is double? — handle null + non-double tolerantly.
        double? rawSize = FontSizeBox.Value;
        if (rawSize.HasValue && rawSize.Value >= 8 && rawSize.Value <= 32)
            _settings.FontSize = (int)System.Math.Round(rawSize.Value);

        if (ShellBox.SelectedIndex <= 0) _settings.DefaultShell = "";
        else _settings.DefaultShell = _shells[ShellBox.SelectedIndex - 1].CommandLine;

        _settings.Save();
        Saved = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Saved = false;
        Close();
    }
}
