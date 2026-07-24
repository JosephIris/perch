using System;
using System.IO;
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
    // Pre-quoted on purpose. Shell.NormalizeShellPath disambiguates a spaced,
    // unquoted exe path by probing File.Exists at each space boundary, so an
    // unquoted literal here only reaches the pwsh branch on machines that
    // actually have PowerShell 7 installed at the default location. Everywhere
    // else the exe token splits at "C:\Program" and the whole line degrades to
    // the unrecognized-shell default, failing these tests for a reason that has
    // nothing to do with what they pin. The quotes short-circuit that probe.
    // AnUnquotedShellPathWithSpacesIsStillRecognizedAsPwsh covers the probe
    // itself, against a file it creates.
    private const string Pwsh = "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\"";

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

    /// The other half of what the unquoted constant used to cover by accident:
    /// a spaced exe path with no quotes still has to be recognized as pwsh
    /// rather than falling through to the default branch (which would ship a
    /// pane with no IPC env and no cwd). Pinned against a file this test
    /// creates, so it holds on a machine with no PowerShell 7.
    [Fact]
    public void AnUnquotedShellPathWithSpacesIsStillRecognizedAsPwsh()
    {
        var dir = Path.Combine(Path.GetTempPath(), "perch shell " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "pwsh.exe");
        File.WriteAllBytes(exe, Array.Empty<byte>());
        try
        {
            var line = Shell.BuildStartupCommandLine(exe, null, Guid.NewGuid(), TabCommand("loc diff fix", "abc-123"));

            // Quoted back up, pwsh branch taken (env injection + -Command), and
            // the spliced name still a single bare token.
            Assert.StartsWith($"\"{exe}\" -NoExit -Command \"", line);
            Assert.Contains("$env:PERCH_PIPE=", line);
            Assert.Contains("--name loc-diff-fix", line);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
