using System;
using System.IO;
using System.Windows.Media.Imaging;

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
internal static class ImageThumb
{
    /// A downscaled JPEG of `bytes`, base64-encoded, with its long edge capped
    /// at `maxEdge`. Null on any decode failure — callers fall back to showing
    /// no image rather than shipping something unbounded.
    ///
    /// JPEG rather than PNG: thumbnails of screenshots compress roughly 10x
    /// better, and these are previews, not the artifact. The artifact itself is
    /// the file on disk, which is untouched.
    public static string? JpegBase64(byte[] bytes, int maxEdge)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource src = frame;

            var scale = (double)maxEdge / Math.Max(frame.PixelWidth, frame.PixelHeight);
            if (scale < 1.0)
            {
                var tb = new TransformedBitmap(
                    frame, new System.Windows.Media.ScaleTransform(scale, scale));
                tb.Freeze();
                src = tb;
            }

            var enc = new JpegBitmapEncoder { QualityLevel = 80 };
            enc.Frames.Add(BitmapFrame.Create(src));
            using var outMs = new MemoryStream();
            enc.Save(outMs);
            return Convert.ToBase64String(outMs.ToArray());
        }
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

    /// PNG bytes for a clipboard bitmap. PNG because this is the ARTIFACT — the
    /// file that lands in the board's assets/ and that an agent will read — so
    /// it must be lossless and it must not depend on the encoder's quality
    /// setting. The lossy step is only ever the preview.
    public static byte[]? EncodePng(BitmapSource src)
    {
        try
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
