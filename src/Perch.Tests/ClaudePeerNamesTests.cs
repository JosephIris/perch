using Xunit;

namespace Perch.Tests;

// Peer names are the ADDRESSES of cross-session messaging — a tab title goes
// in, the --name another agent's SendMessage must resolve comes out. The
// sanitizer therefore has to be deterministic and never empty.
public class ClaudePeerNamesTests
{
    [Theory]
    [InlineData("weekly-digest", "weekly-digest")]
    [InlineData("  user profiles  ", "user profiles")]           // trim + keep inner space
    [InlineData("fix\tthe   spinner", "fix the spinner")]        // whitespace collapses
    [InlineData("say \"hi\" to 'them'", "say hi to them")]       // quotes dropped (they'd mangle the intro line)
    [InlineData("", "tab")]                                      // never an empty --name
    [InlineData("\a\b", "tab")]                          // control chars alone → fallback
    public void Sanitize_ProducesStableAddresses(string title, string expected)
        => Assert.Equal(expected, ClaudePeerNames.Sanitize(title));

    [Fact]
    public void Sanitize_CapsAtFortyChars()
    {
        var s = ClaudePeerNames.Sanitize(new string('x', 100));
        Assert.Equal(40, s.Length);
    }

    // The address itself is the SLUG — the one spelling the launch command,
    // the name sweep and a teammate's SendMessage all use.
    [Theory]
    [InlineData("Loc diff fix", "loc-diff-fix")]
    [InlineData("weekly-digest", "weekly-digest")]
    [InlineData("fix: the / bug", "fix-the-bug")]
    [InlineData("", "tab")]
    [InlineData("!!!", "tab")]
    public void ForTitle_IsTheLaunchSlug(string title, string expected)
        => Assert.Equal(expected, ClaudePeerNames.ForTitle(title));

    // The bug this pins: a project tab launched as `--name loc-diff-fix` while
    // the host remembered "Loc diff fix", so an observed SendMessage target
    // matched nothing and the note was dropped.
    [Theory]
    [InlineData("Loc diff fix", "loc-diff-fix", true)]
    [InlineData("LOC DIFF FIX", "loc-diff-fix", true)]
    [InlineData("loc-diff-fix", "loc-diff-fix", true)]
    [InlineData("loc diff fix 2", "loc-diff-fix-2", true)]     // legacy " 2" suffix still resolves
    [InlineData("loc-diff-fix", "loc-diff-fix-2", false)]
    [InlineData("", "loc-diff-fix", false)]
    [InlineData("!!!", "!!!", false)]                          // nothing slugs to nothing
    public void Matches_ComparesThroughTheSlug(string a, string b, bool expected)
        => Assert.Equal(expected, ClaudePeerNames.Matches(a, b));
}
