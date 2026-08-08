using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Perch;

/// Velopack updater for the mac build — the same headless wrapper as the
/// Windows UpdateService minus the Windows-only concerns (no MSIX to defer
/// to, no kill-on-close job for Update to break away from). No-ops cleanly
/// on a dev `dotnet run` (IsInstalled false → the pill never shows).
internal sealed class MacUpdateService : IUpdateService
{
    private const string RepoUrl = "https://github.com/JosephIris/perch";

    private readonly UpdateManager _mgr;
    private UpdateInfo? _pending;

    public MacUpdateService()
        => _mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));

    public bool IsUpdatable
    {
        get { try { return _mgr.IsInstalled; } catch { return false; } }
    }

    public string? CurrentVersion
    {
        get { try { return _mgr.CurrentVersion?.ToString(); } catch { return null; } }
    }

    public async Task<string?> CheckAsync()
    {
        if (!IsUpdatable) return null;
        _pending = await _mgr.CheckForUpdatesAsync();
        return _pending?.TargetFullRelease?.Version?.ToString();
    }

    public async Task DownloadAndApplyAsync()
    {
        if (_pending is null) return;
        await _mgr.DownloadUpdatesAsync(_pending);
        _mgr.ApplyUpdatesAndRestart(_pending);
    }
}
