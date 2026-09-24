using System.Linq;
using Xunit;

namespace Perch.Tests;

// A project chat's coordinator re-reads its whole conversation every turn.
// Past ChatController.RotateAtTokens the next turn starts a fresh one, handed
// the recent messages, the threads and where the full history is — so how
// big the conversation is, and what the handover says, are the product.
public class ChatRotationTests
{
    [Fact]
    public void ParseLine_MeasuresTheConversationFromAnAssistantCall()
    {
        ChatController.ParseLine(
            """{"type":"assistant","message":{"content":[{"type":"text","text":"hi"}],"usage":{"input_tokens":8,"cache_creation_input_tokens":520,"cache_read_input_tokens":42531,"output_tokens":110}}}""",
            out _, out _, out _, out var context);
        Assert.Equal(8 + 520 + 42531, context);
        ChatController.ParseLine("""{"type":"result","subtype":"success","is_error":false,"result":"x","usage":{"input_tokens":99999}}""",
            out _, out _, out _, out var none);
        Assert.Equal(0, none);   // the result line sums the whole turn — not a size
    }

    private static ChatController.ChatEntry E(string kind, string text) => new("id", kind, text, "", 0);

    [Fact]
    public void Handoff_CarriesRecentMessagesThreadsAndWhereTheRestIs()
    {
        var entries = Enumerable.Range(1, 40).Select(i => E(i % 2 == 0 ? "user" : "claude", $"message {i}"))
            .Append(E("tool", "git log"))
            .Append(E("thread", "Fix the login bug"))
            .Append(E("notice", "Thread 3 (Fix the login bug) finished its turn: \"Done.\""))
            .ToList();
        var text = ChatController.Handoff(entries, "3. Fix the login bug — idle — branch fix-login\n", @"C:\data\chat.jsonl");

        Assert.StartsWith("[Perch] You are continuing this project chat in a fresh conversation", text);
        Assert.Contains(@"C:\data\chat.jsonl", text);
        Assert.Contains("(You started a thread: Fix the login bug)", text);
        Assert.Contains("Perch: Thread 3 (Fix the login bug) finished its turn", text);
        Assert.Contains("3. Fix the login bug — idle — branch fix-login", text);
        Assert.DoesNotContain("git log", text);                    // tool rows stay out
        Assert.Contains("User: message 40", text);
        Assert.DoesNotContain("message 10\n", text);               // only the last rows
        Assert.True(text.IndexOf("message 30") < text.IndexOf("message 40"), "oldest first");
    }

    // The coordinator's shell commands pass Perch's push guard first, so a
    // `git push` ends at the approval card, whatever the model remembers.
    [Fact]
    public void GuardSettings_RunPerchBeforeEveryShellCommand()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(ChatController.GuardSettingsJson(@"C:\Perch\tools\perch.exe"));
        var group = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0];
        Assert.Equal("Bash|PowerShell", group.GetProperty("matcher").GetString());
        Assert.Equal("\"C:\\Perch\\tools\\perch.exe\" hooks claude coordinator-pre-bash",
            group.GetProperty("hooks")[0].GetProperty("command").GetString());
    }

    [Fact]
    public void Handoff_ClipsAVeryLongMessage()
    {
        var text = ChatController.Handoff(new[] { E("user", new string('x', 5000)) }, "", "log");
        Assert.Contains(new string('x', 1200) + "…", text);
        Assert.DoesNotContain(new string('x', 1201), text);
    }
}
