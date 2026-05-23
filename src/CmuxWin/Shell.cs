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
