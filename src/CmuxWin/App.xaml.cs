using System;
using System.Linq;
using System.Threading.Tasks;

namespace CmuxWin;

public partial class App : System.Windows.Application
{
    public App()
    {
        // Anchor our process to a job so any conhost/OpenConsole/shell children
        // (started by ConPTY) are reaped automatically when we exit, including
        // ungraceful exits.
        JobObjectGuard.AssignSelfToKillOnCloseJob();

        // Make `cmux` resolvable inside spawned panes. ConPTY children inherit
        // our process env, so prepending PATH here propagates to every pane
        // shell without any per-shell flag plumbing.
        // The build target drops cmux.exe into <app>/tools/.
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
        catch (Exception ex) { Log.Error("PATH.cmuxTools", ex); }

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
