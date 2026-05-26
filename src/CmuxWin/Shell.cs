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
        => BuildStartupCommandLine(shellCommandLine, cwd, paneId: null);

    /// Same as <see cref="WithStartupCwd"/>, plus injection of the per-pane
    /// IPC env vars (<c>CMUX_PIPE</c>, <c>CMUX_PANE_ID</c>). EasyTerminalControl
    /// doesn't expose a child-env hook, so we splice the assignments into the
    /// shell's own startup syntax. paneId=null skips the IPC env (used by
    /// any callers that don't need it).
    public static string BuildStartupCommandLine(string shellCommandLine, string? cwd, Guid? paneId)
    {
        // Default shell paths like "C:\Program Files\PowerShell\7\pwsh.exe"
        // come back unquoted from DetectedShells(). Without normalization,
        // ExtractExe splits at the first space ("C:\Program") and the switch
        // below falls through to the unrecognized-shell default, skipping
        // env injection entirely. Quoting any prefix that resolves to a real
        // file fixes both ExtractExe and downstream argv quoting.
        shellCommandLine = NormalizeShellPath(shellCommandLine);

        var hasCwd = !string.IsNullOrWhiteSpace(cwd);
        try { if (hasCwd && !Directory.Exists(cwd!)) hasCwd = false; } catch { hasCwd = false; }

        var exe = ExtractExe(shellCommandLine);
        var rest = ExtractRest(shellCommandLine).Trim();
        var leaf = Path.GetFileName(exe).ToLowerInvariant();
        var quotedExe = exe.Contains(' ') ? $"\"{exe}\"" : exe;
        var quotedCwd = hasCwd && cwd!.Contains(' ') ? $"\"{cwd}\"" : cwd ?? "";

        var pipePath = paneId is Guid pid ? $@"\\.\pipe\cmux\{pid:N}" : null;
        var paneIdStr = paneId?.ToString("N");

        switch (leaf)
        {
            case "pwsh.exe":
            case "powershell.exe":
            {
                // Weave env + cwd + any user-supplied args (e.g. -File foo.ps1
                // from `cmux open --cmd`) into a single -Command, since pwsh
                // only honors one. -File or -Command in rest is turned into
                // an inline invocation; bare flags like -NoLogo are dropped
                // (caller can rely on our -NoExit). Empty rest matches the
                // original behavior — just env + cwd, then interactive shell.
                var sb = new System.Text.StringBuilder();
                if (pipePath != null)
                {
                    sb.Append("$env:CMUX_PIPE='").Append(EscapePs(pipePath)).Append("'; ");
                    sb.Append("$env:CMUX_PANE_ID='").Append(EscapePs(paneIdStr!)).Append("'; ");
                }
                if (hasCwd)
                    sb.Append("Set-Location -LiteralPath '").Append(EscapePs(cwd!)).Append("'; ");

                var userScript = TranslatePwshArgs(rest);
                if (!string.IsNullOrEmpty(userScript))
                    sb.Append(userScript);
                else if (sb.Length >= 2 && sb[sb.Length - 2] == ';')
                    sb.Length -= 2; // trim trailing "; " when no user script follows

                if (sb.Length == 0) return shellCommandLine;
                return $"{quotedExe} -NoExit -Command \"{sb.ToString().Replace("\"", "`\"")}\"";
            }
            case "cmd.exe":
            {
                // cmd /K with a single quoted command; semicolons aren't
                // separators in cmd, so chain with `&&`. Each `set` is its
                // own statement inside the /K command. User-supplied args
                // tacked on as the last step.
                var parts = new System.Collections.Generic.List<string>();
                if (pipePath != null)
                {
                    parts.Add($"set CMUX_PIPE={pipePath}");
                    parts.Add($"set CMUX_PANE_ID={paneIdStr}");
                }
                if (hasCwd) parts.Add($"cd /D \"{cwd}\"");
                if (!string.IsNullOrEmpty(rest)) parts.Add(rest);
                if (parts.Count == 0) return shellCommandLine;
                return $"{quotedExe} /K \"{string.Join(" && ", parts)}\"";
            }
            case "wsl.exe":
            {
                // wsl reads WSLENV from the parent env to decide which host
                // env vars to forward. We can't set parent env per-pane (it's
                // process-wide), so for WSL we degrade to cwd-only and skip
                // the IPC injection. The CLI is Windows-side anyway; calling
                // it from inside WSL would need /mnt/c paths, which is out of
                // scope for phase 1.
                return hasCwd ? $"{quotedExe} --cd {quotedCwd}" : shellCommandLine;
            }
            default:
                return shellCommandLine;
        }
    }

    /// PowerShell single-quoted string escape: only the single quote needs
    /// doubling. `$` and backslashes are literal inside single quotes.
    private static string EscapePs(string s) => s.Replace("'", "''");

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

    /// Wraps the leading exe path in quotes when it contains spaces and isn't
    /// already quoted. Walks each space boundary asking "is the prefix an
    /// existing file?" — first hit wins. No-ops if quotes are already present
    /// or there are no spaces to disambiguate.
    private static string NormalizeShellPath(string s)
    {
        s = s.TrimStart();
        if (s.StartsWith("\"")) return s;
        if (!s.Contains(' ')) return s;
        try { if (File.Exists(s)) return $"\"{s}\""; } catch { }
        var idx = -1;
        while ((idx = s.IndexOf(' ', idx + 1)) > 0)
        {
            var candidate = s.Substring(0, idx);
            try { if (File.Exists(candidate)) return $"\"{candidate}\"{s.Substring(idx)}"; } catch { }
        }
        return s;
    }

    /// Everything after the exe token. "" if the command line is just an exe.
    private static string ExtractRest(string commandLine)
    {
        commandLine = commandLine.TrimStart();
        if (commandLine.StartsWith("\""))
        {
            var end = commandLine.IndexOf('"', 1);
            if (end > 0) return commandLine.Substring(end + 1);
            return "";
        }
        var space = commandLine.IndexOf(' ');
        return space > 0 ? commandLine.Substring(space + 1) : "";
    }

    /// Translate PowerShell-style follow-on args into a single statement that
    /// can be appended inside our wrapper -Command. Recognises:
    ///   - "-File <path>"     → `& '<path>'`     (everything after this flag
    ///                                            up to the end is the path
    ///                                            and any script args)
    ///   - "-Command <rest>"  → <rest> verbatim
    /// All other flags (-NoExit, -NoLogo, -NoProfile, …) are skipped over —
    /// our wrapper already supplies -NoExit, and the rest are either harmless
    /// or out of scope for now. Quotes around the path are stripped if balanced.
    private static string TranslatePwshArgs(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) return "";
        var tokens = TokenizeCommandLine(rest);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i].ToLowerInvariant();
            if (t == "-file" || t == "-f")
            {
                if (i + 1 >= tokens.Count) return "";
                var path = tokens[i + 1];
                // Anything after the file path is treated as script args
                // (joined with spaces); harmless if absent.
                var extra = i + 2 < tokens.Count
                    ? " " + string.Join(' ', tokens.GetRange(i + 2, tokens.Count - (i + 2)))
                    : "";
                return $"& '{EscapePs(path)}'{extra}";
            }
            if (t == "-command" || t == "-c")
            {
                // Everything after -Command is the script (PowerShell convention).
                if (i + 1 >= tokens.Count) return "";
                return string.Join(' ', tokens.GetRange(i + 1, tokens.Count - (i + 1)));
            }
            // unknown flag — skip
        }
        return "";
    }

    /// Naive command-line tokenizer: splits on spaces, respects double-quote
    /// spans. Strips surrounding double quotes from each token. Good enough
    /// for the PowerShell args we care about; we don't need to handle backtick
    /// escapes or single quotes here because Session.Shell is set by us or by
    /// an agent calling `cmux open --cmd`.
    private static System.Collections.Generic.List<string> TokenizeCommandLine(string s)
    {
        var result = new System.Collections.Generic.List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    /// Detects installed shells in preferred order. The first entry is the default.
    /// Paths with spaces are returned ALREADY QUOTED so downstream
    /// command-line builders don't have to disambiguate via filesystem probes
    /// (which fails when the binary moves, is hidden by AV, or lives in
    /// localised "Program Files" variants we don't enumerate).
    public static IReadOnlyList<ShellChoice> DetectedShells()
    {
        static string Q(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

        var found = new List<ShellChoice>();

        // PowerShell 7+
        foreach (var p in new[]
        {
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            @"C:\Program Files\PowerShell\7-preview\pwsh.exe",
        })
        {
            if (FileExistsSafe(p)) { found.Add(new("PowerShell 7", Q(p))); break; }
        }
        if (found.Count == 0)
        {
            var onPath = OnPath("pwsh.exe");
            if (onPath != null) found.Add(new("PowerShell 7", Q(onPath)));
        }

        // Windows PowerShell 5.1 (almost always present)
        found.Add(new("Windows PowerShell", "powershell.exe"));

        // cmd
        found.Add(new("Command Prompt", "cmd.exe"));

        // WSL default distro
        var wsl = OnPath("wsl.exe");
        if (wsl != null) found.Add(new("WSL", Q(wsl)));

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
