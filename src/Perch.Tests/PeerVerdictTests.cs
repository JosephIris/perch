using PerchCli;
using Xunit;

namespace Perch.Tests;

/// The verdict on a bot's SendMessage. Every string here is a real response
/// captured from cc 2.1.259 while the team was running — the "No agent named"
/// one is the failure the old prose sniff read as a success, which put the
/// same message in the room three times (a failed attempt, a second failed
/// attempt, and the retry that worked).
public class PeerVerdictTests
{
    [Theory]
    // Delivered.
    [InlineData("{\"success\":true,\"message\":\"“HANDOFF: x” → joe\",\"msg_id\":\"3b91\"}", true)]
    // Escaped, as the hook's tool_response often arrives.
    [InlineData("\"{\\\"success\\\":true,\\\"message\\\":\\\"→ joe\\\"}\"", true)]
    // Ambiguous name: nothing was sent.
    [InlineData("{\"success\":false,\"message\":\"2 agents are named 'shabtay'. Re-send with the ref of the one you mean:\"}", false)]
    // The one the sniff got wrong.
    [InlineData("{\"success\":false,\"message\":\"No agent named 'joe [4910a2]' is reachable. Did you mean: joe, bo?\"}", false)]
    [InlineData("{\"success\":false,\"message\":\"Failed to send to joe.\"}", false)]
    // No flag at all: the prose sniff, biased toward true.
    [InlineData("\"Message delivered to ada\"", true)]
    [InlineData("\"No agent named 'ada' is reachable\"", false)]
    [InlineData("", true)]
    public void Ok_ReadsTheSuccessFlagFirst(string raw, bool expected)
        => Assert.Equal(expected, PeerVerdict.Ok(raw));

    [Fact]
    public void Ok_IsNotFooledByAWordInTheMessageBody()
    {
        // The body says "failed" — the send did not.
        Assert.True(PeerVerdict.Ok("{\"success\":true,\"message\":\"“REPORT: the build failed” → ada\"}"));
    }

    [Fact]
    public void Reason_IsTheFirstSentenceOfCcsOwnMessage()
    {
        var raw = "{\"success\":false,\"message\":\"No agent named 'joe [4910a2]' is reachable. "
                + "Did you mean: joe, bo?\\nUse ListAgents to see everyone you can message.\"}";
        Assert.Equal("No agent named 'joe [4910a2]' is reachable.", PeerVerdict.Reason(raw));
    }

    [Fact]
    public void Reason_ReadsAnEscapedResponse_AndIsCapped()
    {
        var raw = "\"{\\\"success\\\":false,\\\"message\\\":\\\"" + new string('x', 400) + "\\\"}\"";
        var reason = PeerVerdict.Reason(raw);
        Assert.True(reason.Length <= 161, reason.Length.ToString());
        Assert.EndsWith("…", reason);
    }

    [Fact]
    public void Reason_OfNothing_IsEmpty() => Assert.Equal("", PeerVerdict.Reason(""));
}
