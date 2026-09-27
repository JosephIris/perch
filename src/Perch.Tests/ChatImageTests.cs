using System;
using System.IO;
using Xunit;

namespace Perch.Tests;

// A thread's screenshot, named in a chat message, is read for the page to
// show — picture files only, and only ones that exist and fit.
public class ChatImageTests
{
    [Fact]
    public void APictureFileComesBackAsADataUrl_AnythingElseDoesNot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "perch-chatimg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var png = Path.Combine(dir, "shot.png");
            File.WriteAllBytes(png, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            var url = AppController.ChatImageDataUrl(png.Replace('\\', '/'));   // as a message writes it
            Assert.Equal("data:image/png;base64,iVBORw==", url);

            var txt = Path.Combine(dir, "notes.txt");
            File.WriteAllText(txt, "secret");
            Assert.Null(AppController.ChatImageDataUrl(txt));                   // not a picture
            Assert.Null(AppController.ChatImageDataUrl(Path.Combine(dir, "gone.png")));
            File.WriteAllBytes(Path.Combine(dir, "empty.jpg"), Array.Empty<byte>());
            Assert.Null(AppController.ChatImageDataUrl(Path.Combine(dir, "empty.jpg")));
            Assert.Null(AppController.ChatImageDataUrl(""));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
