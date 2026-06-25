using System;
using System.IO;

namespace PerchCli;

// Finds the "real" target binary on PATH, skipping our own tools directory so
// a `claude` / `codex` shim that lives next to perch.exe never resolves to
// itself (infinite recursion). Shared by ClaudeWrapper and CodexWrapper.
internal static class BinResolver
{
    /// Walks PATH and returns the first `<baseName>.{exe,cmd,bat,}` found in a
    /// directory other than the wrapper's own. Order matches Windows resolution
    /// and the common npm-shim case (a .cmd next to no .exe). Null if none.
    public static string? FindOnPathSkippingSelf(string baseName)
    {
        var selfDir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
        try { selfDir = Path.GetFullPath(selfDir); } catch { }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string dir;
            try { dir = Path.GetFullPath(raw.Trim()); } catch { continue; }
            if (string.Equals(dir, selfDir, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var ext in new[] { ".exe", ".cmd", ".bat", "" })
            {
                var candidate = Path.Combine(dir, baseName + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
