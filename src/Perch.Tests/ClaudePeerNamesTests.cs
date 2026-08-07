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
}
