using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Perch;

/// Thin wrapper around Velopack's UpdateManager. Headless by design: it only
/// checks / downloads / applies. The "update available" UI lives entirely in
/// the webview (MainWindow posts an `update.available` message and handles the
/// `update.apply` reply; the footer pill is in main.ts / index.html).
///
/// No-ops cleanly when the running copy ISN'T a Velopack install — a dev
/// `dotnet run`, or a copy laid down by anything other than the Velopack
/// Setup.exe. In those cases IsInstalled is false and CheckAsync returns null,
/// so the check never throws on a non-updatable layout and the pill never shows.
internal sealed class UpdateService
{
    // Public GitHub repo → no access token, and stable releases only (no
    // prereleases). The feed is the .nupkg assets `vpk upload github` attaches
    // to each Release.
    private const string RepoUrl = "https://github.com/JosephIris/perch";

    private readonly UpdateManager _mgr;

    // The update discovered by the last CheckAsync, held so DownloadAndApply
    // doesn't have to re-query. Null until a check finds something.
    private UpdateInfo? _pending;

    public UpdateService()
        => _mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));

    /// True only for a real Velopack-installed copy. Dev runs and portable
    /// unzips return false (and every method below short-circuits).
    public bool IsUpdatable => _mgr.IsInstalled;

    /// Check the GitHub feed. Returns the new version string when an update is
    /// available, or null when up to date (or not a Velopack install).
    public async Task<string?> CheckAsync()
    {
        if (!_mgr.IsInstalled) return null;
        _pending = await _mgr.CheckForUpdatesAsync();
        return _pending?.TargetFullRelease?.Version?.ToString();
    }

    /// Download the pending update and relaunch into it. On success the process
    /// is replaced and this never returns; throws if the download fails (the
    /// caller surfaces that to the page so the pill can offer a retry).
    public async Task DownloadAndApplyAsync()
    {
        if (_pending is null) return;
        await _mgr.DownloadUpdatesAsync(_pending);
        _mgr.ApplyUpdatesAndRestart(_pending);
    }
}
