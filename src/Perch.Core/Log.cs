using System;
using System.IO;

namespace Perch;

internal static class Log
{
    private static readonly object _lock = new();
    private static readonly string LogPath = Path.Combine(
        AppPaths.DataRoot,
        "perch", "errors.log");

    /// Roll at 8 MB, keeping three generations (errors.1/2/3.log) — ~32 MB of
    /// history, capped. Uncapped, this file reached 18 MB on one machine, and
    /// it is the ONLY forensic record we keep: the 56-hour silence in it is
    /// what dated the session-leak incident. So the cap deletes only the
    /// OLDEST generation, never truncates the live file.
    private const long MaxBytes = 8L * 1024 * 1024;
    private const int Generations = 3;

    public static void Error(string context, Exception? ex)
    {
        Write($"ERROR {context}: {ex}");
    }

    public static void Info(string context, string message = "")
    {
        Write($"INFO  {context}{(message.Length > 0 ? ": " + message : "")}");
    }

    private static void Write(string body)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {body}{Environment.NewLine}";
            lock (_lock)
            {
                RollIfBig();
                File.AppendAllText(LogPath, line);
            }
        }
        catch { /* logging must never throw */ }
    }

    /// Caller holds _lock. Shifts errors.2→errors.3, errors.1→errors.2,
    /// errors→errors.1. A failure here must not lose the line being written,
    /// so every step is swallowed and the append proceeds either way.
    private static void RollIfBig()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxBytes) return;
            var dir = Path.GetDirectoryName(LogPath)!;
            string Gen(int n) => Path.Combine(dir, $"errors.{n}.log");
            try { File.Delete(Gen(Generations)); } catch { }
            for (var n = Generations - 1; n >= 1; n--)
            {
                try { if (File.Exists(Gen(n))) File.Move(Gen(n), Gen(n + 1), overwrite: true); }
                catch { }
            }
            try { File.Move(LogPath, Gen(1), overwrite: true); } catch { }
        }
        catch { }
    }
}
