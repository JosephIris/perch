using System.Linq;
using Xunit;

namespace Perch.Tests;

// A thread stopped on a permission prompt shows what it asks to do. That is
// the transcript's last tool call with no result yet — rows shaped the way
// Claude Code 2.1 writes them.
public class ThreadAskTests
{
    private static string Use(string id, string name, string input) =>
        $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"{{{id}}}","name":"{{{name}}}","input":{{{input}}}}]}}""";

    private static string Result(string id) =>
        $$$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"{{{id}}}","content":"ok"}]}}""";

    [Fact]
    public void ThePendingCallIsTheLastWithoutAResult()
    {
        var lines = new[]
        {
            "t\":\"torn row from the middle of the file\"}",
            Use("a", "Write", """{"file_path":"C:\\repo\\test_greet.py","content":"x"}"""),
            Result("a"),
            Use("b", "Bash", """{"command":"cd \"C:\\repo\" && python -m pytest","description":"Run tests"}"""),
        };
        Assert.Equal(("Bash", "python -m pytest"), ThreadController.PendingTool(lines));
        Assert.Null(ThreadController.PendingTool(lines.Append(Result("b"))));
        Assert.Null(ThreadController.PendingTool(new string[0]));
    }

    [Theory]
    [InlineData("Bash", "python -m pytest", "Run python -m pytest")]
    [InlineData("PowerShell", "", "Run a command")]
    [InlineData("Edit", "greet.py", "Edit greet.py")]
    [InlineData("WebFetch", "https://x.dev", "Fetch https://x.dev")]
    [InlineData("mcp__jira__search", "", "Use mcp__jira__search")]
    public void DescribesTheCall(string verb, string target, string expected) =>
        Assert.Equal(expected, ThreadController.DescribeTool(verb, target));

    // Each request's row in the chat keeps its own wording, so it must carry
    // the ask itself — the page tags "Thread 3 (Title)" as the thread.
    [Fact]
    public void AskNoticeSaysWhatThisRequestWas()
    {
        Assert.Equal("Thread 3 (Add tests) asks to: Run python -m pytest", ThreadController.AskNotice(3, "Add tests", "Run python -m pytest"));
        Assert.Equal("Thread 3 (Add tests) is waiting for your permission.", ThreadController.AskNotice(3, "Add tests", null));
    }

    [Fact]
    public void KickoffAsksForATaskListFirst() =>
        Assert.StartsWith("Start on the task in your brief. First write your plan as a task list", ThreadController.Kickoff);

    // A push's branch and remote go on git's command line: names only,
    // never an option or anything a shell would read.
    [Theory]
    [InlineData("main", true)]
    [InlineData("feature/login-fix_2", true)]
    [InlineData("--force", false)]
    [InlineData("-f", false)]
    [InlineData("main;rm", false)]
    [InlineData("a..b", false)]
    [InlineData("", false)]
    public void PushNamesAreNamesOnly(string name, bool ok) => Assert.Equal(ok, ThreadController.SafeRefName(name));

    [Fact]
    public void FirstLine_SkipsALeadingHeading()
    {
        Assert.Equal("Python 3.14 is installed.", ThreadController.FirstLine("## Results\n\n**Python 3.14** is installed.", 100));
        Assert.Equal("Only a heading", ThreadController.FirstLine("# Only a heading", 100));
    }
}
