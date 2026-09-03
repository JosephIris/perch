using System;
using System.IO;

namespace Perch;

/// The two %TEMP% files a pane's hook and the host trade when the answer has
/// to travel the OTHER way from the pipe (which only runs hook → host):
///
///   - `perch-perm-&lt;id&gt;.txt` — the owner's `allow` / `deny` for a permission
///     request. The PermissionRequest hook blocks polling for it; the host
///     writes it from the room card. Deleted by the hook once read.
///   - `perch-task-&lt;pane&gt;.txt` — the id of the task the lead just created with
///     `perch team task new`, so the CLI can print it and the lead can use it
///     in `assign`/`done`. Written by the host, read and deleted by the CLI.
///
/// The CLI computes the same names on its side (tools/perch-cli); the
/// contract is the file name, kept in one place per project.
internal static class TeamPaths
{
    public static string PermAnswerPathFor(string id)
        => Path.Combine(Path.GetTempPath(), $"perch-perm-{San(id)}.txt");

    public static string TaskReplyPathFor(Guid paneId)
        => Path.Combine(Path.GetTempPath(), $"perch-task-{paneId:N}.txt");

    /// Ids come from the hook (it mints them) — keep the file name safe.
    private static string San(string id)
    {
        var sb = new System.Text.StringBuilder(id.Length);
        foreach (var c in id) if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
        return sb.Length == 0 ? "x" : sb.ToString();
    }

    /// Write a small reply file atomically; never throws.
    public static void Write(string path, string text)
    {
        try { AtomicFile.WriteAllText(path, text); }
        catch (Exception ex) { Log.Error("TeamPaths.Write", ex); }
    }
}
