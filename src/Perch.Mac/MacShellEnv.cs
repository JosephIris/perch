using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Perch;

/// The PATH a Finder/Dock launch does NOT have.
///
/// launchd starts GUI apps with `/usr/bin:/bin:/usr/sbin:/sbin:/usr/local/bin`
/// and nothing the user's shell adds — no ~/.local/bin (where the claude
/// installer puts its binary), no Homebrew on Apple silicon, no nvm, no
/// cargo. Anything Perch itself starts inherits that: the `claude` a new
/// project tab, a resume or a team bot runs as its pane's initial command
/// (it goes through `sh -c` BEFORE the interactive shell sources any rc
/// file), the headless `claude -p` jobs the room uses for briefs, `git`,
/// `codex`. The bundled shim then reports "real `claude` binary not found on
/// PATH" and the pane falls into a bare shell — which is exactly how "starting
/// a Claude in a session doesn't work" presented, while a `claude` typed into
/// a pane by hand kept working because zsh had run ~/.zshrc by then.
///
/// Same approach as VS Code's shell-environment resolution: ask the user's
/// login shell, interactively, what PATH ends up as, and adopt it. Bounded by
/// a timeout so a slow rc file costs at most a couple of seconds of startup
/// and never blocks it.
internal static class MacShellEnv
{
    private const string Begin = "PERCH_PATH_BEGIN";
    private const string End = "PERCH_PATH_END";

    /// Merge the login shell's PATH into this process's PATH: the shell's
    /// entries first (in its order), then anything we already had that it
    /// lacks. No-op on any failure. Returns what was adopted, for the log.
    public static string? AdoptLoginShellPath(int timeoutMs = 4000)
    {
        // A harness or a dev shell can pin PATH deliberately.
        if (Environment.GetEnvironmentVariable("PERCH_NO_SHELL_ENV") is { Length: > 0 }) return null;
        var resolved = ResolveLoginShellPath(timeoutMs);
        if (string.IsNullOrWhiteSpace(resolved)) return null;

        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        var merged = new System.Collections.Generic.List<string>();
        foreach (var part in resolved.Split(':', StringSplitOptions.RemoveEmptyEntries))
            if (!merged.Contains(part)) merged.Add(part);
        foreach (var part in current.Split(':', StringSplitOptions.RemoveEmptyEntries))
            if (!merged.Contains(part)) merged.Add(part);
        var path = string.Join(':', merged);
        Environment.SetEnvironmentVariable("PATH", path);
        return path;
    }

    /// Run `$SHELL -ilc '<print PATH between markers>'` and pull the value
    /// out of whatever else the rc files print (banners, fortune, warnings).
    internal static string? ResolveLoginShellPath(int timeoutMs)
    {
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrWhiteSpace(shell) || !File.Exists(shell)) shell = "/bin/zsh";
        var leaf = Path.GetFileName(shell);
        // fish keeps PATH as a list; POSIX shells as one string.
        var script = leaf == "fish"
            ? $"printf '%s%s%s' {Begin} (string join ':' $PATH) {End}"
            : $"printf '%s%s%s' {Begin} \"$PATH\" {End}";
        try
        {
            var psi = new ProcessStartInfo(shell)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            // -i: interactive, so ~/.zshrc (where most PATH edits live) runs;
            // -l: login, so ~/.zprofile / path_helper run too; -c: then this.
            psi.ArgumentList.Add("-ilc");
            psi.ArgumentList.Add(script);
            // Lets a user's rc file skip its slow parts for us, the way VS
            // Code's VSCODE_RESOLVING_ENVIRONMENT does.
            psi.Environment["PERCH_RESOLVING_ENVIRONMENT"] = "1";
            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                Log.Info("ShellEnv", $"{shell} did not answer within {timeoutMs} ms; keeping the launch PATH");
                return null;
            }
            var text = stdout.GetAwaiter().GetResult();
            var b = text.IndexOf(Begin, StringComparison.Ordinal);
            var e = text.IndexOf(End, StringComparison.Ordinal);
            if (b < 0 || e < 0 || e <= b)
            {
                Log.Info("ShellEnv", $"{shell} printed no PATH markers (exit {p.ExitCode}, {sw.ElapsedMilliseconds} ms)");
                return null;
            }
            var value = text.Substring(b + Begin.Length, e - b - Begin.Length).Trim();
            Log.Info("ShellEnv", $"{shell} -ilc answered in {sw.ElapsedMilliseconds} ms: PATH={value}");
            return value;
        }
        catch (Exception ex)
        {
            Log.Error("ShellEnv", ex);
            return null;
        }
    }
}
