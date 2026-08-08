using System;
using System.IO;

namespace Perch;

/// Decode / downscale / re-encode images, for the two places the app moves
/// picture bytes around: transcript images in the inspector rail, and board
/// nodes.
///
/// The reason a thumbnail exists at all is the bridge. Page↔host messages are
/// JSON over PostWebMessage, tuned for keystroke-sized payloads, and the host
/// has already lost its render process once to an OOM under a large burst
/// (see MainWindow's crash-recovery notes). A 12MB screenshot as a base64
/// dataURL is several transient copies of itself. Downscaling first turns that
/// into tens of kilobytes, and at card or rail size the quality loss is
/// invisible.
///
/// The codec itself is host-supplied (WPF/WIC on Windows, sips on macOS) —
/// assign Codec once at startup, before any controller runs.
internal static class ImageThumb
{
    /// (imageBytes, maxEdge) → base64 JPEG with the long edge capped at
    /// maxEdge, or null on any decode failure. Callers fall back to showing
    /// no image rather than shipping something unbounded.
    public static Func<byte[], int, string?> Codec = static (_, _) => null;

    public static string? JpegBase64(byte[] bytes, int maxEdge)
    {
        try { return Codec(bytes, maxEdge); }
        catch { return null; }
    }

    /// Same, reading from a file. Null when the file is missing or unreadable —
    /// a board node whose asset was deleted shows no picture rather than
    /// throwing.
    public static string? JpegBase64FromFile(string path, int maxEdge)
    {
        try { return File.Exists(path) ? JpegBase64(File.ReadAllBytes(path), maxEdge) : null; }
        catch { return null; }
    }
}
