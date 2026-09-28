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
    public void PastedLine_EchoedInsideTheWrapper_IsConfirmed()
    {
        // A long line trips Claude Code's paste detection; the prompt-submit
        // hook then reports it wrapped. Seen live: every long line from the
        // project chat went in, was never confirmed, and was given up on.
        var f = new Fake();
        var s = f.D.Enqueue(f.Sess.Id, "a long steer");
        f.D.OnPromptSubmitted(f.Sess.Id, $"\n\n<pasted_content id=\"6ffc\">\n[Perch #{s}] a long steer");
        Assert.Equal(0, f.D.Queued(f.Sess.Id));

        Assert.True(LineDelivery.Echoes($"[Perch #{s}] x", s));
        Assert.True(LineDelivery.Echoes($"<pasted_content id=\"a1\">[Perch #{s}] x", s));
        Assert.False(LineDelivery.Echoes($"<pasted_content id=\"a1\">[Perch #{s + 1}] x", s));
        Assert.False(LineDelivery.Echoes("<pasted_content id=\"a1\">my own paste", s));
        Assert.False(LineDelivery.Echoes("<pasted_content id=\"a1", s));
        Assert.False(LineDelivery.Echoes(null, s));
    }

    [Fact]
    public void AfterAStop_ALineGluedBehindTheStoppedPrompt_IsStillConfirmed()
    {
        // Claude Code puts a stopped prompt back in the box; the next typed
        // line went in behind it as one prompt. Seen live: never confirmed,
        // Enter pressed at it, then a false "couldn't get a message into".
        var f = new Fake();
        var s = f.D.Enqueue(f.Sess.Id, "reply with DONE");
        f.D.OnPromptSubmitted(f.Sess.Id, $"[Perch #{s - 1}] Count to fifty slowly.[Perch #{s}] reply with DONE");
        Assert.Equal(0, f.D.Queued(f.Sess.Id));
        Assert.False(LineDelivery.Echoes("[Perch #10] x", 1), "#1 is not #10");
    }

    [Fact]
    public void NoEcho_NeverPressesEnterIntoAClaudeThatIsAsking()
    {
        // Typed, then the Claude stopped on a permission prompt before the
        // echo was seen: Enter there would be "Yes".
        var f = new Fake();
        f.D.Enqueue(f.Sess.Id, "hi");
        f.Busy = true;
        f.Tick(); f.Tick(); f.Tick();
        Assert.Equal(0, f.Enters);
    }

    [Fact]
    public void Pending_ListsWhatIsWaiting_AndSaysWhenItChanges()
    {
        var changed = 0;
        var f = new Fake { Busy = true };
        var d = new LineDelivery(new LineDelivery.Host
        {
            SessionById = id => id == f.Sess.Id ? f.Sess : null,
            ClaudeUp = _ => f.Up,
            Busy = _ => f.Busy,
            Type = (_, t) => { f.Typed.Add(t); return true; },
            PressEnter = _ => true,
            Delay = (a, t) => f.Timers.Add((a, t)),
            EnsureRunning = _ => { },
            Changed = _ => changed++,
        });
        var s1 = d.Enqueue(f.Sess.Id, "first\nline");
        d.Enqueue(f.Sess.Id, "second");
        Assert.Equal(new[] { "first  line", "second" }, d.Pending(f.Sess.Id));
        Assert.Equal(2, changed);
        Assert.Empty(f.Typed);

        f.Busy = false;
        d.OnFree(f.Sess.Id);
        f.Tick();
        Assert.Equal(3, changed);   // typed: no longer one to save for a restart
        d.OnPromptSubmitted(f.Sess.Id, $"[Perch #{s1}] first  line");
        Assert.Equal(new[] { "second" }, d.Pending(f.Sess.Id));
        Assert.Equal(4, changed);
    }

    [Fact]
    public void Enqueue_WithoutPump_WaitsForTheNextFreeMoment()
    {
        var f = new Fake();
        f.D.Enqueue(f.Sess.Id, "hi", pumpNow: false);
        Assert.Empty(f.Typed);
        f.D.OnFree(f.Sess.Id);
        f.Tick();
        Assert.Single(f.Typed);
    }

    [Fact]
    public void LongMessage_DeliveredAsPaste_NoGiveUp_NoCut_NoDuplicate()
    {
        // The live failure: the project chat sent ~1,200 characters, Claude
        // Code lost the first ~1,000 of the typed line, the thread answered
        // "your message was cut off" and the chat resent it, split, twice.
        // Now the typed line stays short and points at the whole message.
        var saved = new List<string>();
        var f = new Fake();
        f.D = new LineDelivery(new LineDelivery.Host
        {
            SessionById = id => id == f.Sess.Id ? f.Sess : null,
            ClaudeUp = _ => f.Up,
            Busy = _ => f.Busy,
            Type = (_, t) => { f.Typed.Add(t); return true; },
            PressEnter = _ => { f.Enters++; return true; },
            Delay = (a, t) => f.Timers.Add((a, t)),
            EnsureRunning = _ => { },
            GaveUp = (_, t) => f.GaveUp.Add(t),
            SaveLong = (_, text) => { saved.Add(text); return @"C:\perch\threads\x\message-1.md"; },
        });
        var body = "From the project chat: Joseph: dig into payer_score harder. " + string.Join(" ", Enumerable.Repeat("check the dictionary rule", 50))
                   + " Report the cause and the new match count on all 41 bids. No feature code yet.";
        Assert.True(body.Length > 1100);
        var s = f.D.Enqueue(f.Sess.Id, body);

        var typed = Assert.Single(f.Typed);
        Assert.StartsWith($"[Perch #{s}] From the project chat: Joseph: dig into payer_score harder.", typed);
        Assert.True(typed.Length <= LineDelivery.MaxChars, $"typed {typed.Length} chars");
        Assert.Contains(@"C:\perch\threads\x\message-1.md", typed);
        Assert.Equal(body, Assert.Single(saved));          // nothing of it is lost

        // Claude Code submits it wrapped as a paste; that is a delivery.
        f.D.OnPromptSubmitted(f.Sess.Id, $"\n\n<pasted_content id=\"6ffc\">\n[Perch #{s}] From the project chat: Jos");
        for (int i = 0; i < 8; i++) { f.Tick(); f.D.OnFree(f.Sess.Id); }
        Assert.Empty(f.GaveUp);
        Assert.Equal(0, f.Enters);
        Assert.Single(f.Typed);                           // never typed twice
        Assert.Equal(0, f.D.Queued(f.Sess.Id));
    }

    [Fact]
    public void LongMessage_NowhereToKeepIt_IsMarkedCut_NotSilentlyCut()
    {
        var f = new Fake();                                // no SaveLong
        f.D.Enqueue(f.Sess.Id, new string('x', 2000));
        var typed = Assert.Single(f.Typed);
        Assert.EndsWith("…", typed);
        Assert.True(typed.Length <= LineDelivery.MaxChars + 20);
    }

    [Fact]
    public void PointerLine_KeepsTheOpeningWords_AndStaysShort()
    {
        var one = string.Join(" ", Enumerable.Repeat("word", 400));
        var p = LineDelivery.PointerLine(one, @"C:\a\b.md");
        Assert.StartsWith("word word", p);
        Assert.Contains(@"C:\a\b.md", p);
        Assert.True(p.Length < LineDelivery.PreviewChars + 200);
        Assert.DoesNotContain("wor…", p);                  // cut at a word
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
        Assert.Equal("Done. Added farewell().", ThreadController.FirstLine("**Done.** Added `farewell()`.", 50));
    }
}
