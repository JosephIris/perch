using Xunit;

namespace Perch.Tests;

// The rules that decide whether a line typed into a Claude actually lands:
// only when it is up and free, confirmed by the prompt-submit echo, Enter
// pressed again on a miss, and NEVER typed twice.
public class LineDeliveryTests
{
    private sealed class Fake
    {
        public readonly Session Sess = new();
        public bool Up = true, Busy;
        public readonly List<string> Typed = new();
        public int Enters, Ensures;
        public readonly List<string> GaveUp = new();
        public readonly List<(Action Act, TimeSpan After)> Timers = new();
        public LineDelivery D = null!;

        public Fake()
        {
            D = new LineDelivery(new LineDelivery.Host
            {
                SessionById = id => id == Sess.Id ? Sess : null,
                ClaudeUp = _ => Up,
                Busy = _ => Busy,
                Type = (_, t) => { Typed.Add(t); return true; },
                PressEnter = _ => { Enters++; return true; },
                Delay = (a, t) => Timers.Add((a, t)),
                EnsureRunning = _ => Ensures++,
                GaveUp = (_, t) => GaveUp.Add(t),
            });
        }

        /// Run every timer that is due (all of them, one round).
        public void Tick()
        {
            var due = Timers.ToList();
            Timers.Clear();
            foreach (var (a, _) in due) a();
        }
    }

    [Fact]
    public void TypesWhenFree_ConfirmedByEcho_NextWaitsForTheTurn()
    {
        var f = new Fake();
        var s1 = f.D.Enqueue(f.Sess.Id, "first");
        f.D.Enqueue(f.Sess.Id, "second");
        Assert.Equal(new[] { "[Perch #" + s1 + "] first" }, f.Typed);

        f.D.OnPromptSubmitted(f.Sess.Id, $"[Perch #{s1}] first");
        f.Busy = true;          // Claude is working on it
        f.Tick(); f.Tick();
        Assert.Single(f.Typed);

        f.Busy = false;
        f.D.OnFree(f.Sess.Id);
        f.Tick();
        Assert.Equal(2, f.Typed.Count);
        Assert.EndsWith("second", f.Typed[1]);
        Assert.Equal(0, f.Enters);
    }

    [Fact]
    public void BusyOrDown_WaitsInsteadOfTyping()
    {
        var f = new Fake { Busy = true };
        f.D.Enqueue(f.Sess.Id, "hi");
        Assert.Empty(f.Typed);

        f.Busy = false; f.Up = false;
        f.D.OnFree(f.Sess.Id);
        f.Tick();
        Assert.Empty(f.Typed);
        Assert.Equal(1, f.Ensures);   // asked the tab to start

        f.Up = true;
        f.D.OnAgentUp(f.Sess.Id);
        f.Tick();
        Assert.Single(f.Typed);
    }

    [Fact]
    public void NoEcho_PressesEnterAgain_NeverRetypes_ThenGivesUp()
    {
        var f = new Fake();
        f.D.Enqueue(f.Sess.Id, "hi");
        Assert.Single(f.Typed);
        f.Tick();                // 2s: Enter
        f.Tick();                // 3s: Enter
        f.Tick();                // 10s: held, retry scheduled
        Assert.Equal(2, f.Enters);

        for (int i = 0; i < LineDelivery.HoldLimit; i++)
        {
            f.Tick();            // the retry pump: Enter, not a second typing
            f.Tick(); f.Tick(); f.Tick();
        }
        Assert.Single(f.Typed);
        f.Tick();
        Assert.Single(f.GaveUp);
        Assert.Equal(0, f.D.Queued(f.Sess.Id));
    }

    [Fact]
    public void EchoOfAnotherLine_DoesNotConfirm()
    {
        var f = new Fake();
        var s = f.D.Enqueue(f.Sess.Id, "hi");
        f.D.OnPromptSubmitted(f.Sess.Id, $"[Perch #{s + 1}] something else");
        f.D.OnPromptSubmitted(f.Sess.Id, "a thing the user typed");
        Assert.Equal(1, f.D.Queued(f.Sess.Id));
        f.D.OnPromptSubmitted(f.Sess.Id, $"[Perch #{s}] hi");
        Assert.Equal(0, f.D.Queued(f.Sess.Id));
    }

    [Fact]
    public void Flatten_OneLine_Capped()
    {
        Assert.Equal("a  b  c", LineDelivery.Flatten("a\r\n\nb\nc\n"));
        Assert.True(LineDelivery.Flatten(new string('x', 5000)).Length <= LineDelivery.MaxChars + 1);
    }

    [Fact]
    public void ThreadPrompt_CarriesTheBriefAndTheReportRule()
    {
        var p = ThreadController.ThreadPrompt(3, "Fix login", "Make the login page load.");
        Assert.Contains("thread 3", p);
        Assert.Contains("Make the login page load.", p);
        Assert.Contains("perch thread send lead", p);
        Assert.Equal("First line", ThreadController.FirstLine("\n  First line\nsecond", 50));
    }
}
