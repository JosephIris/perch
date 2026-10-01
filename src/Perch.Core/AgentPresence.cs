using System;

namespace Perch;

/// Whether a session-end really means the pane's agent is gone.
///
/// Claude Code ends a session and starts the next one at once on /clear (and
/// a session can be replaced by another, e.g. a resume), and the session-end
/// hook, short and last to run, can arrive after the new session's start. The
/// pane then read as a plain shell with Claude running in it: dictation typed
/// the words untagged and never pressed Enter (an agent pane gets them sent),
/// and the header lost its badge until Claude's next start.
internal static class AgentPresence
{
    /// `next` is the agent the hook reports ("" on session-end); `endedSession`
    /// and `reason` come with a session-end; `currentSession` is the pane's
    /// Claude session as last reported by a session-start.
    public static bool EndsAgent(string next, string? endedSession, string? reason, string? currentSession)
    {
        if (next.Length > 0) return true;                       // a start: always take it
        if (reason == "clear") return false;                    // a new session follows at once
        // A session-end for a session that isn't the pane's current one: a
        // newer session has started since.
        return string.IsNullOrEmpty(endedSession) || string.IsNullOrEmpty(currentSession)
            || string.Equals(endedSession, currentSession, StringComparison.OrdinalIgnoreCase);
    }
}
