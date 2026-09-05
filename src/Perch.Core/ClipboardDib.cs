namespace Perch;

/// Decode a clipboard bitmap (CF_DIB / CF_DIBV5: a BITMAPINFOHEADER-family
/// header followed by pixels, no file header) into straight BGRA pixels, top
/// row first — the one shape a PNG encoder wants.
///
/// Why not let WPF do it: `Clipboard.GetImage()` turns a 32-bit screenshot
/// whose alpha bytes are all zero — what browsers, Telegram and most screen
/// grabbers put on the clipboard, since they never meant "transparent" — into
/// a fully transparent, premultiplied-to-black image. The PNG that reached the
/// room on 2026-09-05 was exactly that: 1280×722 of alpha 0 and black. The
/// colour is unrecoverable once WPF has premultiplied, so the rescue has to
/// read the raw bytes. Rule, the same one image editors use: alpha counts only
/// when at least one pixel has some; a bitmap that is transparent everywhere
/// is a bitmap that has no alpha channel and is shown opaque.
///
/// Handles what clipboards actually carry: 32- and 24-bit, BI_RGB or
/// BI_BITFIELDS with the standard BGR(A) masks, bottom-up or top-down, with
/// a 40-, 108- or 124-byte header. Anything else (palettes, RLE, odd masks)
/// returns null and the caller falls back to WPF.
public static class ClipboardDib
{
    public sealed record Decoded(int Width, int Height, byte[] Bgra, bool AlphaWasEmpty);

    public static Decoded? Decode(byte[]? dib)
    {
        if (dib == null || dib.Length < 40) return null;
        var headerSize = ReadInt(dib, 0);
        if (headerSize is not (40 or 52 or 56 or 108 or 124) || dib.Length < headerSize) return null;
        var width = ReadInt(dib, 4);
        var heightRaw = ReadInt(dib, 8);
        var planes = ReadShort(dib, 12);
        var bits = ReadShort(dib, 14);
        var compression = ReadInt(dib, 16);
        if (planes != 1 || width <= 0 || heightRaw == 0 || width > 32768) return null;
        if (bits is not (24 or 32)) return null;
        const int BI_RGB = 0, BI_BITFIELDS = 3;
        if (compression is not (BI_RGB or BI_BITFIELDS)) return null;

        var topDown = heightRaw < 0;
        var height = Math.Abs(heightRaw);
        if (height > 32768) return null;

        // Where the pixels start: after the header, and after the three colour
        // masks a BITMAPINFOHEADER carries separately for BI_BITFIELDS (the
        // larger headers embed them). Then check the masks are the plain
        // BGR(A) layout; anything exotic is WPF's problem.
        var pixelOffset = headerSize;
        uint rMask = 0x00FF0000, gMask = 0x0000FF00, bMask = 0x000000FF, aMask = bits == 32 ? 0xFF000000 : 0;
        if (compression == BI_BITFIELDS)
        {
            var maskAt = headerSize == 40 ? 40 : 40;      // both layouts put the masks at byte 40
            if (dib.Length < maskAt + 12) return null;
            rMask = ReadUInt(dib, maskAt);
            gMask = ReadUInt(dib, maskAt + 4);
            bMask = ReadUInt(dib, maskAt + 8);
            if (headerSize == 40) pixelOffset = 40 + 12;
            if (headerSize >= 108) aMask = ReadUInt(dib, 52);
            if (rMask != 0x00FF0000 || gMask != 0x0000FF00 || bMask != 0x000000FF) return null;
            if (aMask != 0 && aMask != 0xFF000000) return null;
        }
        else if (headerSize >= 108)
        {
            // BI_RGB with a V4/V5 header: an alpha mask may still be declared.
            aMask = ReadUInt(dib, 52);
            if (aMask != 0 && aMask != 0xFF000000) return null;
        }

        var bytesPerPixel = bits / 8;
        var stride = ((width * bits + 31) / 32) * 4;      // rows are 4-byte aligned
        if ((long)pixelOffset + (long)stride * height > dib.Length) return null;

        var outStride = width * 4;
        var bgra = new byte[outStride * height];
        var anyAlpha = false;
        for (var row = 0; row < height; row++)
        {
            var srcRow = topDown ? row : height - 1 - row;
            var src = pixelOffset + srcRow * stride;
            var dst = row * outStride;
            for (var x = 0; x < width; x++)
            {
                bgra[dst]     = dib[src];        // B
                bgra[dst + 1] = dib[src + 1];    // G
                bgra[dst + 2] = dib[src + 2];    // R
                var a = bits == 32 ? dib[src + 3] : (byte)255;
                if (bits == 32 && a != 0) anyAlpha = true;
                bgra[dst + 3] = a;
                src += bytesPerPixel;
                dst += 4;
            }
        }

        // A 32-bit bitmap that is "transparent" everywhere has no alpha at all
        // — it is a screenshot with an unused fourth byte. Show it.
        var alphaWasEmpty = bits == 32 && !anyAlpha;
        if (alphaWasEmpty)
            for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;

        return new Decoded(width, height, bgra, alphaWasEmpty);
    }

    private static int ReadInt(byte[] b, int at) => BitConverter.ToInt32(b, at);
    private static uint ReadUInt(byte[] b, int at) => BitConverter.ToUInt32(b, at);
    private static short ReadShort(byte[] b, int at) => BitConverter.ToInt16(b, at);
}
