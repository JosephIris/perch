using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace CmuxWin;

public partial class App : System.Windows.Application
{
    static App()
    {
        // EasyWindowsTerminalControl declares its CreatePseudoConsole/Close/Resize
        // P/Invokes against "conpty.dll". That file only exists on machines with
        // Windows Terminal / OpenConsole installed — the actual OS exports live in
        // kernel32.dll on Windows 10 1809+. Without this redirect, a clean Windows
        // machine throws DllNotFoundException inside EasyTerminalControl's
        // Task.Run(() => term.Start(...)); the exception is unobserved, the
        // connection never starts, and the terminal HWND renders black with no shell.
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) =>
        {
            if (e.LoadedAssembly.GetName().Name == "EasyWindowsTerminalControl")
                InstallConPtyResolver(e.LoadedAssembly);
        };
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            if (a.GetName().Name == "EasyWindowsTerminalControl")
                InstallConPtyResolver(a);
    }

    private static void InstallConPtyResolver(Assembly target)
    {
        try
        {
            NativeLibrary.SetDllImportResolver(target, (name, asm, search) =>
                string.Equals(name, "conpty.dll", StringComparison.OrdinalIgnoreCase)
                    ? NativeLibrary.Load("kernel32.dll", asm, search)
                    : IntPtr.Zero);
        }
        catch (InvalidOperationException) { /* resolver already set */ }
    }

    public App()
    {
        // Anchor our process to a job so any conhost/OpenConsole/shell children
        // are reaped automatically when we exit, including ungraceful exits.
        JobObjectGuard.AssignSelfToKillOnCloseJob();

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
