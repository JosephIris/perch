using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perch;

public sealed class Settings
{
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1040;
    public double WindowHeight { get; set; } = 640;
    public bool WindowMaximized { get; set; } = false;
    public string FontFamily { get; set; } = "Cascadia Code";
    public int FontSize { get; set; } = 13;

    /// User-chosen default shell command line. Empty = auto-detect via Shell.DetectedShells().
    public string DefaultShell { get; set; } = "";

    /// Whether the first-launch onboarding lightbox has been shown and
    /// dismissed. False on a fresh install → the webview auto-opens the
    /// welcome once; the "Show welcome" button in Settings reopens it anytime
    /// (without clearing this).
    public bool OnboardingSeen { get; set; } = false;

    /// Auto-resume Claude Code sessions on launch / on restoring a closed
    /// session. When true (default), a pane that carries a saved Claude session
    /// id offers to `claude --resume <id>` instead of dropping to a bare shell.
    /// The launch resume is still gated by a one-time prompt; this flag is the
    /// master switch (off = never resume, never prompt). Mirrors upstream
    /// perch's "Reopen Previous Session" toggle.
    public bool ResumeAgentsOnLaunch { get; set; } = true;

    /// Working directory used when a session has no recorded Cwd yet.
    /// Defaults to the user's profile directory so we never land in the install
    /// folder (Program Files / AppData). Resolved lazily so an empty stored
    /// value falls back to %USERPROFILE% at use time.
    public string DefaultCwd { get; set; } = "";

    public string ResolveDefaultCwd()
    {
        if (!string.IsNullOrWhiteSpace(DefaultCwd) && Directory.Exists(DefaultCwd))
            return DefaultCwd;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? @"C:\Users" : profile;
    }

    private static string SettingsPath =>
        Path.Combine(
            AppPaths.DataRoot,
            "perch",
            "settings.json");

    public static Settings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path)) return new Settings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.Settings) ?? new Settings();
        }
        catch
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(this, SettingsJsonContext.Default.Settings);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Settings persistence is best-effort; don't crash on shutdown.
        }
    }
}

[JsonSerializable(typeof(Settings))]
// AllowNamedFloatingPointLiterals so WindowLeft/Top = NaN (the "no recorded
// position yet" sentinel) round-trips as the JSON token "NaN" instead of
// throwing inside Save(). Without it the whole settings file silently
// fails to write on first save, since the Save() catch swallows.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
public partial class SettingsJsonContext : JsonSerializerContext { }
