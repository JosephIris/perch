using System;
using System.IO;

namespace Perch;

/// The host → CLI side-channel for a bot's identity: which standing brief a
/// pane's `claude` launches with, and which team roster its prompts carry.
///
/// Same mechanism as ClaudeModelState and BoardController.MarkerPathFor, for
/// the same reason: the per-pane pipe is one-way (the host only reads it), so
/// a tiny temp file keyed by PERCH_PANE_ID is how the host tells the CLI side
/// anything. Three files per pane:
///
///   perch-claude-brief-&lt;pane&gt;.txt   → absolute path of the bot's rendered
///                                     system.md; wrap-claude appends it as
///                                     --append-system-prompt-file at launch.
///   perch-team-&lt;pane&gt;.txt           → absolute path of the team's roster.md;
///                                     the UserPromptSubmit hook injects its
///                                     contents as additionalContext each turn.
///   perch-claude-launched-name-&lt;pane&gt;.txt
///                                   → written by wrap-claude, NOT the host:
///                                     the --name it actually passed. The
///                                     session-start hook reports this one so
///                                     the host records the real address.
///
/// Brief at launch, roster per turn: the brief is who the bot IS and only
/// changes when someone edits it (a relaunch is the honest moment to apply
/// that); the roster is who is AROUND and changes whenever a bot joins or
/// leaves, which must reach a running bot without restarting it.
///
/// Never throws. A missing marker costs a bot its brief for one launch, which
/// is visible and recoverable; a throw here would take the spawn down with it.
internal static class TeamMarkers
{
    public static string BriefPathFor(Guid paneId)
        => Path.Combine(Path.GetTempPath(), $"perch-claude-brief-{paneId:N}.txt");

    public static string RosterPathFor(Guid paneId)
        => Path.Combine(Path.GetTempPath(), $"perch-team-{paneId:N}.txt");

    public static string LaunchedNamePathFor(Guid paneId)
        => Path.Combine(Path.GetTempPath(), $"perch-claude-launched-name-{paneId:N}.txt");

    /// Write (or, for null/blank/nonexistent, delete) both host-owned markers
    /// for a pane. A pointer at a file that isn't there is worse than no
    /// pointer: the CLI would try and fail every turn.
    public static void Publish(Guid paneId, string? systemMdPath, string? rosterPath)
    {
        WriteOrDelete(BriefPathFor(paneId), systemMdPath, "Team.marker.brief");
        WriteOrDelete(RosterPathFor(paneId), rosterPath, "Team.marker.roster");
    }

    /// Drop every marker for a pane when it closes or stops being a bot, so a
    /// recycled pane can't inherit someone else's brief.
    public static void Clear(Guid paneId)
    {
        Delete(BriefPathFor(paneId), "Team.marker.clear");
        Delete(RosterPathFor(paneId), "Team.marker.clear");
        Delete(LaunchedNamePathFor(paneId), "Team.marker.clear");
    }

    /// The --name wrap-claude last launched this pane's claude with, or null
    /// when it hasn't launched one (or the wrapper predates the file).
    public static string? ReadLaunchedName(Guid paneId)
    {
        try
        {
            var path = LaunchedNamePathFor(paneId);
            if (!File.Exists(path)) return null;
            var name = File.ReadAllText(path).Trim();
            return name.Length is 0 or > 60 ? null : name;
        }
        catch { return null; }
    }

    private static void WriteOrDelete(string marker, string? target, string site)
    {
        try
        {
            var t = (target ?? "").Trim();
            if (t.Length == 0 || !File.Exists(t))
            {
                if (File.Exists(marker)) File.Delete(marker);
                return;
            }
            AtomicFile.WriteAllText(marker, t);
        }
        catch (Exception ex) { Log.Error(site, ex); }
    }

    private static void Delete(string marker, string site)
    {
        try { if (File.Exists(marker)) File.Delete(marker); }
        catch (Exception ex) { Log.Error(site, ex); }
    }
}
