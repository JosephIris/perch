using System;
using System.IO;
using System.Text;

namespace Perch;

/// Stream complete JSONL records without allocating the entire unread file.
/// A partial final row is retried on the next poll; image offsets remain bytes.
internal static class TranscriptLines
{
    public static long Read(FileStream file, long offset, Action<string, long, int> row)
    {
        file.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream();
        long next = offset;
        int count;
        // Snapshot the end so a continuously appended file cannot monopolize
        // the worker indefinitely.
        long remaining = file.Length - offset;
        while (remaining > 0 && (count = file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
        {
            remaining -= count;
            int start = 0;
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                line.Write(buffer, start, i - start);
                int length = checked((int)line.Length);
                row(Encoding.UTF8.GetString(line.GetBuffer(), 0, length), next, length);
                next += length + 1;
                line.SetLength(0);
                start = i + 1;
            }
            line.Write(buffer, start, count - start);
        }
        return next;
    }
}
