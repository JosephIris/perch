using System;
using System.IO;
using Xunit;

namespace Perch.Tests;

/// The bot → CLI handoff, host half.
///
/// The host writes three tiny marker files per TERMINAL pane; the CLI half
/// (wrap-claude at launch, the UserPromptSubmit hook every turn) reads them.
/// The two live in different processes with nothing else connecting them, so
/// what these tests pin is the CONTRACT: the file names, the key format, and
/// the rule that a pointer at a file that isn't there is removed rather than
/// left for the CLI to trip over every turn.
public class TeamMarkersTests
{
    private static string TempFile(string name, string contents = "x")
    {
        var dir = Path.Combine(Path.GetTempPath(), "perch-teammarkers-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void MarkerPaths_MatchTheNFormatTheShellExports()
    {
        // Shell.BuildStartupCommandLine sets PERCH_PANE_ID = paneId.ToString("N");
        // wrap-claude and the hook build these names from that. A mismatch is
        // a file nobody reads, with no error anywhere.
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        const string n = "11111111222233334444555555555555";
        Assert.EndsWith($"perch-claude-brief-{n}.txt", TeamMarkers.BriefPathFor(id));
        Assert.EndsWith($"perch-team-{n}.txt", TeamMarkers.RosterPathFor(id));
        Assert.EndsWith($"perch-claude-launched-name-{n}.txt", TeamMarkers.LaunchedNamePathFor(id));
        Assert.StartsWith(Path.GetTempPath(), TeamMarkers.BriefPathFor(id));
        Assert.StartsWith(Path.GetTempPath(), TeamMarkers.RosterPathFor(id));
        Assert.StartsWith(Path.GetTempPath(), TeamMarkers.LaunchedNamePathFor(id));
    }

    [Fact]
    public void Publish_WritesBothPointersAndClearRemovesThem()
    {
        var id = Guid.NewGuid();
        var system = TempFile("system.md", "# Ada\n");
        var roster = TempFile("roster.md", "- ada\n");
        try
        {
            TeamMarkers.Publish(id, system, roster);
            Assert.Equal(system, File.ReadAllText(TeamMarkers.BriefPathFor(id)).Trim());
            Assert.Equal(roster, File.ReadAllText(TeamMarkers.RosterPathFor(id)).Trim());

            TeamMarkers.Clear(id);
            Assert.False(File.Exists(TeamMarkers.BriefPathFor(id)));
            Assert.False(File.Exists(TeamMarkers.RosterPathFor(id)));
            Assert.False(File.Exists(TeamMarkers.LaunchedNamePathFor(id)));
        }
        finally { CleanUp(id, system, roster); }
    }

    [Fact]
    public void Publish_RemovesAPointerWhoseTargetIsGone()
    {
        // A bot that leaves the team, or a brief file deleted underneath us:
        // the CLI must stop being pointed at it, not fail to read it each turn.
        var id = Guid.NewGuid();
        var system = TempFile("system.md");
        var roster = TempFile("roster.md");
        try
        {
            TeamMarkers.Publish(id, system, roster);
            Assert.True(File.Exists(TeamMarkers.BriefPathFor(id)));

            File.Delete(system);
            TeamMarkers.Publish(id, system, roster);
            Assert.False(File.Exists(TeamMarkers.BriefPathFor(id)));
            Assert.True(File.Exists(TeamMarkers.RosterPathFor(id)));

            TeamMarkers.Publish(id, null, null);
            Assert.False(File.Exists(TeamMarkers.RosterPathFor(id)));
        }
        finally { CleanUp(id, system, roster); }
    }

    [Fact]
    public void ReadLaunchedName_IsNullUntilTheWrapperWritesIt()
    {
        var id = Guid.NewGuid();
        try
        {
            Assert.Null(TeamMarkers.ReadLaunchedName(id));
            File.WriteAllText(TeamMarkers.LaunchedNamePathFor(id), "ada-2\n");
            Assert.Equal("ada-2", TeamMarkers.ReadLaunchedName(id));
            File.WriteAllText(TeamMarkers.LaunchedNamePathFor(id), "   ");
            Assert.Null(TeamMarkers.ReadLaunchedName(id));
        }
        finally { TeamMarkers.Clear(id); }
    }

    [Fact]
    public void Publish_And_Clear_SurviveJunk()
    {
        // Runs on every spawn; it must never be the thing that throws.
        var id = Guid.NewGuid();
        TeamMarkers.Publish(id, "C:\\<not>|a*path", "also <junk>|");
        TeamMarkers.Publish(id, "", "");
        TeamMarkers.Clear(id);
        TeamMarkers.Clear(Guid.Empty);
        Assert.Null(TeamMarkers.ReadLaunchedName(id));
    }

    private static void CleanUp(Guid id, params string[] files)
    {
        TeamMarkers.Clear(id);
        foreach (var f in files)
        {
            try { Directory.Delete(Path.GetDirectoryName(f)!, recursive: true); } catch { }
        }
    }
}
