using System;
using Xunit;

namespace Perch.Tests;

// The command a project tab spawns with. This exists because of a bug that is
// invisible from the outside: the initial command is SPLICED into the shell's
// own startup line (pwsh -NoExit -Command "…"), and Shell escapes an inner
// double quote as `" — so a tab named "loc diff fix" spawned
//
//     claude --session-id <id> --name `"loc diff fix`"
//
// and claude came up with the session named `"loc, having parsed `diff` and
// `fix"` as stray arguments. Nothing errored; the name was just quietly wrong.
//
// The fix is to never put a quotable character in the command at all: --name
// takes the SLUG, which has no spaces and needs no quoting in any shell we
// spawn. These pin that, so the day someone "improves" it back to the raw name
// the suite says why not.
public class SpawnCommandTests
{
    private const string Pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";

    /// The command a project tab hands to the lazy spawn, mirroring
    /// MainWindow.OnProjectTabNew.
    private static string TabCommand(string tabName, string sessionId)
    {
        var ccName = GitProc.Slugify(tabName);
        if (ccName.Length == 0) ccName = "tab";
        return $"claude --session-id {sessionId} --name {ccName}";
    }

    [Fact]
    public void TabCommand_CarriesNoQuotesOrSpacesInTheName()
    {
        var cmd = TabCommand("loc diff fix", "abc-123");
        Assert.Equal("claude --session-id abc-123 --name loc-diff-fix", cmd);
        Assert.DoesNotContain("\"", cmd);
    }

    [Fact]
    public void TabCommand_SurvivesTheShellSplice_WithTheNameStillOneToken()
    {
        var cmd = TabCommand("loc diff fix", "abc-123");
        var line = Shell.BuildStartupCommandLine(Pwsh, @"C:\repo", Guid.NewGuid(), cmd);

        // The whole point: after Shell has wrapped and escaped everything, the
        // name is STILL a single bare token. The old form left `--name `" here.
        Assert.Contains("--name loc-diff-fix", line);
        Assert.DoesNotContain("--name `\"", line);
        Assert.DoesNotContain("--name \"", line);
    }

    [Fact]
    public void TabCommand_AWeirdTabNameStillProducesOneToken()
    {
        // Punctuation, emoji, and a name that slugs to nothing at all — none of
        // these may reach the shell as multiple arguments.
        foreach (var name in new[] { "fix: the / bug", "emoji 🎉 tab", "!!!", "  spaced  out  " })
        {
            var cmd = TabCommand(name, "sid");
            var line = Shell.BuildStartupCommandLine(Pwsh, @"C:\repo", Guid.NewGuid(), cmd);
            var afterName = line.Substring(line.IndexOf("--name ", StringComparison.Ordinal) + 7);
            var token = afterName.Split(' ', '"', '`')[0];

            Assert.NotEqual("", token);              // never an empty --name
            Assert.DoesNotContain("\"", token);
            Assert.DoesNotContain(" ", token);
        }
    }
}
