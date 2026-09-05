using Xunit;

namespace Perch.Tests;

/// The clipboard bitmap decoder behind pasting a screenshot into the room or
/// the board. The case that matters is the one that produced a blank image:
/// a 32-bit bitmap whose alpha bytes are all zero must come out opaque, with
/// its colours intact.
public class ClipboardDibTests
{
    /// A BITMAPINFOHEADER (40 bytes) bitmap, bottom-up unless `topDown`.
    private static byte[] Dib(int width, int height, int bits, byte[] rows, bool topDown = false, int compression = 0, bool v5 = false)
    {
        var headerSize = v5 ? 124 : 40;
        var masks = compression == 3 && !v5 ? 12 : 0;
        var header = new byte[headerSize + masks];
        void Int(int at, int v) => System.BitConverter.GetBytes(v).CopyTo(header, at);
        void Short(int at, short v) => System.BitConverter.GetBytes(v).CopyTo(header, at);
        Int(0, headerSize);
        Int(4, width);
        Int(8, topDown ? -height : height);
        Short(12, 1);
        Short(14, (short)bits);
        Int(16, compression);
        if (compression == 3)
        {
            var at = 40;
            Int(at, 0x00FF0000); Int(at + 4, 0x0000FF00); Int(at + 8, 0x000000FF);
            if (v5 && bits == 32) Int(52, unchecked((int)0xFF000000));
        }
        return header.Concat(rows).ToArray();
    }

    // 2×2, 32-bit, alpha 0 everywhere. Stored bottom-up: the FIRST stored row
    // is the bottom of the picture.
    private static readonly byte[] Bottom = { 10, 20, 30, 0,  40, 50, 60, 0 };   // B G R A ×2
    private static readonly byte[] Top    = { 70, 80, 90, 0,  1, 2, 3, 0 };

    [Fact]
    public void AScreenshotWithEmptyAlphaComesOutOpaque_WithItsColours()
    {
        var d = ClipboardDib.Decode(Dib(2, 2, 32, Bottom.Concat(Top).ToArray()));
        Assert.NotNull(d);
        Assert.True(d!.AlphaWasEmpty);
        Assert.Equal((2, 2), (d.Width, d.Height));
        // Top row first, alpha forced opaque, BGR untouched.
        Assert.Equal(new byte[] { 70, 80, 90, 255, 1, 2, 3, 255, 10, 20, 30, 255, 40, 50, 60, 255 }, d.Bgra);
    }

    [Fact]
    public void RealAlphaIsKept()
    {
        var rows = new byte[] { 10, 20, 30, 128, 40, 50, 60, 0 };
        var d = ClipboardDib.Decode(Dib(2, 1, 32, rows));
        Assert.False(d!.AlphaWasEmpty);
        Assert.Equal(new byte[] { 10, 20, 30, 128, 40, 50, 60, 0 }, d.Bgra);
    }

    [Fact]
    public void TopDownBitmapsAreNotFlipped()
    {
        var d = ClipboardDib.Decode(Dib(2, 2, 32, Top.Concat(Bottom).ToArray(), topDown: true));
        Assert.Equal(new byte[] { 70, 80, 90, 255, 1, 2, 3, 255, 10, 20, 30, 255, 40, 50, 60, 255 }, d!.Bgra);
    }

    [Fact]
    public void TwentyFourBitRowsArePaddedToFourBytes_AndAlwaysOpaque()
    {
        // 1 px wide: 3 bytes of pixel + 1 byte of padding per row.
        var rows = new byte[] { 10, 20, 30, 0,   70, 80, 90, 0 };
        var d = ClipboardDib.Decode(Dib(1, 2, 24, rows));
        Assert.False(d!.AlphaWasEmpty);
        Assert.Equal(new byte[] { 70, 80, 90, 255, 10, 20, 30, 255 }, d.Bgra);
    }

    [Fact]
    public void BitfieldsWithTheStandardMasks_InBothHeaderSizes()
    {
        var rows = new byte[] { 10, 20, 30, 0 };
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, ClipboardDib.Decode(Dib(1, 1, 32, rows, compression: 3))!.Bgra);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, ClipboardDib.Decode(Dib(1, 1, 32, rows, compression: 3, v5: true))!.Bgra);
    }

    [Fact]
    public void WhatItDoesNotUnderstandIsLeftToWpf()
    {
        Assert.Null(ClipboardDib.Decode(null));
        Assert.Null(ClipboardDib.Decode(new byte[10]));
        Assert.Null(ClipboardDib.Decode(Dib(1, 1, 8, new byte[4])));                       // palette
        Assert.Null(ClipboardDib.Decode(Dib(1, 1, 32, new byte[4], compression: 1)));      // RLE
        Assert.Null(ClipboardDib.Decode(Dib(4, 4, 32, new byte[8])));                      // truncated pixels
    }
}
