using System;
using System.IO;
using System.Linq;

namespace Perch;

/// Finding codex's own record of a conversation on disk — the counterpart to
/// <see cref="ClaudeTranscripts"/>.
///
/// Codex writes one "rollout" file per thread under
/// <c>$CODEX_HOME/sessions/&lt;yyyy&gt;/&lt;MM&gt;/&lt;dd&gt;/rollout-&lt;timestamp&gt;-&lt;session id&gt;.jsonl</c>.
/// The session id is in the FILENAME, which is the whole reason this is cheap:
/// we never open a file to find out whether it is the one we want, and we never
/// have to reproduce codex's directory naming (unlike Claude's, which encodes
/// the cwd and has drifted between versions).
internal static class CodexTranscripts
{
    /// Codex's config home: $CODEX_HOME, else ~/.codex. Mirrors what the codex
    /// wrapper writes its profile into, so the two can't disagree about where
    /// codex lives.
    public static string Home()
    {
        var over = Environment.GetEnvironmentVariable("CODEX_HOME");
        return string.IsNullOrWhiteSpace(over)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : over;
    }

    /// The rollout file for this thread, or null when codex has no record of
    /// it. Unlike the Claude side there is no cwd to scope by — the id alone
    /// identifies the file.
    public static string? Locate(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        // A session id lands in a search pattern; keep it to the shape codex
        // mints (a uuid) so a malformed one can't reach outside the tree.
        foreach (var c in sessionId!)
            if (!(char.IsLetterOrDigit(c) || c == '-')) return null;
        try
        {
            var sessions = Path.Combine(Home(), "sessions");
            if (!Directory.Exists(sessions)) return null;
            // Newest first: a thread resumed across days has one file, but a
            // session id is only unique in practice, not by construction.
            return Directory
                .EnumerateFiles(sessions, "*" + sessionId + ".jsonl", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// Whether codex has a resumable record of this thread. Used the same way
    /// as ClaudeTranscripts.Exists — to stop Perch from firing a resume for a
    /// conversation that was never written, which just errors in the pane.
    public static bool Exists(string? sessionId) => Locate(sessionId) != null;
}
