using System;
using System.Linq;
using System.Threading.Tasks;

namespace Perch;

public partial class App : System.Windows.Application
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

    private const int STD_INPUT_HANDLE  = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE  = -12;

    public App()
    {
        // Detach from any inherited console state BEFORE we ever spawn a
        // shell. The danger path: a console parent (`dotnet run`, bash,
        // Tabby) launches us with STARTF_USESTDHANDLES forwarding its
        // stdin/stdout/stderr. Our WPF process never uses them, but they
        // sit in our PEB. When we later spawn cmd.exe with
        // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE the docs say the child gets
        // the pseudoconsole's pipes, but in practice (Win11 26200) cmd
        // appears to favor whatever's in the parent PEB and exits with
        // code 0 in ~50ms (it sees the wrong stdin and reads EOF).
        //
        // FreeConsole alone isn't enough -- it releases the attached console
        // but doesn't clear the PEB stdio. Wiping the std handles to
        // INVALID_HANDLE_VALUE (-1) blanks the slate.
        //
        // Both calls are no-ops when launched cleanly (Explorer / installer)
        // because we have no console and no inherited stdio.
        try { FreeConsole(); } catch { }
        var INVALID_HANDLE_VALUE = new IntPtr(-1);
        try { SetStdHandle(STD_INPUT_HANDLE,  INVALID_HANDLE_VALUE); } catch { }
        try { SetStdHandle(STD_OUTPUT_HANDLE, INVALID_HANDLE_VALUE); } catch { }
        try { SetStdHandle(STD_ERROR_HANDLE,  INVALID_HANDLE_VALUE); } catch { }

        // Anchor our process to a job so any conhost/OpenConsole/shell children
        // (started by ConPTY) are reaped automatically when we exit, including
        // ungraceful exits.
        JobObjectGuard.AssignSelfToKillOnCloseJob();

        // Make `perch` resolvable inside spawned panes. ConPTY children inherit
        // our process env, so prepending PATH here propagates to every pane
        // shell without any per-shell flag plumbing.
        // The build target drops perch.exe into <app>/tools/.
        try
        {
            var appDir = System.AppContext.BaseDirectory;
            var toolsDir = System.IO.Path.Combine(appDir, "tools");
            if (System.IO.Directory.Exists(toolsDir))
            {
                var current = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (!current.Split(';').Any(p => string.Equals(p?.Trim(), toolsDir, StringComparison.OrdinalIgnoreCase)))
                    Environment.SetEnvironmentVariable("PATH", toolsDir + ";" + current);
            }
        }
        catch (Exception ex) { Log.Error("PATH.perchTools", ex); }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Dispatcher.UnhandledException", e.Exception);
            e.Handled = true;
        };
    }
}
