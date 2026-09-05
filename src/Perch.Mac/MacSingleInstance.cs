using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Perch;

/// The mac twin of the WPF host's SingleInstance: one Perch per data
/// directory, and a second launch focuses the window that is already there.
///
/// Same reason it exists on Windows — two live instances race on
/// sessions.json and the last writer silently discards the other's state —
/// and the same scoping: keyed on the resolved data root, so test harnesses
/// pointing PERCH_DATA_DIR at a scratch dir run alongside the user's window.
///
/// Mechanics differ because there is no named mutex worth trusting here: the
/// guard is an exclusive `flock` on `<data>/perch/instance.lock`, which .NET
/// takes for FileShare.None on Unix and the kernel drops the moment the owner
/// exits, crash included. The owner's pid sits in a sibling file (the locked
/// file itself can't be read while the lock is held) so the loser knows whom
/// to bring forward.
internal static class MacSingleInstance
{
    // Held for the lifetime of the process, never disposed on purpose.
    private static FileStream? _held;

    /// True when this process owns the data root and startup should continue.
    /// Every failure path returns true: a guard that can't tell whether it is
    /// alone must let the app start.
    public static bool TryAcquire()
    {
        string lockPath, pidPath;
        try
        {
            var dir = Path.Combine(AppPaths.DataRoot, "perch");
            Directory.CreateDirectory(dir);
            lockPath = Path.Combine(dir, "instance.lock");
            pidPath = Path.Combine(dir, "instance.pid");
        }
        catch (Exception ex)
        {
            Log.Error("SingleInstance.Key", ex);
            return true;
        }

        try
        {
            _held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try { File.WriteAllText(pidPath, Environment.ProcessId.ToString()); } catch { }
            return true;
        }
        catch (IOException)
        {
            // flock refused: someone else owns the data dir.
        }
        catch (Exception ex)
        {
            Log.Error("SingleInstance.Lock", ex);
            return true;
        }

        Log.Info("SingleInstance.deferred", "another Perch already owns this data dir; focusing it");
        try
        {
            if (int.TryParse(File.ReadAllText(pidPath).Trim(), out var pid) && pid > 0 && pid != Environment.ProcessId)
                FocusExisting(pid);
        }
        catch (Exception ex) { Log.Error("SingleInstance.Focus", ex); }
        return false;
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr ObjcGetClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr SelRegisterName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjcMsgSendInt(IntPtr receiver, IntPtr selector, int arg);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern bool ObjcMsgSendBoolULong(IntPtr receiver, IntPtr selector, ulong arg);

    private const ulong NSApplicationActivateIgnoringOtherApps = 1UL << 1;

    /// NSRunningApplication for the owner's pid → activate. AppKit isn't
    /// loaded yet this early in startup (Photino loads it later), so load it
    /// explicitly; if anything about that fails, fall back to System Events.
    private static void FocusExisting(int pid)
    {
        try
        {
            NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
            var cls = ObjcGetClass("NSRunningApplication");
            if (cls != IntPtr.Zero)
            {
                var app = ObjcMsgSendInt(cls, SelRegisterName("runningApplicationWithProcessIdentifier:"), pid);
                if (app != IntPtr.Zero &&
                    ObjcMsgSendBoolULong(app, SelRegisterName("activateWithOptions:"), NSApplicationActivateIgnoringOtherApps))
                    return;
            }
        }
        catch (Exception ex) { Log.Error("SingleInstance.activate", ex); }

        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"tell application \"System Events\" to set frontmost of (first process whose unix id is {pid}) to true");
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch (Exception ex) { Log.Error("SingleInstance.osascript", ex); }
    }
}
