using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Perch;

/// Guards against two Perch windows sharing one data directory.
///
/// This matters because of the dual distribution channel. The Store (MSIX) copy
/// and the Velopack copy are two independent installs on disk, and Windows does
/// NOT hand the packaged copy a private AppData: since Windows 10 1903 a
/// packaged desktop app writes straight through to the real user AppData, so
/// both copies resolve AppPaths.DataRoot to the same %APPDATA%\perch. Two live
/// instances then race on sessions.json and the last writer wins, silently
/// discarding the other's state. Verified 2026-07-27 by installing the MSIX
/// alongside the Velopack build: the packaged window listed the unpackaged
/// install's real sessions, and no package-local copy was ever created.
///
/// The mutex is keyed on the resolved data root rather than being global, so
/// genuinely isolated instances still coexist: every test script points
/// PERCH_DATA_DIR at a scratch dir (scripts/run-test-instance.ps1) and those
/// must keep running alongside the user's real window.
internal static class SingleInstance
{
    // Held for the lifetime of the process. Never disposed on purpose: the
    // handle closes when we exit (including on a crash), which releases the
    // mutex and lets the next launch through.
    private static Mutex? _held;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    /// True when this process owns the data root and startup should continue.
    /// False when another instance already owns it, in which case that window is
    /// brought to the front first so the click that launched us still does
    /// something visible.
    ///
    /// Every failure path returns true. A guard that can't tell whether it's
    /// alone must let the app start: two windows is a bad day, no window at all
    /// is a broken install.
    public static bool TryAcquire()
    {
        string key;
        try
        {
            var root = Path.GetFullPath(AppPaths.DataRoot).TrimEnd('\\').ToLowerInvariant();
            key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16];
        }
        catch (Exception ex)
        {
            Log.Error("SingleInstance.Key", ex);
            return true;
        }

        try
        {
            // Local\ scopes the name to the logon session, which matches the
            // scope of AppPaths.DataRoot (per-user). A Global\ name would make
            // two different users' Perches fight over one mutex.
            _held = new Mutex(initiallyOwned: true, $@"Local\Perch.Instance.{key}", out bool created);
            if (created) return true;
        }
        catch (Exception ex)
        {
            Log.Error("SingleInstance.Mutex", ex);
            return true;
        }

        Log.Info("SingleInstance.deferred", "another Perch already owns this data dir; focusing it");
        try { FocusExisting(); } catch (Exception ex) { Log.Error("SingleInstance.Focus", ex); }
        return false;
    }

    /// Best effort: bring some other Perch window to the front. We can't map the
    /// mutex back to a PID, so a machine also running an isolated test instance
    /// could get the wrong window focused. That's cosmetic, and the alternative
    /// (exiting with no visible effect) is worse.
    private static void FocusExisting()
    {
        var me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("Perch"))
        {
            using (p)
            {
                if (p.Id == me) continue;
                var hwnd = p.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                return;
            }
        }
    }
}
