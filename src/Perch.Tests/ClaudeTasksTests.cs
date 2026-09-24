using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Perch.Tests;

// A thread's progress in the Overview comes from its Claude's task files —
// the exact shape Claude Code 2.1 writes under ~/.claude/tasks/<session>/.
// And the chat's composer names the model from the run's init line.
public class ClaudeTasksTests
{
    private static string Dir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"perch-tasks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Task(string dir, string id, string subject, string status, string activeForm = "") =>
        File.WriteAllText(Path.Combine(dir, id + ".json"),
            $$"""{"id":"{{id}}","subject":"{{subject}}","description":"x","activeForm":"{{activeForm}}","status":"{{status}}","blocks":[],"blockedBy":[]}""");

    [Fact]
    public void ReadsTasksInOrderAndSumsThemUp()
    {
        var d = Dir();
        try
        {
            Task(d, "10", "Tenth", "pending");
            Task(d, "2", "Second", "in_progress", "Running the tests");
            Task(d, "1", "First", "completed");
            File.WriteAllText(Path.Combine(d, "3.json"), "{not json");
            Task(d, "4", "Deleted one", "deleted");
            var items = ClaudeTasks.ReadDir(d);
            Assert.Equal(new[] { "First", "Second", "Tenth" }, items.Select(i => i.Subject));
            Assert.Equal((1, 3, "Running the tests"), ClaudeTasks.Summary(items));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void NoFolderOrNoSessionIsNoTasks()
    {
        Assert.Empty(ClaudeTasks.ReadDir(Path.Combine(Path.GetTempPath(), $"perch-none-{Guid.NewGuid():N}")));
        Assert.Empty(ClaudeTasks.Read(null));
        Assert.Empty(ClaudeTasks.Read("../../etc"));
        Assert.Equal((0, 0, ""), ClaudeTasks.Summary(Array.Empty<ClaudeTasks.Item>()));
    }

    [Fact]
    public void InProgressWithoutActiveFormFallsBackToSubject()
    {
        var items = new[] { new ClaudeTasks.Item("1", "Fix the bug", "", "in_progress") };
        Assert.Equal((0, 1, "Fix the bug"), ClaudeTasks.Summary(items));
    }

    [Fact]
    public void ParseLine_ReportsTheModelFromInit()
    {
        var rows = ChatController.ParseLine("""{"type":"system","subtype":"init","model":"claude-opus-5-5","session_id":"s"}""", out var isResult, out _, out var model);
        Assert.Empty(rows);
        Assert.False(isResult);
        Assert.Equal("claude-opus-5-5", model);
        ChatController.ParseLine("""{"type":"assistant","message":{"content":[]}}""", out _, out _, out var none);
        Assert.Null(none);
    }

    [Theory]
    [InlineData("joseph", "Joseph")]
    [InlineData("Dana Levi", "Dana")]
    [InlineData("josep.k", "Josep")]
    [InlineData("  ", "")]
    public void FirstName(string raw, string expected) => Assert.Equal(expected, ChatController.FirstName(raw));
}
