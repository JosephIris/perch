using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Photino.NET;

namespace Perch;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
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

        AppController? app = null;
        ui.InvokeAsync(() =>
        {
            app = new AppController(
                web: host,
                host: host,
                ui: ui,
                ptyFactory: new UnixPtyFactory(),
                probe: new MacSystemProbe(),
                urlPanes: null,       // URL panes: WKWebView subviews, later
                updates: null);       // auto-update: Sparkle-or-equivalent, later
        }).GetAwaiter().GetResult();

        window.RegisterFocusInHandler((_, _) => ui.Post(() => app!.OnActivated()));
        window.RegisterSizeChangedHandler((_, _) => ui.Post(() => app!.OnWindowResized()));
        window.RegisterWindowClosingHandler((_, _) =>
        {
            try { ui.InvokeAsync(() => app!.Shutdown()).Wait(5000); }
            catch (Exception ex) { Log.Error("Shutdown", ex); }
            return false; // don't cancel the close
        });
        window.RegisterWindowCreatedHandler((_, _) => ui.Post(() => _ = app!.StartAsync()));

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled", e.ExceptionObject as Exception ?? new Exception($"{e.ExceptionObject}"));

        window.WaitForClose();
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
