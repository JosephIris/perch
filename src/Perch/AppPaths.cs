using System;

namespace Perch;

/// Single source of truth for the root under which all per-user app data lives
/// (the session store, settings, WebView2 user-data folder, and error log all
/// sit in "<DataRoot>\perch\…").
///
/// Honors the PERCH_DATA_DIR env var when set, falling back to roaming AppData.
/// The override exists for isolated runs (screenshot/visual checks, tests) that
/// must NOT read or write the user's real session store. NOTE: setting the
/// APPDATA env var does NOT work for this — .NET's GetFolderPath(ApplicationData)
/// resolves the Windows *known-folder* path and ignores APPDATA — so a dedicated
/// override variable is the only reliable way to redirect our data dir.
internal static class AppPaths
{
    public static string DataRoot
    {
        get
        {
            var over = Environment.GetEnvironmentVariable("PERCH_DATA_DIR");
            return string.IsNullOrWhiteSpace(over)
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : over;
        }
    }
}
