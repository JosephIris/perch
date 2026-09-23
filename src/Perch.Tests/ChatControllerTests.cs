using Xunit;

namespace Perch.Tests;

// How a project chat reads a headless run's stream-json: each line becomes the
// rows the conversation shows, and the final `result` line is what says the
// turn completed (or failed) — without it the chat reports an error.
public class ChatControllerTests
{
    [Fact]
    public void AssistantLine_TextAndToolBecomeRows()
    {
        var line = """{"type":"assistant","message":{"content":[{"type":"text","text":"Starting two threads."},{"type":"tool_use","name":"Bash","input":{"command":"perch thread new \"A\" --brief \"x\""}},{"type":"tool_use","name":"Read","input":{"file_path":"C:\\repo\\src\\app.ts"}}]}}""";
        var rows = ChatController.ParseLine(line, out var isResult, out var err);
        Assert.False(isResult);
        Assert.Null(err);
        Assert.Equal(3, rows.Count);
        Assert.Equal(("claude", "Starting two threads.", ""), rows[0]);
        Assert.Equal("tool", rows[1].Kind);
        Assert.Equal("Bash", rows[1].Tool);
        Assert.StartsWith("perch thread new", rows[1].Text);
        Assert.Equal(("tool", "app.ts", "Read"), rows[2]);
    }

    [Fact]
    public void ResultLine_MarksTheTurn_AndCarriesAnError()
    {
        Assert.Empty(ChatController.ParseLine("""{"type":"result","subtype":"success","is_error":false,"result":"done"}""", out var ok, out var none));
        Assert.True(ok);
        Assert.Null(none);
        ChatController.ParseLine("""{"type":"result","subtype":"error_during_execution","is_error":true,"result":""}""", out var bad, out var err);
        Assert.True(bad);
        Assert.Equal("error_during_execution", err);
    }

    [Fact]
    public void OtherLines_AndGarbage_AddNothing()
    {
        Assert.Empty(ChatController.ParseLine("""{"type":"system","subtype":"init","session_id":"x"}""", out _, out _));
        Assert.Empty(ChatController.ParseLine("""{"type":"user","message":{"content":[{"type":"tool_result","content":"ok"}]}}""", out _, out _));
        Assert.Empty(ChatController.ParseLine("not json", out var r, out _));
        Assert.False(r);
    }

    [Fact]
    public void CoordinatorMayReadAndRunThreads_ButNotEdit()
    {
        Assert.Contains("Read", ChatController.AllowedTools);
        Assert.Contains("Bash(perch thread:*)", ChatController.AllowedTools);
        Assert.Contains("PowerShell(perch thread:*)", ChatController.AllowedTools);
        Assert.Contains("PowerShell(git commit:*)", ThreadController.ThreadAllowedTools);
        Assert.DoesNotContain("Edit", ChatController.AllowedTools);
        Assert.DoesNotContain("Write", ChatController.AllowedTools);
        Assert.DoesNotContain(ChatController.AllowedTools, t => t == "Bash");
    }
}
