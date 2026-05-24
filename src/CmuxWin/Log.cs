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
            lock (_lock) File.AppendAllText(LogPath, line);
        }
        catch { /* logging must never throw */ }
    }
}
