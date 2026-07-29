using System;
using System.IO;
using System.Text;

namespace Perch;

/// Write-then-replace file writes.
///
/// A plain File.WriteAllText truncates the target before it writes, so a crash
/// (or a kill, or a full disk) mid-write leaves a half-written file that fails
/// to parse on the next launch. Writing to a sibling ".tmp" and then moving it
/// over the target makes the replacement atomic at the filesystem level: a
/// reader sees either the old file or the new one, never a truncated one.
///
/// This idiom was copy-pasted in CloudLedger.Save and LocalLedger.Save; the
/// board store needed a third copy, which is the point at which it becomes a
/// helper. Directory creation is included because every caller did it first.
///
/// Failure policy is the CALLER's, not ours — these throw. The ledgers and the
/// board store each wrap their own call and log, because what a failed write
/// means differs (a lost ledger entry is cosmetic; a lost board write is not).
internal static class AtomicFile
{
    /// Write text to `path` via a temp file in the same directory. UTF-8
    /// without a BOM: these files are read back by our own parsers, by git,
    /// and by agents, none of which want a BOM.
    public static void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }

    /// Byte-oriented sibling, for images and other binary artifacts.
    public static void WriteAllBytes(string path, byte[] contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}
