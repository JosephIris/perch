using Xunit;

namespace Perch.Tests;

// The Overview's Stop button and "Steer this thread…" box, against a fake
// terminal: what reaches the thread's Claude, and what state it is left in.
public class ThreadSteeringTests
{
    private sealed class Fake
    {
        public readonly Session Thread = new() { ThreadOf = Guid.NewGuid(), ThreadNumber = 1 };
        public PaneNode Pane => Thread.Root;
        public bool Live = true;
        public readonly List<byte[]> Keys = new();
        public readonly List<string> Typed = new();
        public readonly List<Action> Timers = new();
        public readonly List<Guid> Escaped = new();
        public int Pushes;
        public long Now = 100_000;
        public readonly LineDelivery Delivery;
        public readonly ThreadSteering Steer;

        public Fake(AgentState state)
        {
            Pane.AgentType = "claude";
            Pane.AgentState = state;
            Delivery = new LineDelivery(new LineDelivery.Host
            {
                SessionById = id => id == Thread.Id ? Thread : null,
                ClaudeUp = _ => Live,
                Busy = _ => Pane.AgentState is AgentState.Working or AgentState.Permission or AgentState.Waiting,
                Type = (_, t) => { Typed.Add(t); return true; },
                PressEnter = _ => true,
                Delay = (a, _) => Timers.Add(a),
                EnsureRunning = _ => { },
            });
            Steer = new ThreadSteering(new ThreadSteering.Host
            {
                ClaudePane = _ => Live ? Pane : null,
                Write = (_, b) => Keys.Add(b),
                PushState = () => Pushes++,
                Escaped = id => Escaped.Add(id),
                NowMs = () => Now,
            }, Delivery);
        }

        public void Tick()
        {
            var due = Timers.ToList();
            Timers.Clear();
            foreach (var a in due) a();
        }

        public int Escapes => Keys.Count(k => k.Length == 1 && k[0] == 0x1b);
    }

    [Fact]
    public void Stop_OnAWorkingThread_EscapesOnce_AndItReadsStoppedAtOnce()
    {
        var f = new Fake(AgentState.Working);
        f.Pane.TurnStartUnixMs = 123;
        Assert.Equal(ThreadSteering.StopResult.Stopped, f.Steer.Stop(f.Thread));
        Assert.Equal(1, f.Escapes);
        // No hook reports an interrupt: without this the Overview kept
        // showing "Claude is working" and its Stop button.
        Assert.Equal(AgentState.Done, f.Pane.AgentState);
        Assert.True(f.Pane.StateInferred);
        Assert.Equal(0, f.Pane.TurnStartUnixMs);
        Assert.True(f.Pushes > 0);
        Assert.Equal(new[] { f.Pane.Id }, f.Escaped);
    }

    [Fact]
    public void Stop_ClickedTwice_SendsOneEscape()
    {
        // A second Escape on an idle prompt opens Claude Code's rewind menu.
        var f = new Fake(AgentState.Working);
        f.Steer.Stop(f.Thread);
        f.Pane.AgentState = AgentState.Working;   // a stale push: still reads working
        f.Now += 300;
        Assert.Equal(ThreadSteering.StopResult.AlreadyStopping, f.Steer.Stop(f.Thread));
        Assert.Equal(1, f.Escapes);
        f.Now += ThreadSteering.StopDebounceMs;
        Assert.Equal(ThreadSteering.StopResult.Stopped, f.Steer.Stop(f.Thread));
        Assert.Equal(2, f.Escapes);
    }

    [Fact]
    public void Stop_OnAnIdleOrMissingClaude_TypesNothing()
    {
        var f = new Fake(AgentState.Done);
        Assert.Equal(ThreadSteering.StopResult.NotBusy, f.Steer.Stop(f.Thread));
        f.Live = false;
        Assert.Equal(ThreadSteering.StopResult.NoClaude, f.Steer.Stop(f.Thread));
        Assert.Empty(f.Keys);
    }

    [Fact]
    public void Stop_LetsALineQueuedWhileItWorkedGoIn()
    {
        var f = new Fake(AgentState.Working);
        Assert.Equal(ThreadSteering.SendResult.Queued, f.Steer.Send(f.Thread, "use the other API"));
        Assert.Empty(f.Typed);
        f.Steer.Stop(f.Thread);
        Assert.Empty(f.Typed);            // not on the heels of the Escape
        f.Tick();
        Assert.Single(f.Typed);
        Assert.EndsWith("use the other API", f.Typed[0]);
    }

    [Fact]
    public void Send_ToAThreadWaitingOnAQuestion_DismissesItAndTheReplyGoesIn()
    {
        // The Overview says "It's waiting for your answer — reply in the box
        // below". Before: the reply waited for a Claude that isn't asking
        // anything, while the Claude waited for the reply. Forever.
        var f = new Fake(AgentState.Waiting);
        f.Thread.ThreadResolved = true;
        Assert.Equal(ThreadSteering.SendResult.AnsweredQuestion, f.Steer.Send(f.Thread, "Option B, please"));
        Assert.Equal(1, f.Escapes);
        Assert.False(f.Thread.ThreadResolved);
        Assert.Empty(f.Typed);            // the Escape lands first
        f.Tick();
        Assert.Single(f.Typed);
        Assert.EndsWith("Option B, please", f.Typed[0]);
    }

    [Fact]
    public void Send_ToAThreadOnAPermissionPrompt_WaitsForTheAnswer()
    {
        // Escape there would be a silent Deny; the card has the buttons.
        var f = new Fake(AgentState.Permission);
        Assert.Equal(ThreadSteering.SendResult.Queued, f.Steer.Send(f.Thread, "after that, run the tests"));
        Assert.Empty(f.Keys);
        Assert.Empty(f.Typed);
        Assert.Equal(new[] { "after that, run the tests" }, f.Delivery.Pending(f.Thread.Id));
    }

    [Fact]
    public void Send_ToAFreeThread_TypesAtOnce_AndBlankSendsNothing()
    {
        var f = new Fake(AgentState.Done);
        Assert.Equal(ThreadSteering.SendResult.Empty, f.Steer.Send(f.Thread, "   "));
        Assert.Empty(f.Typed);
        Assert.Equal(ThreadSteering.SendResult.Queued, f.Steer.Send(f.Thread, "carry on"));
        Assert.Single(f.Typed);
        Assert.Empty(f.Keys);
    }
}
