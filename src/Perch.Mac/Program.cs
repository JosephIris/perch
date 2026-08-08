using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Photino.NET;
using Velopack;

namespace Perch;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack hook entry — must run before anything else so install/
        // update/uninstall invocations short-circuit (same as the WPF App).
        VelopackApp.Build().Run();

        // Thumbnails via sips (bundled with macOS) — assigned before any
        // controller can ask for one.
        ImageThumb.Codec = SipsCodec.JpegBase64;

        // Prepend the bundled tools dir (perch CLI + claude/codex shims) to
        // PATH so every pane shell inherits it — same trick as the Windows
        // App constructor, with ':' for ';'.
        var toolsDir = Path.Combine(AppContext.BaseDirectory, "tools");
        try
        {
            if (Directory.Exists(toolsDir))
            {
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (!path.Split(':').Contains(toolsDir))
                    Environment.SetEnvironmentVariable("PATH", toolsDir + ":" + path);
            }
        }
        catch (Exception ex) { Log.Error("Startup.path", ex); }

        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        using var server = new StaticServer(webRoot);
        var ui = new AppDispatcher();

        var window = new PhotinoWindow()
            .SetTitle("perch")
            .SetUseOsDefaultLocation(true)
            // Photino defaults UseOsDefaultSize=true, which silently ignores
            // SetSize — the window came up 800×572 until this was disabled.
            .SetUseOsDefaultSize(false)
            .SetSize(new System.Drawing.Size(1040, 640))
            .SetMinSize(640, 360)
            .SetContextMenuEnabled(false)
            .SetGrantBrowserPermissions(true)
            .SetJavascriptClipboardAccessEnabled(true)
            .SetDevToolsEnabled(
#if DEBUG
                true
#else
                ControlIpcServer.IsEnabled
#endif
            )
            .SetLogVerbosity(0);

        // Photino validates that a start URL exists before its native loop
        // starts; AppController's own NavigateToApp (from StartAsync) lands
        // after WindowCreated, which is too late for that check. Same URL,
        // so the controller's navigate is a no-op reload at worst.
        if (Directory.Exists(webRoot)) window.Load($"{server.BaseUrl}/index.html");
        else window.LoadRawString("<html><body>web bundle missing</body></html>");

        var host = new MacHost(window, server, ui, webRoot);

        var urlPanes = new UrlPanes(ui, new MacUrlPaneHostFactory(window));

        AppController? app = null;
        ui.InvokeAsync(() =>
        {
            app = new AppController(
                web: host,
                host: host,
                ui: ui,
                ptyFactory: new UnixPtyFactory(),
                probe: new MacSystemProbe(),
                urlPanes: urlPanes,
                updates: new MacUpdateService());
        }).GetAwaiter().GetResult();

        window.RegisterFocusInHandler((_, _) => ui.Post(() => app!.OnActivated()));
        window.RegisterSizeChangedHandler((_, _) => ui.Post(() => app!.OnWindowResized()));
        window.RegisterWindowClosingHandler((_, _) =>
        {
            SaveWindowPlacement(window, app!, ui);
            try { ui.InvokeAsync(() => app!.Shutdown()).Wait(5000); }
            catch (Exception ex) { Log.Error("Shutdown", ex); }
            return false; // don't cancel the close
        });
        window.RegisterWindowCreatedHandler((_, _) =>
        {
            host.ApplyMacChrome();
            RestoreWindowPlacement(window, app!, ui);
            host.StartClipboardWatcher(() => ui.Post(() => app!.OnClipboardChanged()));
            ui.Post(() => _ = app!.StartAsync());
        });

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled", e.ExceptionObject as Exception ?? new Exception($"{e.ExceptionObject}"));

        window.WaitForClose();
    }

    /// Same policy as the WPF host: size is always safe to restore; the
    /// position only comes back when the saved rect still intersects the
    /// desktop enough to grab (WindowPlacement.IsReachable — monitors come
    /// and go). Settings values are AppKit points here, WPF DIPs on Windows;
    /// both are the platform's logical unit so the fields are per-platform.
    private static void RestoreWindowPlacement(PhotinoWindow window, AppController app, AppDispatcher ui)
    {
        try
        {
            Settings s = null!;
            ui.InvokeAsync(() => s = app.SettingsRef).Wait(2000);
            if (s == null) return;
            if (s.WindowWidth >= 640 && s.WindowHeight >= 360)
                window.SetSize(new System.Drawing.Size((int)s.WindowWidth, (int)s.WindowHeight));

            // Virtual screen = union of every monitor's frame, in points.
            double left = 0, top = 0, right = 0, bottom = 0;
            var first = true;
            foreach (var m in window.Monitors)
            {
                var a = m.MonitorArea;
                if (first) { left = a.X; top = a.Y; right = a.X + a.Width; bottom = a.Y + a.Height; first = false; }
                else
                {
                    left = Math.Min(left, a.X); top = Math.Min(top, a.Y);
                    right = Math.Max(right, a.X + a.Width); bottom = Math.Max(bottom, a.Y + a.Height);
                }
            }
            var screen = new ScreenRect(left, top, right - left, bottom - top);
            if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop) && !first &&
                WindowPlacement.IsReachable(
                    new ScreenRect(s.WindowLeft, s.WindowTop, s.WindowWidth, s.WindowHeight), screen))
                window.SetLocation(new System.Drawing.Point((int)s.WindowLeft, (int)s.WindowTop));

            if (s.WindowMaximized) window.SetMaximized(true);
        }
        catch (Exception ex) { Log.Error("RestoreWindowPlacement", ex); }
    }

    private static void SaveWindowPlacement(PhotinoWindow window, AppController app, AppDispatcher ui)
    {
        try
        {
            // Read geometry on the Photino thread (we're in its closing
            // handler), then mutate settings on the app thread.
            var maximized = window.Maximized;
            double l = window.Left, t = window.Top, w = window.Width, h = window.Height;
            ui.InvokeAsync(() =>
            {
                var s = app.SettingsRef;
                // A maximized window reports its maximized geometry; keep the
                // last windowed rect instead so un-maximizing next launch
                // doesn't restore full-screen-sized "windowed" bounds.
                if (!maximized && w >= 640 && h >= 360)
                {
                    s.WindowLeft = l; s.WindowTop = t;
                    s.WindowWidth = w; s.WindowHeight = h;
                }
                s.WindowMaximized = maximized;
                s.Save();
            }).Wait(2000);
        }
        catch (Exception ex) { Log.Error("SaveWindowPlacement", ex); }
    }

    private static bool Contains(this string[] arr, string v)
    {
        foreach (var s in arr) if (s == v) return true;
        return false;
    }
}

/// ImageThumb backend over `sips`, macOS's built-in scriptable image tool:
/// decode anything ImageIO can read, cap the long edge, re-encode JPEG.
internal static class SipsCodec
{
    public static string? JpegBase64(byte[] bytes, int maxEdge)
    {
        var tmpIn = Path.Combine(Path.GetTempPath(), $"perch-thumb-{Guid.NewGuid():N}");
        var tmpOut = tmpIn + ".jpg";
        try
        {
            File.WriteAllBytes(tmpIn, bytes);
            var psi = new ProcessStartInfo("/usr/bin/sips")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[]
                     { "-Z", maxEdge.ToString(), "-s", "format", "jpeg",
                       "-s", "formatOptions", "80", tmpIn, "--out", tmpOut })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10000) || p.ExitCode != 0 || !File.Exists(tmpOut)) return null;
            return Convert.ToBase64String(File.ReadAllBytes(tmpOut));
        }
        catch { return null; }
        finally
        {
            try { File.Delete(tmpIn); } catch { }
            try { File.Delete(tmpOut); } catch { }
        }
    }
}
