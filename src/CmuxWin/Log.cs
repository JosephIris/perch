using System;
using System.IO;

namespace CmuxWin;

internal static class Log
{
    private static readonly object _lock = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "cmux-win", "errors.log");

    public static void Error(string context, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}: {ex}{Environment.NewLine}";
            lock (_lock) File.AppendAllText(LogPath, line);
        }
        catch { /* logging must never throw */ }
    }
}
