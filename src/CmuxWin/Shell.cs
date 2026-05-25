using System;
using System.Collections.Generic;
using System.IO;

namespace CmuxWin;

internal static class Shell
{
    public sealed record ShellChoice(string Name, string CommandLine);

    /// Returns the command line for the preferred default shell, honoring the user's
    /// override from Settings.DefaultShell if set.
    public static string DefaultCommandLine(string? userOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(userOverride)) return userOverride;
        var first = DetectedShells();
        return first.Count > 0 ? first[0].CommandLine : "powershell.exe";
    }

    /// Wraps a shell command line with shell-specific syntax to launch it
    /// directly in <paramref name="cwd"/>. Falls back to the unmodified
    /// command line if the shell is unrecognised or the cwd doesn't exist —
    /// the pane will still spawn, just in the parent process's directory.
    public static string WithStartupCwd(string shellCommandLine, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return shellCommandLine;
        try { if (!Directory.Exists(cwd)) return shellCommandLine; } catch { return shellCommandLine; }

        var exe = ExtractExe(shellCommandLine);
        var leaf = Path.GetFileName(exe).ToLowerInvariant();
        var quotedExe = exe.Contains(' ') ? $"\"{exe}\"" : exe;
        var quotedCwd = cwd.Contains(' ') ? $"\"{cwd}\"" : cwd;

        return leaf switch
        {
            "pwsh.exe" => $"{quotedExe} -WorkingDirectory {quotedCwd}",
            // PS 5.1 doesn't have -WorkingDirectory. Use Set-Location with literal
            // path so single quotes inside the path don't break the parse — the
            // double-up here is PowerShell's escape for a literal single quote.
            "powershell.exe" => $"{quotedExe} -NoExit -Command \"Set-Location -LiteralPath '{cwd.Replace("'", "''")}'\"",
            // NOT `cmd /K "cd /D \"...\""` — cmd doesn't backslash-escape and
            // would strip the outer quotes per its /K parse rule, leaving a
            // command starting with a literal `\`. Instead, drop the outer
            // wrap and quote only the path: `cmd /K cd /D "<path>"`.
            "cmd.exe" => $"{quotedExe} /K cd /D \"{cwd}\"",
            "wsl.exe" => $"{quotedExe} --cd {quotedCwd}",
            _ => shellCommandLine,
        };
    }

    /// Pulls the executable path out of a command line like
    /// `"C:\path with space\thing.exe" -arg`. Handles the quoted-first-token
    /// case and the unquoted single-token case.
    private static string ExtractExe(string commandLine)
    {
        commandLine = commandLine.TrimStart();
        if (commandLine.StartsWith("\""))
        {
            var end = commandLine.IndexOf('"', 1);
            if (end > 0) return commandLine.Substring(1, end - 1);
        }
        var space = commandLine.IndexOf(' ');
        return space > 0 ? commandLine.Substring(0, space) : commandLine;
    }

    /// Detects installed shells in preferred order. The first entry is the default.
    public static IReadOnlyList<ShellChoice> DetectedShells()
    {
        var found = new List<ShellChoice>();

        // PowerShell 7+
        foreach (var p in new[]
        {
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            @"C:\Program Files\PowerShell\7-preview\pwsh.exe",
        })
        {
            if (FileExistsSafe(p)) { found.Add(new("PowerShell 7", p)); break; }
        }
        if (found.Count == 0)
        {
            var onPath = OnPath("pwsh.exe");
            if (onPath != null) found.Add(new("PowerShell 7", onPath));
        }

        // Windows PowerShell 5.1 (almost always present)
        found.Add(new("Windows PowerShell", "powershell.exe"));

        // cmd
        found.Add(new("Command Prompt", "cmd.exe"));

        // WSL default distro
        var wsl = OnPath("wsl.exe");
        if (wsl != null) found.Add(new("WSL", wsl));

        return found;
    }

    private static bool FileExistsSafe(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    private static string? OnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), exe);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }
}
