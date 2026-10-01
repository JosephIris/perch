using Perch;
using Xunit;

namespace Perch.Tests;

/// A session-end that lands after the next session's start must not turn a
/// running Claude's pane back into a plain shell (dictation then typed without
/// sending, and the header lost its badge).
public class AgentPresenceTests
{
    [Fact]
    public void AStartIsAlwaysTaken() =>
        Assert.True(AgentPresence.EndsAgent("claude", null, null, "s1"));

    [Fact]
    public void ClaudeExitingEndsIt() =>
        Assert.True(AgentPresence.EndsAgent("", "s1", "prompt_input_exit", "s1"));

    [Fact]
    public void AClearDoesNot_ANewSessionFollows() =>
        Assert.False(AgentPresence.EndsAgent("", "s1", "clear", "s1"));

    [Fact]
    public void ALateEndOfAnOlderSessionDoesNot() =>
        Assert.False(AgentPresence.EndsAgent("", "s1", "other", "s2"));

    [Fact]
    public void AnOldHookWithoutDetailsStillEndsIt() =>
        Assert.True(AgentPresence.EndsAgent("", null, null, "s1"));
}
