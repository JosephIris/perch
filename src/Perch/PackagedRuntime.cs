using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Perch;

/// Detects whether this process runs with MSIX package identity — i.e. it was
/// installed from the Microsoft Store (or sideloaded from an .msix) — as opposed
/// to a Velopack install, a portable unzip, or a dev `dotnet run`.
///
/// The distinction drives who owns updates and signing. The Store channel signs
/// the package and delivers updates itself, so the in-app Velopack updater must
/// stand down when packaged (see UpdateService) rather than fight the Store's
/// own update mechanism.
internal static class PackagedRuntime
{
    // GetCurrentPackageFullName returns APPMODEL_ERROR_NO_PACKAGE when the
    // process has no package identity; anything else (success, or the expected
    // ERROR_INSUFFICIENT_BUFFER for our null buffer) means we're packaged.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    private static readonly Lazy<bool> _isPackaged = new(Detect);

    /// True only when launched from an installed MSIX package.
    public static bool IsPackaged => _isPackaged.Value;

    private static bool Detect()
    {
        try
        {
            int len = 0;
            return GetCurrentPackageFullName(ref len, null) != APPMODEL_ERROR_NO_PACKAGE;
        }
        catch (EntryPointNotFoundException)
        {
            // API absent on pre-Win8; we target Win10+, so treat as unpackaged.
            return false;
        }
    }
}
