using Xunit;

namespace Perch.Tests;

// A thread whose conversation has grown long takes its next piece of work in
// a fresh Claude session, handed its brief, its last report and where the old
// conversation is — the way Claude's own project chats let a thread "continue
// in a fresh session" instead of re-reading everything on every call.
public class ThreadFreshTests
{
    [Fact]
    public void BriefFromPrompt_TakesTheBriefAndNotAnEarlierLeftOff()
    {
        var prompt = ThreadController.ThreadPrompt(3, "Fix login", "Make the login form\nreject empty passwords.")
                   + ThreadController.LeftOff("Done: it rejects them.", @"C:\old.jsonl");
        Assert.Equal("Make the login form\nreject empty passwords.", ThreadController.BriefFromPrompt(prompt));
        // The prompt's own line breaks follow the source file's (CRLF on a
        // Windows checkout); either way the brief comes out the same.
        Assert.Equal("Make the login form\nreject empty passwords.", ThreadController.BriefFromPrompt(prompt.Replace("\r\n", "\n").Replace("\n", "\r\n")));
        Assert.Equal("", ThreadController.BriefFromPrompt("no brief here"));
    }

    [Fact]
    public void LeftOff_SaysWhereTheOldConversationIsAndWhatItReported()
    {
        var text = ThreadController.LeftOff("Done: it rejects them.", @"C:\old.jsonl");
        Assert.Contains("fresh session", text);
        Assert.Contains(@"C:\old.jsonl", text);
        Assert.Contains("Done: it rejects them.", text);
        Assert.DoesNotContain("saved at", ThreadController.LeftOff("x", null));
        Assert.Contains(new string('r', 4000) + "…", ThreadController.LeftOff(new string('r', 9000), null));
    }

    [Fact]
    public void Delivery_WaitsForTheFreshSessionBeforeTyping()
    {
        var sess = new Session();
        var typed = new List<string>();
        var fresh = 0;
        var timers = new List<Action>();
        var d = new LineDelivery(new LineDelivery.Host
        {
            SessionById = id => id == sess.Id ? sess : null,
            ClaudeUp = _ => true,
            Busy = _ => false,
            Type = (_, t) => { typed.Add(t); return true; },
            PressEnter = _ => true,
            Delay = (a, _) => timers.Add(a),
            EnsureRunning = _ => { },
            // Starts over once, then the new Claude is the one typed into.
            StartFresh = _ => fresh++ == 0,
        });
        d.Enqueue(sess.Id, "next task");
        Assert.Empty(typed);
        Assert.Equal(1, fresh);

        d.OnAgentUp(sess.Id);   // the fresh Claude came up
        foreach (var a in timers.ToList()) a();
        Assert.Single(typed);
        Assert.EndsWith("next task", typed[0]);
    }
}
